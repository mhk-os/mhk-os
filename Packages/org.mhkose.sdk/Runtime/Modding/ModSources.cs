using System;
using System.Collections.Generic;
using System.Threading;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Mod sources: GitHub repositories whose releases the player installs and updates by hand, mods (a <c>mod.json</c>
    /// asset plus one zip per platform) into the user Mods folder and asset packs (one zip with pack.json, optionally a
    /// pack.json asset) into the asset pack folder. Every source is checked once at game start. Downloads can be paused
    /// and resumed and never reload content. The base game mods, TINcan Race and the Music Pack are built-in sources.
    /// </summary>
    public static partial class ModSources
    {
        /// <summary>
        /// What the last check found or what runs. NeedsNewerGame and NeedsModUpdate: no release has this game's mod API
        /// level (<see cref="ModSdk.ApiLevel"/>), the newest is newer or older. Paused: a download stopped (by <see cref="Pause"/>
        /// or a lost connection) with its bytes kept, <see cref="InstallAsync"/> or <see cref="Resume"/> continues it.
        /// </summary>
        public enum State { Unknown, Checking, NotInstalled, UpToDate, UpdateAvailable, Downloading, Installing, NeedsNewerGame, NoRelease, Offline, Error, NeedsModUpdate, Paused }

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
            /// <summary>Size of the chosen release's zip and the bytes downloaded so far (while Downloading or Paused).</summary>
            public long downloadBytes, downloadedBytes;
            /// <summary>A copy of the pack is installed (a loaded or disabled mod or asset pack, or a folder the game installed).</summary>
            public bool installed;
            /// <summary>The installed copy came from this source (its folder has source.json). False for manual installs.</summary>
            public bool managed;

            internal Release latest;
            internal bool newestNeedsNewerGame, newestNeedsModUpdate, busy;
            internal int installedSdkBuild;
            internal string installedTag;
            internal CancellationTokenSource pause;

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
        [AutoStaticsCleanup] static bool checking, retried, pendingChanges;
        [AutoStaticsCleanup] static float lastChanged;

        /// <summary>State or progress of a source changed, a source was added or removed (main thread).</summary>
        public static event Action Changed;

        /// <summary>Editor only: whether play mode checks the mod sources at start (MHK ▸ Modding toggle). Set by the editor.</summary>
        [NoAutoStaticsCleanup] public static Func<bool> EditorAutomatic { get; set; }

        /// <summary>Editor only: another GitHub API base URL from the editor preferences, empty for GitHub. Set by the editor.</summary>
        [NoAutoStaticsCleanup] public static Func<string> EditorApiBase { get; set; }

        /// <summary>Every source in list order: the built-in ones the player kept, then the added ones.</summary>
        public static IReadOnlyList<Source> All => sources ??= LoadSources();

        /// <summary>Whether the sources are checked at game start: always in players, in the editor only with the MHK ▸ Modding toggle.</summary>
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

        /// <summary>A check of every source runs (the start check or <see cref="RetryFailedChecks"/>).</summary>
        public static bool Checking => checking;

        /// <summary>A download or install runs (paused ones do not count).</summary>
        public static bool AnyDownloading
        {
            get
            {
                foreach (var s in All)
                    if (s.state is State.Downloading or State.Installing) return true;
                return false;
            }
        }

        /// <summary>An installed source of <paramref name="kind"/> has an update (UpdateAvailable, or its update download is Paused).</summary>
        public static bool HasUpdates(Kind kind)
        {
            foreach (var s in All)
                if (s.kind == kind && s.installed && s.state is State.UpdateAvailable or State.Paused) return true;
            return false;
        }

        /// <summary>An install, update or deletion finished since the last content load: the Mods page's Apply loads it.</summary>
        public static bool PendingChanges => pendingChanges;

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
            if (root != null) CheckQuietlyAsync(Find(repo), null, InSession(root.destroyCancellationToken)).LogExceptionsAndForget();
            return true;
        }

        /// <summary>
        /// Removes <paramref name="repo"/> from the list (a built-in one stays removed until added again) and drops its
        /// paused download. With <paramref name="deleteInstalledMod"/> its installed folder is deleted too (<see cref="PendingChanges"/>).
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
            source.pause?.Cancel();
            DeletePartial(source.repo);
            if (deleteInstalledMod) DeleteInstalled(source);
            sources.Remove(source);
            RaiseChanged();
        }

        /// <summary>
        /// Checks again, once per game run, the sources whose start check failed or did not run (Unknown, Offline, Error):
        /// the Mods page calls it when it opens. Only when <see cref="Enabled"/> and no check runs. True when it started one.
        /// </summary>
        public static bool RetryFailedChecks()
        {
            var root = GameRoot.Instance;
            if (retried || checking || !Enabled || root == null) return false;
            var failed = false;
            foreach (var s in All)
                failed |= Failed(s);
            if (!failed) return false;
            retried = true;
            CheckAllQuietlyAsync(true, InSession(root.destroyCancellationToken)).LogExceptionsAndForget();
            return true;
        }

        /// <summary>
        /// Checks every listed source again (the Mods page's Check for Updates), also in the editor without the start
        /// check. Ignored while a check runs. True when it started one, <see cref="Changed"/> reports its progress.
        /// </summary>
        public static bool CheckAll()
        {
            var root = GameRoot.Instance;
            if (checking || root == null) return false;
            CheckAllQuietlyAsync(false, InSession(root.destroyCancellationToken)).LogExceptionsAndForget();
            return true;
        }

        static bool Failed(Source s) => !s.busy && s.state is State.Unknown or State.Offline or State.Error;

        // onlyFailed: the retry of the sources whose start check failed.
        static async Awaitable CheckAllQuietlyAsync(bool onlyFailed, CancellationToken ct)
        {
            if (checking) return;
            checking = true;
            RaiseChanged();
            try
            {
                var running = new int[1];
                foreach (var s in All)
                    if (!s.busy && (!onlyFailed || Failed(s))) CheckQuietlyAsync(s, running, ct).LogExceptionsAndForget();
                while (running[0] > 0) await Awaitable.NextFrameAsync(ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                checking = false;
                RaiseChanged();
            }
        }

        static async Awaitable CheckQuietlyAsync(Source s, int[] running, CancellationToken ct)
        {
            if (running != null) running[0]++;
            try
            {
                await CheckAsync(s, ct);
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
        /// Downloads (continuing a paused download of the same release) and installs the release the start check chose
        /// for <paramref name="repo"/> (a source without one is checked first). Never reloads content: true when installed,
        /// the new copy loads at the next content load (<see cref="PendingChanges"/>). False when it failed or was paused.
        /// </summary>
        public static async Awaitable<bool> InstallAsync(string repo, CancellationToken ct)
        {
            var s = Find(repo);
            if (s == null || s.busy || !Application.isPlaying) return false;
            ct = InSession(ct);
            if (s.latest == null) await CheckAsync(s, ct);
            ct.ThrowIfCancellationRequested();
            if (s.latest == null || s.busy) return false;
            return await InstallLatestAsync(s, ct);
        }

        /// <summary>Pauses the running download of <paramref name="repo"/>: its bytes stay in the cache (state Paused). True when one was paused.</summary>
        public static bool Pause(string repo)
        {
            var s = Find(repo);
            if (s == null || s.state != State.Downloading || s.pause == null) return false;
            s.pause.Cancel();
            return true;
        }

        /// <summary>Starts <see cref="InstallAsync"/> for <paramref name="repo"/> in the background (continues a paused download). True when it started.</summary>
        public static bool Resume(string repo)
        {
            var s = Find(repo);
            var root = GameRoot.Instance;
            if (s == null || s.busy || root == null || !Application.isPlaying) return false;
            InstallQuietlyAsync(repo, root.destroyCancellationToken).LogExceptionsAndForget();
            return true;
        }

        static async Awaitable InstallQuietlyAsync(string repo, CancellationToken ct)
        {
            try
            {
                await InstallAsync(repo, ct);
            }
            catch (OperationCanceledException)
            {
            }
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
        /// Game start (called by <see cref="GameRoot"/> once mods are loaded): removes downloads of sources no longer listed
        /// and, when <see cref="Enabled"/>, starts the one check of every source and of the game (<see cref="GameUpdate"/>).
        /// </summary>
        internal static void Startup(CancellationToken ct)
        {
            PruneDownloads();
            if (!Enabled) return;
            CheckAllQuietlyAsync(false, InSession(ct)).LogExceptionsAndForget();
            GameUpdate.Check(InSession(ct));
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
    }
}
