using System;
using System.Collections.Generic;
using System.Threading;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Mod sources: GitHub repositories whose releases the game installs and keeps up to date, mods (a <c>mod.json</c>
    /// asset plus one zip per platform) into the user Mods folder and asset packs (one zip with pack.json, optionally a
    /// pack.json asset) into the asset pack folder. The base game mods, TINcan Race and the Music Pack are built-in sources.
    /// </summary>
    public static partial class ModSources
    {
        /// <summary>What the last check found. NeedsNewerGame and NeedsModUpdate: no release has this game's mod API level (<see cref="ModSdk.ApiLevel"/>), the newest is newer or older.</summary>
        public enum State { Unknown, Checking, NotInstalled, UpToDate, UpdateAvailable, Downloading, Installing, NeedsNewerGame, NoRelease, Offline, Error, NeedsModUpdate }

        /// <summary>What a source publishes, Unknown until the first check of an added source finds out.</summary>
        public enum Kind { Unknown, Mod, AssetPack }

        /// <summary>One repository and what the last check found. Read it on the main thread, <see cref="Changed"/> reports changes.</summary>
        public sealed class Source
        {
            /// <summary>"owner/name".</summary>
            public string repo;
            public bool builtIn;
            public Kind kind;
            /// <summary>Mod pack id or asset pack id, title and description from the release's mod.json or pack.json once known (built-in sources know them from the start), title else the repository name.</summary>
            public string packId, title, description;
            public State state;
            /// <summary>
            /// Identity of the installed copy (null when unknown) and of the release an install would take: the mod commit,
            /// for asset packs the pack.json commit or else the release tag. <see cref="error"/> is a one-line reason (Offline, Error, NoRelease, NeedsNewerGame, NeedsModUpdate).
            /// </summary>
            public string installedCommit, latestCommit, error;
            /// <summary>Size of the chosen release's zip and the bytes downloaded so far (while Downloading).</summary>
            public long downloadBytes, downloadedBytes;
            /// <summary>A copy of the pack is installed (a loaded or disabled mod or asset pack, or a folder the game installed).</summary>
            public bool installed;
            /// <summary>The installed copy came from this source (its folder has source.json): automatic updates apply. False for manual installs.</summary>
            public bool managed;

            internal Release latest;
            internal bool newestNeedsNewerGame, newestNeedsModUpdate, busy;
            internal int installedSdkBuild;
            internal string installedTag;
            internal float checkedAt = float.NegativeInfinity;

            /// <summary>A built-in mod (a base game or TINcan Race), the ones the first start download offers. Built-in asset packs are only listed.</summary>
            public bool BaseMod => builtIn && kind == Kind.Mod;
        }

        /// <summary>Command-line option with another GitHub API base URL (tests use a local mock server).</summary>
        public const string ApiOption = "-modSourcesApi";
        public const string DefaultApi = "https://api.github.com";

        static readonly (string repo, Kind kind, string packId, string title)[] BuiltIns =
        {
            ("mhk-os/mhk2-mod", Kind.Mod, "mhk2", "Moorhuhn Kart 2"),
            ("mhk-os/mhk3-mod", Kind.Mod, "mhk3", "Moorhuhn Kart 3"),
            ("mhk-os/mhke-mod", Kind.Mod, "mhke", "Moorhuhn Kart Extra"),
            ("mhk-os/mhkt-mod", Kind.Mod, "mhkt", "Moorhuhn Kart Thunder"),
            ("mhk-os/tincan-mod", Kind.Mod, "tincan", "TINcan Race"),
            ("mhk-os/music-asset-pack", Kind.AssetPack, "music", "Music Pack"),
        };

        [AutoStaticsCleanup] static List<Source> sources;
        [AutoStaticsCleanup] static bool checking, startupCheckRunning;
        [AutoStaticsCleanup] static float lastChanged;

        /// <summary>State or progress of a source changed, a source was added or removed (main thread).</summary>
        public static event Action Changed;

        /// <summary>Editor only: whether the automatic start-of-game check and update run (MHK ▸ Modding toggle). Set by the editor.</summary>
        [NoAutoStaticsCleanup] public static Func<bool> EditorAutomatic { get; set; }

        /// <summary>Editor only: another GitHub API base URL from the editor preferences, empty for GitHub. Set by the editor.</summary>
        [NoAutoStaticsCleanup] public static Func<string> EditorApiBase { get; set; }

        /// <summary>Every source in list order: the built-in ones the player kept, then the added ones.</summary>
        public static IReadOnlyList<Source> All => sources ??= LoadSources();

        /// <summary>Whether the automatic check and update run at game start: always in players, in the editor only with the MHK ▸ Modding toggle.</summary>
        public static bool Enabled
        {
            get
            {
#if UNITY_EDITOR
                return EditorAutomatic != null && EditorAutomatic();
#else
                return true;
#endif
            }
        }

        /// <summary>Install compatible updates at game start (<see cref="UserSettings.autoUpdateMods"/>, default on).</summary>
        public static bool AutoUpdate
        {
            get => Settings == null || Settings.autoUpdateMods;
            set
            {
                if (Settings == null || Settings.autoUpdateMods == value) return;
                Settings.autoUpdateMods = value;
                Save();
                RaiseChanged();
            }
        }

        /// <summary>A built-in mod in the list has no installed pack with its id (enabled or disabled).</summary>
        public static bool BaseGamesMissing
        {
            get
            {
                foreach (var s in All)
                    if (s.BaseMod && FindAvailable(s.packId) == null) return true;
                return false;
            }
        }

        /// <summary>The source of <paramref name="repo"/> ("owner/name", any case), null when it is not in the list.</summary>
        public static Source Find(string repo)
        {
            foreach (var s in All)
                if (string.Equals(s.repo, repo, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>
        /// Adds the repository of <paramref name="input"/> (a GitHub link, <c>github.com/owner/name</c> or <c>owner/name</c>)
        /// to the list, saves it and checks it. False with a one-line <paramref name="error"/> when it is no repository or already listed.
        /// </summary>
        public static bool TryAdd(string input, out string error)
        {
            if (!RepoLink.TryParse(input, out var repo))
            {
                error = "Paste a GitHub repository link like github.com/owner/name.";
                return false;
            }
            if (Find(repo) != null)
            {
                error = "This repository is already in the list.";
                return false;
            }
            error = null;
            var builtIn = BuiltInRepo(repo);
            if (builtIn != null) RemoveIgnoreCase(Settings?.removedModSources, builtIn);
            else Settings?.modSources.Add(repo);
            Save();
            sources = LoadSources();
            RaiseChanged();
            var root = GameRoot.Instance;
            if (root != null) CheckQuietlyAsync(Find(repo), null, false, InSession(root.destroyCancellationToken)).LogExceptionsAndForget();
            return true;
        }

        /// <summary>
        /// Removes <paramref name="repo"/> from the list (a built-in one stays removed until added again). With
        /// <paramref name="deleteInstalledMod"/> its installed mod folder is deleted too: reload content afterwards.
        /// </summary>
        public static void Remove(string repo, bool deleteInstalledMod)
        {
            var source = Find(repo);
            if (source == null) return;
            if (source.builtIn)
            {
                if (Settings != null && !ContainsIgnoreCase(Settings.removedModSources, source.repo)) Settings.removedModSources.Add(source.repo);
            }
            else RemoveIgnoreCase(Settings?.modSources, source.repo);
            Save();
            if (deleteInstalledMod) DeleteInstalled(source);
            sources.Remove(source);
            RaiseChanged();
        }

        /// <summary>Checks every source on GitHub for a compatible release (sources being installed are skipped). A running check is joined.</summary>
        public static Awaitable CheckAsync(CancellationToken ct) => CheckAllAsync(false, InSession(ct));

        // allowCached: a source whose last successful check is younger than GameConfig.modCheckIntervalMinutes is judged
        // from its cached releases list without a request (the start-of-game check).
        static async Awaitable CheckAllAsync(bool allowCached, CancellationToken ct)
        {
            if (checking)
            {
                while (checking) await Awaitable.NextFrameAsync(ct);
                return;
            }
            checking = true;
            try
            {
                var running = new int[1];
                foreach (var s in All)
                    if (!s.busy) CheckQuietlyAsync(s, running, allowCached, ct).LogExceptionsAndForget();
                while (running[0] > 0) await Awaitable.NextFrameAsync(ct);
            }
            finally
            {
                checking = false;
            }
        }

        static async Awaitable CheckQuietlyAsync(Source s, int[] running, bool allowCached, CancellationToken ct)
        {
            if (running != null) running[0]++;
            try
            {
                await CheckAsync(s, ct, allowCached);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (running != null) running[0]--;
            }
        }

        /// <summary>
        /// Downloads and installs the latest compatible release of <paramref name="repo"/> (checked again first when the
        /// last check is older than a minute). True when installed: the caller reloads content (<see cref="SceneFlow.ReloadContent"/>).
        /// </summary>
        public static async Awaitable<bool> InstallAsync(string repo, CancellationToken ct)
        {
            var s = Find(repo);
            if (s == null || s.busy || !Application.isPlaying) return false;
            ct = InSession(ct);
            if (s.latest == null || Time.realtimeSinceStartup - s.checkedAt > RecheckSeconds) await CheckAsync(s, ct);
            ct.ThrowIfCancellationRequested();
            if (s.latest == null || s.busy) return false;
            return await InstallLatestAsync(s, ct);
        }

        // With domain reload off, a check or download still awaiting when play mode ends would otherwise go on in the
        // next play session (an install nobody asked for there). Every operation is tied to the session it started in.
        [NoAutoStaticsCleanup] static CancellationTokenSource session = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void NewSession()
        {
            session.Cancel();
            session = new CancellationTokenSource();
            Application.quitting -= EndSession;
            Application.quitting += EndSession;
        }

        static void EndSession() => session.Cancel();

        static CancellationToken InSession(CancellationToken ct) => CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token).Token;

        /// <summary>
        /// Game start (called by <see cref="GameRoot"/> once mods are loaded): removes downloads a closed game left behind
        /// and, when <see cref="Enabled"/>, starts the once-per-start check of every source.
        /// </summary>
        internal static void Startup(CancellationToken ct)
        {
            ClearDownloads();
            if (!Enabled) return;
            startupCheckRunning = true;
            StartupCheckAsync(InSession(ct)).LogExceptionsAndForget();
        }

        static async Awaitable StartupCheckAsync(CancellationToken ct)
        {
            try
            {
                await CheckAllAsync(true, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                startupCheckRunning = false;
            }
        }

        /// <summary>The start-of-game check still runs.</summary>
        internal static bool StartupCheckRunning => startupCheckRunning;

        /// <summary>Automatic updates are on and the check found updates of mods or asset packs this game installed (never manual installs).</summary>
        internal static bool AutoUpdatesDue
        {
            get
            {
                if (!Enabled || !AutoUpdate) return false;
                foreach (var s in All)
                    if (IsDue(s, Kind.Unknown)) return true;
                return false;
            }
        }

        // An update of an install this game made (source.json), of kind (Unknown = any kind).
        static bool IsDue(Source s, Kind kind) =>
            s.state == State.UpdateAvailable && s.managed && s.latest != null && !s.busy && (kind == Kind.Unknown || s.kind == kind);

        /// <summary>
        /// Installs the update of every installed source of <paramref name="kind"/> that has one (installs this game made,
        /// manual installs are left alone). Returns how many got installed: the caller reloads content (<see cref="SceneFlow.ReloadContent"/>).
        /// </summary>
        public static Awaitable<int> UpdateAllAsync(Kind kind, CancellationToken ct) => InstallDueAsync(kind, null, InSession(ct));

        /// <summary>Installs every due automatic update of both kinds, reporting the share of bytes done 0..1. Returns how many got installed.</summary>
        internal static Awaitable<int> InstallAutoUpdatesAsync(Action<float> progress, CancellationToken ct) => InstallDueAsync(Kind.Unknown, progress, InSession(ct));

        static async Awaitable<int> InstallDueAsync(Kind kind, Action<float> progress, CancellationToken ct)
        {
            var due = new List<Source>();
            long total = 0, done = 0;
            foreach (var s in All)
                if (IsDue(s, kind))
                {
                    due.Add(s);
                    total += Math.Max(1, s.downloadBytes);
                }
            var installed = 0;
            foreach (var s in due)
            {
                var size = Math.Max(1, s.downloadBytes);
                var start = done;
                using (OnChanged(() => progress?.Invoke((float)(start + Math.Min(size, s.downloadedBytes)) / total)))
                    if (await InstallLatestAsync(s, ct)) installed++;
                done += size;
                progress?.Invoke((float)done / total);
            }
            Debug.Log($"[ModSources] Updates ({(kind == Kind.Unknown ? "automatic" : kind.ToString())}): {installed} of {due.Count} installed.");
            return installed;
        }

        static UserSettings Settings
        {
            get
            {
                var root = GameRoot.Instance;
                return root != null ? root.Settings : null;
            }
        }

        static void Save()
        {
            var root = GameRoot.Instance;
            if (root != null) root.SaveSettings();
        }

        // Sources already listed keep their objects, so adding one does not reset what the others' checks found.
        static List<Source> LoadSources()
        {
            var list = new List<Source>();
            var settings = Settings;
            foreach (var (repo, kind, packId, title) in BuiltIns)
                if (settings == null || !ContainsIgnoreCase(settings.removedModSources, repo))
                    list.Add(Listed(repo) ?? NewSource(repo, kind, packId, title));
            if (settings != null)
                foreach (var repo in settings.modSources)
                    if (RepoLink.TryParse(repo, out var r) && BuiltInRepo(r) == null && list.Find(s => SameRepo(s.repo, r)) == null)
                        list.Add(Listed(r) ?? NewSource(r, Kind.Unknown, null, null));
            return list;
        }

        static Source Listed(string repo) => sources?.Find(s => SameRepo(s.repo, repo));

        // A built-in source comes with its kind, an added one is Kind.Unknown until its first check.
        static Source NewSource(string repo, Kind kind, string packId, string title)
        {
            var s = new Source
            {
                repo = repo, builtIn = kind != Kind.Unknown, kind = kind, packId = packId,
                title = title ?? repo.Substring(repo.IndexOf('/') + 1),
            };
            if (packId == null) RememberPackId(s);
            RefreshInstalled(s);
            return s;
        }

        static string BuiltInRepo(string repo)
        {
            foreach (var b in BuiltIns)
                if (SameRepo(b.repo, repo)) return b.repo;
            return null;
        }

        static bool SameRepo(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool ContainsIgnoreCase(List<string> list, string value) => list != null && list.FindIndex(x => SameRepo(x, value)) >= 0;

        static void RemoveIgnoreCase(List<string> list, string value) => list?.RemoveAll(x => SameRepo(x, value));

        static ContentPack FindAvailable(string packId)
        {
            if (string.IsNullOrEmpty(packId)) return null;
            foreach (var p in ModLoader.AvailablePacks)
                if (p != null && string.Equals(p.packId, packId, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // Progress fires many times a second while downloading: the UI hears about it at most every ProgressSeconds.
        static void RaiseChanged(bool progressOnly = false)
        {
            if (progressOnly && Time.realtimeSinceStartup - lastChanged < ProgressSeconds) return;
            lastChanged = Time.realtimeSinceStartup;
            Changed?.Invoke();
        }

        static IDisposable OnChanged(Action action)
        {
            Changed += action;
            return new Unsubscribe(action);
        }

        sealed class Unsubscribe : IDisposable
        {
            readonly Action action;
            public Unsubscribe(Action action) => this.action = action;
            public void Dispose() => Changed -= action;
        }
    }
}
