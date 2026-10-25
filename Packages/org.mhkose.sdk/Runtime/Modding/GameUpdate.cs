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
    /// Whether a newer game exists: at start a numbered build (<see cref="BuildInfo.Number"/> above 0) reads the latest
    /// release of the game's repository, whose name is "Build &lt;n&gt; | &lt;sha7&gt;". Dev builds (number 0) never check.
    /// </summary>
    public static partial class GameUpdate
    {
        public const string Repo = "mhk-os/mhk-os";

        static readonly Regex BuildName = new(@"^\s*Build\s+(\d+)", RegexOptions.CultureInvariant);

        [AutoStaticsCleanup] static int latestNumber;
        [AutoStaticsCleanup] static string releaseUrl;

        /// <summary>The latest release has a higher build number than this game.</summary>
        public static bool Available => latestNumber > BuildInfo.Number && BuildInfo.Number > 0 && !string.IsNullOrEmpty(releaseUrl);

        /// <summary>Web page of the latest release (its html_url), null before the check found one.</summary>
        public static string ReleaseUrl => releaseUrl;

        /// <summary>Build number of the latest release, 0 while unknown.</summary>
        public static int LatestNumber => latestNumber;

        /// <summary>The check finished with a result (main thread).</summary>
        public static event Action Changed;
        [Serializable]
        sealed class LatestRelease
        {
            public string name, html_url;
        }

        /// <summary>Starts the one check of this game run (called by <see cref="ModSources"/> at game start).</summary>
        internal static void Check(CancellationToken ct)
        {
            if (BuildInfo.Number <= 0) return;
            CheckAsync(ct).LogExceptionsAndForget();
        }

        static async Awaitable CheckAsync(CancellationToken ct)
        {
            var cache = ModSources.RepoCache.Load(Repo + ".latest");
            try
            {
                var body = await ModSources.FetchAsync($"{ModSources.ApiBase}/repos/{Repo}/releases/latest", cache, ct);
                cache.Save(Repo + ".latest");
                var release = JsonUtility.FromJson<LatestRelease>(body);
                var match = release != null ? BuildName.Match(release.name ?? "") : Match.Empty;
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return;
                latestNumber = number;
                releaseUrl = release.html_url;
                Debug.Log($"[GameUpdate] Latest release build {number} ({releaseUrl}), this is build {BuildInfo.Number}: {(Available ? "update available" : "up to date")}.");
                Changed?.Invoke();
            }
            catch (ModSources.SourceException e)
            {
                Debug.Log($"[GameUpdate] Check failed: {e.Message}");
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"[GameUpdate] Unexpected answer: {e.Message}");
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
