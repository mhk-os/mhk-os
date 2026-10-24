using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// The commit of this build, the identity of the game and its SDK, and its build number (mods name the SDK build they
    /// need). Players carry the commit as <see cref="Application.version"/> and the number as a Resources text asset (both
    /// stamped by the player build), the editor reports "dev" and 0.
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>Commit label of the editor.</summary>
        public const string EditorCommit = "dev";
        /// <summary>Resources text asset with the build number, stamped by the player build ("0" in the project).</summary>
        public const string NumberResource = "BuildNumber";

        /// <summary>Short commit the player was built from ("abc1234", "abc1234-dirty" with local changes), "dev" in the editor.</summary>
        public static string Commit
        {
            get
            {
#if UNITY_EDITOR
                return EditorCommit;
#else
                return Application.version;
#endif
            }
        }

        /// <summary>
        /// The game's build number (commits on main at this build, the number in the release title, the <c>sdkBuild</c>
        /// of mods built with this SDK), stamped into players by the build. 0 in the editor (the project's file says 0)
        /// and in unstamped builds.
        /// </summary>
        public static int Number
        {
            get
            {
                var text = Resources.Load<TextAsset>(NumberResource);
                return text != null && int.TryParse(text.text.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LogBuild() =>
            Debug.Log($"[Build] Moorhuhn Kart, commit {Commit}, build {Number}, Unity {Application.unityVersion}, {Application.platform}.");
    }
}
