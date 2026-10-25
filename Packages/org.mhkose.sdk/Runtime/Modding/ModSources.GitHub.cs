using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace MHK.Modding
{
    public static partial class ModSources
    {
        const int ReleasesPerPage = 10;
        const int RequestTimeoutSeconds = 20;
        const int DefaultRateLimitMinutes = 10;
        const string ProjectUrl = "https://github.com/mhk-os/mhk-os";

        [AutoStaticsCleanup] static DateTime rateLimitedUntil;

        internal static string ApiBase
        {
            get
            {
                var api = CommandLine.Value(ApiOption);
                if (string.IsNullOrWhiteSpace(api)) api = EditorApiBase?.Invoke();
                return (string.IsNullOrWhiteSpace(api) ? DefaultApi : api.Trim()).TrimEnd('/');
            }
        }

        static string UserAgent => $"MoorhuhnKartOSE/{BuildInfo.Commit} (+{ProjectUrl})";

        static UnityWebRequest Request(string url, bool api)
        {
            var r = UnityWebRequest.Get(url);
            r.SetRequestHeader("User-Agent", UserAgent);
            r.SetRequestHeader("Accept", api ? "application/vnd.github+json" : "application/octet-stream");
            if (api) r.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
            r.timeout = RequestTimeoutSeconds;
            return r;
        }

        // Cancelling aborts the request. progress runs once a frame while the request runs.
        static async Awaitable SendAsync(UnityWebRequest r, Action progress, CancellationToken ct)
        {
            var op = r.SendWebRequest();
            try
            {
                while (!op.isDone)
                {
                    progress?.Invoke();
                    await Awaitable.NextFrameAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                r.Abort();
                throw;
            }
        }

        static async Awaitable<GitHubRelease[]> FetchReleasesAsync(string repo, RepoCache cache, CancellationToken ct) =>
            ParseReleases(await FetchAsync($"{ApiBase}/repos/{repo}/releases?per_page={ReleasesPerPage}", cache, ct));

        /// <summary>
        /// The body of the API answer at <paramref name="url"/>. A request costs one of the 60 per hour, an unchanged answer
        /// comes back as 304 to the ETag of the last one, kept with its body in <paramref name="cache"/>.
        /// </summary>
        internal static async Awaitable<string> FetchAsync(string url, RepoCache cache, CancellationToken ct)
        {
            if (DateTime.UtcNow < rateLimitedUntil) throw RateLimited();
            using var r = Request(url, true);
            var cached = !string.IsNullOrEmpty(cache.etag) && !string.IsNullOrEmpty(cache.body);
            if (cached) r.SetRequestHeader("If-None-Match", cache.etag);
            await SendAsync(r, null, ct);
            if (cached && r.responseCode == 304) return cache.body;
            ThrowOnFailure(r);
            cache.etag = r.GetResponseHeader("ETag");
            cache.body = r.downloadHandler.text;
            return cache.body;
        }

        static GitHubRelease[] ParseReleases(string body)
        {
            try
            {
                var list = JsonUtility.FromJson<ReleaseList>("{\"items\":" + body + "}");
                if (list?.items != null) return list.items;
            }
            catch (ArgumentException)
            {
            }
            throw new SourceException(State.Error, "GitHub sent an unexpected answer.");
        }

        // mod.json files are small and never change under their asset id: each is downloaded once.
        static async Awaitable<string> ManifestJsonAsync(GitHubAsset asset, RepoCache cache, CancellationToken ct)
        {
            var known = cache.manifests.Find(m => m.assetId == asset.id);
            if (known != null) return known.json;
            using var r = Request(asset.browser_download_url, false);
            await SendAsync(r, null, ct);
            ThrowOnFailure(r);
            var json = r.downloadHandler.text;
            cache.manifests.Add(new CachedManifest { assetId = asset.id, json = json });
            return json;
        }

        static void ThrowOnFailure(UnityWebRequest r)
        {
            if (r.result == UnityWebRequest.Result.Success) return;
            if (r.result == UnityWebRequest.Result.ConnectionError) throw new SourceException(State.Offline, "No connection to GitHub.");
            var code = r.responseCode;
            if (code == 429 || (code == 403 && r.GetResponseHeader("X-RateLimit-Remaining") == "0"))
            {
                rateLimitedUntil = RateLimitReset(r);
                throw RateLimited();
            }
            if (code == 404) throw NotFound();
            if (code > 0) throw new SourceException(State.Error, Invariant($"GitHub answered HTTP {code}."));
            throw new SourceException(State.Error, OneLine(r.error));
        }

        static DateTime RateLimitReset(UnityWebRequest r)
        {
            if (long.TryParse(r.GetResponseHeader("X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            if (int.TryParse(r.GetResponseHeader("Retry-After"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return DateTime.UtcNow.AddSeconds(seconds);
            return DateTime.UtcNow.AddMinutes(DefaultRateLimitMinutes);
        }

        static SourceException NotFound() => new(State.Error, "Not found on GitHub.");

        static SourceException RateLimited() =>
            new(State.Error, $"GitHub rate limit reached, checks work again at {rateLimitedUntil.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}.");

        [Serializable]
        sealed class ReleaseList
        {
            public GitHubRelease[] items;
        }

        /// <summary>The fields of a GitHub release the game reads (JSON names of the GitHub API).</summary>
        [Serializable]
        internal sealed class GitHubRelease
        {
            public string tag_name;
            public bool draft, prerelease;
            public GitHubAsset[] assets;

            public GitHubAsset Asset(string name)
            {
                if (string.IsNullOrEmpty(name) || assets == null) return null;
                foreach (var a in assets)
                    if (a != null && a.name == name && !string.IsNullOrEmpty(a.browser_download_url)) return a;
                return null;
            }
        }

        [Serializable]
        internal sealed class GitHubAsset
        {
            public long id, size;
            public string name, browser_download_url;
        }

        [Serializable]
        internal sealed class CachedManifest
        {
            public long assetId;
            public string json;
        }

        /// <summary>What the last check of a repository fetched: ETag and body of the API answer, mod.json files by asset id, pack texts.</summary>
        [Serializable]
        internal sealed class RepoCache
        {
            public string etag, body, packId, title, description;
            public Kind kind;
            public List<CachedManifest> manifests = new();

            static string PathOf(string repo) => Path.Combine(DataPaths.Root, "Cache", "ModSources", repo.Replace('/', '_') + ".json");

            public static RepoCache Load(string repo)
            {
                var cache = ReadJson<RepoCache>(PathOf(repo)) ?? new RepoCache();
                cache.manifests ??= new List<CachedManifest>();
                return cache;
            }

            public void Remember(Source s)
            {
                packId = s.packId;
                kind = s.kind;
                title = s.title;
                description = s.description;
            }

            public void Save(string repo)
            {
                try
                {
                    SafeFile.WriteAllText(PathOf(repo), JsonUtility.ToJson(this));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"[ModSources] Cannot write the cache of {repo}: {e.Message}");
                }
            }
        }

        /// <summary>Reads "owner/name" from a GitHub link (https, .git, a trailing slash or deeper paths), <c>github.com/owner/name</c> or <c>owner/name</c>.</summary>
        internal static class RepoLink
        {
            static readonly Regex Owner = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.CultureInvariant);
            static readonly Regex Name = new("^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

            public static bool TryParse(string input, out string repo)
            {
                repo = null;
                if (string.IsNullOrWhiteSpace(input)) return false;
                var s = input.Trim();
                if (s.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) s = s.Substring("git@github.com:".Length);
                var scheme = s.IndexOf("://", StringComparison.Ordinal);
                if (scheme >= 0) s = s.Substring(scheme + 3);
                var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var first = 0;
                if (parts.Length > 0 && parts[0].Contains("."))
                {
                    if (!IsGitHubHost(parts[0])) return false;
                    first = 1;
                }
                if (parts.Length < first + 2) return false;
                var owner = parts[first];
                var name = parts[first + 1].Split('?', '#')[0];
                if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                if (!Owner.IsMatch(owner) || !Name.IsMatch(name) || name == "." || name == "..") return false;
                repo = owner + "/" + name;
                return true;
            }

            static bool IsGitHubHost(string host) =>
                string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase);
        }
    }
}
