using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using MHK.Core;
using UnityEngine;

namespace MHK.Modding
{
    public static partial class ModSources
    {
        /// <summary>The release description every mod release carries, kept in the installed mod folder too.</summary>
        public const string ManifestFile = "mod.json";
        /// <summary>Repository and release tag of a mod this game installed. Folders without it are manual installs and never updated automatically.</summary>
        public const string SourceFile = "source.json";

        const float RecheckSeconds = 60f;
        const float ProgressSeconds = 0.1f;
        const int MaxErrorLength = 120;

        static TimeSpan CheckInterval
        {
            get
            {
                var root = GameRoot.Instance;
                return TimeSpan.FromMinutes(root != null && root.Config != null ? root.Config.modCheckIntervalMinutes : 60f);
            }
        }

        /// <summary>A compatible release: its zip for this platform, mod.json of a mod, pack.json (when published) of an asset pack.</summary>
        internal sealed class Release
        {
            public Kind kind;
            public string tag, commit, manifestJson, zipUrl;
            public ModManifest manifest;
            public PackManifest pack;
            public long zipId, zipSize;
        }

        /// <summary>A check or install failed: the state to show and a one-line reason.</summary>
        sealed class SourceException : Exception
        {
            public readonly State state;
            public SourceException(State state, string message) : base(message) => this.state = state;
        }

        static async Awaitable CheckAsync(Source s, CancellationToken ct, bool allowCached = false)
        {
            if (s == null || s.busy) return;
            s.busy = true;
            SetState(s, State.Checking, null);
            try
            {
                var cache = RepoCache.Load(s.repo);
                var cached = allowCached && cache.IsFresh(CheckInterval);
                if (cached && cache.missing) throw NotFound();
                var releases = cached ? ParseReleases(cache.body) : await FetchReleasesAsync(s.repo, cache, ct);
                if (!cached) cache.checkedUtc = DateTime.UtcNow.Ticks;
                await ChooseAsync(s, releases, cache, ct);
                cache.Remember(s);
                cache.Save(s.repo);
                s.checkedAt = Time.realtimeSinceStartup;
                RefreshInstalled(s);
                SetEvaluated(s);
                Debug.Log($"[ModSources] {s.repo}: {s.state}, installed {s.installedCommit ?? "none"}, latest {s.latestCommit ?? "none"}" +
                          (cached ? " (cached, checked within the interval)." : "."));
            }
            catch (SourceException e)
            {
                Debug.Log($"[ModSources] {s.repo}: {e.state} ({e.Message})");
                SetState(s, e.state, e.Message);
            }
            catch (OperationCanceledException)
            {
                SetState(s, State.Unknown, null);
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModSources] Check of {s.repo} failed: {e}");
                SetState(s, State.Error, OneLine(e.Message));
            }
            finally
            {
                s.busy = false;
            }
        }

        static async Awaitable ChooseAsync(Source s, GitHubRelease[] releases, RepoCache cache, CancellationToken ct)
        {
            s.latest = null;
            s.newestNeedsNewerGame = false;
            if (s.kind == Kind.Unknown) s.kind = Detect(releases);
            if (s.kind == Kind.AssetPack) await ChoosePackAsync(s, releases, cache, ct);
            else if (s.kind == Kind.Mod) await ChooseModAsync(s, releases, cache, ct);
            s.latestCommit = s.latest?.commit;
            s.downloadBytes = s.latest?.zipSize ?? 0;
        }

        // Releases come newest first. The first with a usable mod.json tells whether the newest needs a newer game, the
        // first whose SDK build this game has (0 = any) and that has this platform's zip is the one to install.
        static async Awaitable ChooseModAsync(Source s, GitHubRelease[] releases, RepoCache cache, CancellationToken ct)
        {
            var number = BuildInfo.Number;
            var target = ModSdk.CurrentBuildTarget;
            ModManifest newest = null;
            var keep = new HashSet<long>();
            foreach (var rel in releases)
            {
                if (rel == null || rel.draft || rel.prerelease) continue;
                var asset = rel.Asset(ManifestFile);
                if (asset == null) continue;
                keep.Add(asset.id);
                if (s.latest != null) continue;
                var json = await ManifestJsonAsync(asset, cache, ct);
                var manifest = ModManifest.Parse(json);
                if (manifest == null) continue;
                newest ??= manifest;
                if (number > 0 && manifest.sdkBuild > number) continue;
                var zip = rel.Asset(manifest.targets?.For(target));
                if (zip == null) continue;
                s.latest = new Release
                {
                    kind = Kind.Mod, tag = rel.tag_name, commit = manifest.commit, manifest = manifest, manifestJson = json,
                    zipUrl = zip.browser_download_url, zipId = zip.id, zipSize = zip.size,
                };
            }
            cache.manifests.RemoveAll(m => !keep.Contains(m.assetId));
            s.newestNeedsNewerGame = newest != null && number > 0 && newest.sdkBuild > number;
            var described = s.latest != null ? s.latest.manifest : newest;
            if (described != null) Describe(s, described.packId, described.title, described.description);
        }

