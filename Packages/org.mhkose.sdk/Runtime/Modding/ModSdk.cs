using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// The check of a loaded mod's build stamp (<see cref="ContentPack.sdkApi"/>, <see cref="ContentPack.sdkVersion"/>,
    /// <see cref="ContentPack.unityVersion"/>, <see cref="ContentPack.buildTarget"/>) against this game. Only the API
    /// level decides whether a mod loads, a different Unity version or build target only warns.
    /// </summary>
    public static class ModSdk
    {
        /// <summary>
        /// Mod API level of this SDK, stamped into every built mod (<see cref="ContentPack.sdkApi"/>) and its release
        /// (mod.json sdkApi). The game loads and installs only mods with its own level. Bump it when a change breaks built
        /// mods (renamed, moved or removed serialized types or fields, changed meaning of data). The mod release workflows
        /// read it from this line. 2: racing lines are the TrackPaths below the TrackRuntime (its path and alternatePaths
        /// fields are gone), PlacementPowerup.throwBackwards replaces alwaysForward, ContentPack.sharedPowerups is gone.
        /// </summary>
        public const int ApiLevel = 2;

        // Level 1 is the API the level was introduced with, so a mod built before (stamp 0, mod.json without sdkApi) has it.
        const int FirstLevel = 1;

        /// <summary>The mod API level of a stamp or mod.json sdkApi: 0 (built before the level existed) is the first level.</summary>
        public static int Level(int stamp) => stamp > 0 ? stamp : FirstLevel;

        /// <summary>Whether a mod stamped with the mod API level <paramref name="stamp"/> works in this game.</summary>
        public static bool IsCompatible(int stamp) => Level(stamp) == ApiLevel;

        /// <summary>The BuildTarget name of bundles this player or editor loads (StandaloneOSX, StandaloneWindows64, Android, ...).</summary>
        public static string CurrentBuildTarget
        {
            get
            {
#if UNITY_EDITOR
                return UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
#else
                return Application.platform switch
                {
                    RuntimePlatform.OSXPlayer => "StandaloneOSX",
                    RuntimePlatform.WindowsPlayer => "StandaloneWindows64",
                    RuntimePlatform.LinuxPlayer => "StandaloneLinux64",
                    RuntimePlatform.Android => "Android",
                    RuntimePlatform.IPhonePlayer => "iOS",
                    _ => Application.platform.ToString(),
                };
#endif
            }
        }

        /// <summary>Logs the build stamp of the bundle-loaded <paramref name="pack"/> from <paramref name="file"/>, a warning for each mismatch.</summary>
        public static void LogStamp(ContentPack pack, string file)
        {
            if (string.IsNullOrEmpty(pack.unityVersion))
            {
                Debug.Log($"[Mods] '{pack.packId}' ({file}) has no build stamp: built before the stamp existed.");
                return;
            }
            var sdk = string.IsNullOrEmpty(pack.sdkVersion) ? "of unknown commit" : pack.sdkVersion;
            Debug.Log($"[Mods] '{pack.packId}' built with SDK {sdk} (mod API level {Level(pack.sdkApi)}), Unity {pack.unityVersion}, for {pack.buildTarget}.");
            if (pack.unityVersion != Application.unityVersion)
                Debug.LogWarning($"[Mods] '{pack.packId}' was built with Unity {pack.unityVersion}, the game runs Unity {Application.unityVersion}.");
            if (pack.buildTarget != CurrentBuildTarget)
                Debug.LogWarning($"[Mods] '{pack.packId}' was built for {pack.buildTarget}, this is {CurrentBuildTarget}: use the mod files of this platform.");
        }
    }
}
