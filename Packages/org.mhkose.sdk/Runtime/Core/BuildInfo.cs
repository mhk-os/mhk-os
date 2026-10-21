using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// Version and commit of this build. The version is the SDK package's (org.mhkose.sdk package.json), players carry
    /// both as <see cref="Application.version"/> "&lt;version&gt;+&lt;commit&gt;" (stamped by the player build), the editor reports commit "dev".
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>Commit label of the editor.</summary>
        public const string EditorCommit = "dev";
        const char CommitSeparator = '+';

        /// <summary>The version of the game and its SDK, for example "0.1.0".</summary>
        public static string Version
        {
            get
            {
#if UNITY_EDITOR
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BuildInfo).Assembly);
                return package != null ? package.version : "";
#else
                var id = Application.version;
                var i = id.IndexOf(CommitSeparator);
                return i < 0 ? id : id.Substring(0, i);
#endif
            }
        }

        /// <summary>Short commit the player was built from ("abc1234", "abc1234-dirty" with local changes), "dev" in the editor.</summary>
        public static string Commit
        {
            get
            {
#if UNITY_EDITOR
                return EditorCommit;
#else
                var id = Application.version;
                var i = id.IndexOf(CommitSeparator);
                return i < 0 ? "" : id.Substring(i + 1);
#endif
            }
        }

        /// <summary>Version and commit as one label ("0.1.0+abc1234"), stored in ghosts and benchmark results.</summary>
        public static string Id => Compose(Version, Commit);

        /// <summary>The label of <paramref name="version"/> and <paramref name="commit"/> (a SemVer version with the commit as build metadata).</summary>
        public static string Compose(string version, string commit) =>
            string.IsNullOrEmpty(commit) ? version : version + CommitSeparator + commit;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LogBuild() =>
            Debug.Log($"[Build] Moorhuhn Kart {Version}, commit {Commit}, Unity {Application.unityVersion}, {Application.platform}.");
    }
}