        static void Describe(Source s, string packId, string title, string description)
        {
            if (!string.IsNullOrEmpty(packId)) s.packId = packId;
            if (!string.IsNullOrEmpty(title)) s.title = title;
            if (description != null) s.description = description;
        }

        static State Evaluate(Source s)
        {
            if (s.latest == null) return s.newestNeedsNewerGame ? State.NeedsNewerGame : State.NoRelease;
            if (!s.installed) return State.NotInstalled;
            if (!IsInstalled(s, s.latest)) return State.UpdateAvailable;
            return s.newestNeedsNewerGame ? State.NeedsNewerGame : State.UpToDate;
        }

        static void SetEvaluated(Source s)
        {
            var state = Evaluate(s);
            SetState(s, state, state switch
            {
                State.NoRelease => "No release for this platform yet.",
                State.NeedsNewerGame => "The newest release needs a newer game.",
                _ => null,
            });
        }

        // Installed identity of a mod: (commit, sdkBuild) of the installed mod.json, of an asset pack: (release tag,
        // pack.json commit). A manual install has only its commit.
        static bool IsInstalled(Source s, Release latest)
        {
            if (latest.kind == Kind.AssetPack)
                return s.managed ? s.installedTag == latest.tag && (latest.pack?.commit == null || s.installedCommit == latest.pack.commit)
                                 : latest.pack?.commit != null && s.installedCommit == latest.pack.commit;
            if (!SameCommit(s.installedCommit, latest.commit)) return false;
            return !s.managed || s.installedSdkBuild == latest.manifest.sdkBuild;
        }

        static bool SameCommit(string a, string b)
        {
            const int ShortSha = 7;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length < ShortSha || b.Length < ShortSha) return false;
            return string.Compare(a, 0, b, 0, ShortSha, StringComparison.OrdinalIgnoreCase) == 0;
        }

        // A pending install (applied at the next content reload) counts as installed.
        static void RefreshInstalled(Source s)
        {
            s.installed = s.managed = false;
            s.installedCommit = s.installedTag = null;
            s.installedSdkBuild = 0;
            if (string.IsNullOrEmpty(s.packId) || s.kind == Kind.Unknown) return;
            if (s.kind == Kind.AssetPack)
            {
                RefreshInstalledPack(s);
                return;
            }
            foreach (var folder in new[] { HiddenFolder(ModsFolder, s.packId, PendingSuffix), InstalledFolder(ModsFolder, s.packId) })
            {
                var record = ReadJson<ModSourceRecord>(Path.Combine(folder, SourceFile));
                if (record == null || !SameRepo(record.repo, s.repo)) continue;
                var manifest = ModManifest.Parse(ReadText(Path.Combine(folder, ManifestFile)));
                s.installed = s.managed = true;
                s.installedTag = record.tag;
                s.installedCommit = manifest?.commit;
                s.installedSdkBuild = manifest != null ? manifest.sdkBuild : 0;
                return;
            }
            var pack = FindAvailable(s.packId);
            s.installed = pack != null || File.Exists(Path.Combine(InstalledFolder(ModsFolder, s.packId), s.packId + ModLoader.ModFileExtension));
            if (pack != null && !string.IsNullOrEmpty(pack.version)) s.installedCommit = pack.version;
        }

        // An added source's kind, pack id, title and description: from its last check, else from the folder it installed.
        static void RememberPackId(Source s)
        {
            var cache = RepoCache.Load(s.repo);
            if (!string.IsNullOrEmpty(cache.packId))
            {
                s.kind = cache.kind;
                Describe(s, cache.packId, cache.title, cache.description);
                return;
            }
            foreach (var (root, kind) in new[] { (ModsFolder, Kind.Mod), (PacksFolder, Kind.AssetPack) })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var record = ReadJson<ModSourceRecord>(Path.Combine(dir, SourceFile));
                    if (record == null || !SameRepo(record.repo, s.repo)) continue;
                    s.packId = Path.GetFileName(dir);
                    s.kind = kind;
                    return;
                }
            }
        }

        static void SetState(Source s, State state, string error)
        {
            s.state = state;
            s.error = error;
            RaiseChanged();
        }

        static string OneLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return "Unknown error.";
            var line = message.Split('\n')[0].Trim();
            return line.Length > MaxErrorLength ? line.Substring(0, MaxErrorLength) : line;
        }

        static string ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        static T ReadJson<T>(string path) where T : class
        {
            var text = ReadText(path);
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                return JsonUtility.FromJson<T>(text);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }
}
