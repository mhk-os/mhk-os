using MHK.Core;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// The check of a loaded mod's build stamp (<see cref="ContentPack.sdkVersion"/>, <see cref="ContentPack.unityVersion"/>,
    /// <see cref="ContentPack.buildTarget"/>) against this game (SDK version <see cref="BuildInfo.Version"/>). A mismatch only warns.
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

        /// <summary>
        /// Major and minor of a version ("0.1.0" gives "0.1"). Mods built with another major or minor SDK may use types
        /// or fields this game does not have.
        /// </summary>
        public static string Feature(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            var first = version.IndexOf('.');
            var second = first < 0 ? -1 : version.IndexOf('.', first + 1);
            return second < 0 ? version : version.Substring(0, second);
        }

        /// <summary>Logs the build stamp of the bundle-loaded <paramref name="pack"/> from <paramref name="file"/>, a warning for each mismatch.</summary>
        public static void LogStamp(ContentPack pack, string file)
        {
            var sdk = BuildInfo.Version;
            if (string.IsNullOrEmpty(pack.sdkVersion))
            {
                Debug.Log($"[Mods] '{pack.packId}' ({file}) has no build stamp: built before SDK {sdk}.");
                return;
            }
            Debug.Log($"[Mods] '{pack.packId}' built with SDK {pack.sdkVersion}, Unity {pack.unityVersion} for {pack.buildTarget}.");
            if (Feature(pack.sdkVersion) != Feature(sdk))
                Debug.LogWarning($"[Mods] '{pack.packId}' was built with SDK {pack.sdkVersion}, the game uses SDK {sdk}: rebuild the mod with SDK {Feature(sdk)}.x if it misbehaves.");
            if (pack.unityVersion != Application.unityVersion)
                Debug.LogWarning($"[Mods] '{pack.packId}' was built with Unity {pack.unityVersion}, the game runs Unity {Application.unityVersion}.");
            if (pack.buildTarget != CurrentBuildTarget)
                Debug.LogWarning($"[Mods] '{pack.packId}' was built for {pack.buildTarget}, this is {CurrentBuildTarget}: use the mod files of this platform.");
        }
    }
}
