using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// The check of a loaded mod's build stamp (<see cref="ContentPack.sdkVersion"/>, the SDK commit, <see cref="ContentPack.unityVersion"/>,
    /// <see cref="ContentPack.buildTarget"/>) against this game. Every game build has its own commit, so only the Unity
    /// version and the build target are compared, and a mismatch only warns.
    /// </summary>
    public static class ModSdk
    {
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
            Debug.Log($"[Mods] '{pack.packId}' built with SDK {sdk}, Unity {pack.unityVersion}, for {pack.buildTarget}.");
            if (pack.unityVersion != Application.unityVersion)
                Debug.LogWarning($"[Mods] '{pack.packId}' was built with Unity {pack.unityVersion}, the game runs Unity {Application.unityVersion}.");
            if (pack.buildTarget != CurrentBuildTarget)
                Debug.LogWarning($"[Mods] '{pack.packId}' was built for {pack.buildTarget}, this is {CurrentBuildTarget}: use the mod files of this platform.");
        }
    }
}
