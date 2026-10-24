using System.IO;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>Files and folders of the player's data under <see cref="Application.persistentDataPath"/>.</summary>
    public static partial class DataPaths
    {
        public const string GhostsFolder = "Ghosts";
        public const string ModsFolder = "Mods";
        public const string AssetPacksFolder = "AssetPacks";

        public static string Root => Application.persistentDataPath;

        /// <summary><paramref name="name"/> inside the persistent data folder.</summary>
        public static string File(string name) => Path.Combine(Root, name);

        public static string Ghosts => File(GhostsFolder);
        public static string Mods => File(ModsFolder);
        /// <summary>The player's asset packs, next to <see cref="Mods"/>.</summary>
        public static string AssetPacks => File(AssetPacksFolder);

        /// <summary>
        /// Folder the game is installed in: the one holding the executable (Windows, Linux) or the .app (macOS, the
        /// user's location also when Gatekeeper runs a translocated copy), the project folder in the editor, null on
        /// Android (the game is an APK). Shipped desktop mods sit in its Mods folder.
        /// </summary>
        public static string InstallRoot => installRoot ??= FindInstallRoot();

        [NoAutoStaticsCleanup] static string installRoot;

        static string FindInstallRoot()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return null;
#elif UNITY_STANDALONE_OSX && !UNITY_EDITOR
            var app = Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
            return Path.GetDirectoryName(MacAppTranslocation.Original(app));
#else
            return Path.GetDirectoryName(Path.GetFullPath(Application.dataPath));
#endif
        }
    }
}
