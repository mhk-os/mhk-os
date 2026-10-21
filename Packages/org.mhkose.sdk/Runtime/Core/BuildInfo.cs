using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// The commit of this build, the only identity of the game and its SDK. Players carry it as
    /// <see cref="Application.version"/> (stamped by the player build), the editor reports "dev".
    /// </summary>
    public static class BuildInfo
    {
        /// <summary>Commit label of the editor.</summary>
        public const string EditorCommit = "dev";

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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LogBuild() =>
            Debug.Log($"[Build] Moorhuhn Kart, commit {Commit}, Unity {Application.unityVersion}, {Application.platform}.");
    }
}
