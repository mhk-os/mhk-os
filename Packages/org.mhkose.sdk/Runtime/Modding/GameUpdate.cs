using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Whether a newer game exists: a numbered build (<see cref="BuildInfo.Number"/> above 0) reads the newest releases of
    /// the game's repository, named "Build &lt;n&gt; | &lt;sha7&gt;", and takes the highest number (release runs of quick
    /// pushes publish in any order, so GitHub's "latest" release need not be the newest build). It checks at start, then
    /// every <see cref="RecheckSeconds"/> while the game runs, and <see cref="RetrySeconds"/> after a failed check
    /// (offline, API rate limit). Dev builds (number 0) never check.
    /// </summary>
    public static class GameUpdate
    {
        public const string Repo = "mhk-os/mhk-os";
        const string CacheKey = Repo + ".releases";
        const int ReleasesPerPage = 5;
        const float RecheckSeconds = 60f * 60f;
        const float RetrySeconds = 10f * 60f;

        static readonly Regex BuildName = new(@"^\s*Build\s+(\d+)", RegexOptions.CultureInvariant);

        [AutoStaticsCleanup] static int latestNumber;
        [AutoStaticsCleanup] static string releaseUrl;

        /// <summary>The newest release has a higher build number than this game.</summary>
        public static bool Available => latestNumber > BuildInfo.Number && BuildInfo.Number > 0 && !string.IsNullOrEmpty(releaseUrl);

        /// <summary>Web page of the newest release (its html_url), null before a check found one.</summary>
        public static string ReleaseUrl => releaseUrl;

        /// <summary>Build number of the newest release, 0 while unknown.</summary>
        public static int LatestNumber => latestNumber;

        /// <summary>A check found another newest release (main thread).</summary>
        public static event Action Changed;

        [Serializable]
        sealed class Release
        {
            public string name, html_url;
            public bool draft, prerelease;
        }

        [Serializable]
        sealed class ReleaseList
        {
            public Release[] items;
        }

        /// <summary>Starts the checks of this game run (called by <see cref="ModSources"/> at game start).</summary>
        internal static void Check(CancellationToken ct)
        {
            if (BuildInfo.Number <= 0) return;
            CheckRepeatedlyAsync(ct).LogExceptionsAndForget();
        }

        static async Awaitable CheckRepeatedlyAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    var done = await CheckOnceAsync(ct);
                    // Real time: a paused race (time scale 0) must not stop the checks.
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(done ? RecheckSeconds : RetrySeconds), ct);
                    await Awaitable.MainThreadAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        // False when the check failed and is retried sooner.
        static async Awaitable<bool> CheckOnceAsync(CancellationToken ct)
        {
            var cache = ModSources.RepoCache.Load(CacheKey);
            try
            {
                var body = await ModSources.FetchAsync($"{ModSources.ApiBase}/repos/{Repo}/releases?per_page={ReleasesPerPage}", cache, ct);
                cache.Save(CacheKey);
                var (number, url) = Newest(JsonUtility.FromJson<ReleaseList>("{\"items\":" + body + "}"));
                if (number <= 0)
                {
                    Debug.LogWarning("[GameUpdate] No release named \"Build <n>\" found.");
                    return true;
                }
                if (number == latestNumber && url == releaseUrl) return true;
                latestNumber = number;
                releaseUrl = url;
                Debug.Log($"[GameUpdate] Newest release build {number} ({url}), this is build {BuildInfo.Number}: {(Available ? "update available" : "up to date")}.");
                Changed?.Invoke();
                return true;
            }
            catch (ModSources.SourceException e)
            {
                Debug.Log($"[GameUpdate] Check failed: {e.Message}");
                return false;
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"[GameUpdate] Unexpected answer: {e.Message}");
                return false;
            }
        }

        // The published release with the highest build number, (0, null) when none is named "Build <n>".
        static (int number, string url) Newest(ReleaseList list)
        {
            var newest = (number: 0, url: (string)null);
            if (list?.items == null) return newest;
            foreach (var r in list.items)
            {
                if (r == null || r.draft || r.prerelease || string.IsNullOrEmpty(r.html_url)) continue;
                var match = BuildName.Match(r.name ?? "");
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > newest.number)
                    newest = (n, r.html_url);
            }
            return newest;
        }
    }
}
