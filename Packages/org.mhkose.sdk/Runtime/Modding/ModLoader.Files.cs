using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    public static partial class ModLoader
    {
        const string AssetsFolder = "Assets";
        const string ZipExtension = ".zip";

        [AutoStaticsCleanup] static readonly Dictionary<ContentPack, string> folderOf = new();
        [AutoStaticsCleanup] static readonly Dictionary<ContentPack, AssetBundle> bundleOf = new();

        /// <summary>
        /// Command-line option with extra mod folders (several joined by the path separator), searched first. Like the
        /// dev mods folders, a folder with a subfolder named after this platform (a mod build output) is searched there.
        /// </summary>
        public const string ModsOption = "-mods";

        /// <summary>Folder for user-installed mods (shown in the Mods menu): one folder or zip per mod, old single <c>.mhkmod</c> files too.</summary>
        public static string UserFolder => DataPaths.Mods;

        /// <summary>
        /// Mods that ship with the game (the base content): <c>&lt;install&gt;/Mods</c> in desktop players (one folder per mod
        /// with its loose <c>Assets/</c>), StreamingAssets/Mods on Android and in the editor.
        /// </summary>
        public static string ShippedFolder
        {
            get
            {
#if UNITY_EDITOR || UNITY_ANDROID
                return Path.Combine(Application.streamingAssetsPath, DataPaths.ModsFolder);
#else
                return Path.Combine(DataPaths.InstallRoot, DataPaths.ModsFolder);
#endif
            }
        }

        /// <summary>Where mod zips are extracted (one folder per zip, keyed by its size and date).</summary>
        public static string CacheFolder => Path.Combine(DataPaths.Root, "Cache", DataPaths.ModsFolder);

        /// <summary>
        /// Disk folder of the enabled bundle-loaded mod <paramref name="root"/>: the folder of its <c>.mhkmod</c> files and
        /// its loose <c>Assets/</c> (inside <see cref="CacheFolder"/> for a zip). Null for project and built-in packs.
        /// </summary>
        public static string FolderOf(ContentPack root) => root != null && folderOf.TryGetValue(root, out var f) ? f : null;

        /// <summary>The loaded content bundle (<c>&lt;packId&gt;.mhkmod</c>) of the enabled mod <paramref name="root"/>, null for project and built-in packs and disabled mods.</summary>
        public static AssetBundle BundleOf(ContentPack root) => root != null && bundleOf.TryGetValue(root, out var b) ? b : null;

        static IEnumerable<string> SearchFolders
        {
            get
            {
                var option = CommandLine.Value(ModsOption);
                if (!string.IsNullOrEmpty(option))
                    foreach (var folder in option.Split(Path.PathSeparator))
                        if (folder.Length > 0) yield return PlatformFolder(folder);
                var editorFolders = EditorModFolders?.Invoke();
                if (editorFolders != null)
                    foreach (var folder in editorFolders) yield return PlatformFolder(folder);
                yield return UserFolder;
                yield return ShippedFolder;
            }
        }

        static string PlatformFolder(string folder)
        {
            var platform = Path.Combine(folder, ModSdk.CurrentBuildTarget);
            return Directory.Exists(platform) ? platform : folder;
        }

        // Every bundle of every search folder in search order, zips extracted on the way. Extractions of zips that are
        // gone are removed afterwards.
        static List<string> AllModFiles()
        {
            var files = new List<string>();
            var extracted = new List<string>();
            foreach (var folder in SearchFolders) files.AddRange(ModFiles(folder, extracted));
            ZipCache.Prune(CacheFolder, extracted);
            return files;
        }

        static IEnumerable<string> ModFiles(string folder, List<string> extracted)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (folder == ShippedFolder) return ApkModFiles(folder);
#endif
            try
            {
                if (!Directory.Exists(folder)) return Array.Empty<string>();
                var files = new List<string>();
                CollectBundles(folder, files);
                foreach (var zip in Directory.GetFiles(folder, "*" + ZipExtension).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var dir = Path.GetFileName(zip).StartsWith("._", StringComparison.Ordinal) ? null : ZipCache.Extract(zip, CacheFolder);
                    if (dir == null) continue;
                    extracted.Add(dir);
                    CollectBundles(dir, files);
                }
                files.Sort(StringComparer.Ordinal);
                return files;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mods] Cannot scan '{folder}': {e.Message}");
                return Array.Empty<string>();
            }
        }

        // A mod folder's Assets/ holds its loose files, never bundles. Zips made on macOS carry __MACOSX/ and ._ files.
        // Hidden folders (.<packId>.staging, .old, .pending of ModSources) are installs in progress.
        static void CollectBundles(string folder, List<string> files)
        {
            foreach (var f in Directory.GetFiles(folder, "*" + ModFileExtension))
                if (!Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal)) files.Add(f);
            foreach (var sub in Directory.GetDirectories(folder))
            {
                var name = Path.GetFileName(sub);
                if (!string.Equals(name, AssetsFolder, StringComparison.OrdinalIgnoreCase) && name != "__MACOSX" &&
                    !name.StartsWith(".", StringComparison.Ordinal)) CollectBundles(sub, files);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // StreamingAssets live inside the APK (or an install-time asset pack): the asset manager lists them, one level of
        // mod folders deep.
        static IEnumerable<string> ApkModFiles(string folder)
        {
            try
            {
                using var assets = UnityEngine.Android.AndroidApplication.currentContext.Call<AndroidJavaObject>("getAssets");
                var files = new List<string>();
                ApkList(assets, DataPaths.ModsFolder, folder, files);
                foreach (var sub in assets.Call<string[]>("list", DataPaths.ModsFolder) ?? Array.Empty<string>())
                    if (!sub.EndsWith(ModFileExtension, StringComparison.OrdinalIgnoreCase))
                        ApkList(assets, DataPaths.ModsFolder + "/" + sub, folder + "/" + sub, files);
                files.Sort(StringComparer.Ordinal);
                return files;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mods] Cannot list '{folder}': {e.Message}");
                return Array.Empty<string>();
            }
        }

        static void ApkList(AndroidJavaObject assets, string assetFolder, string folder, List<string> files)
        {
            foreach (var n in assets.Call<string[]>("list", assetFolder) ?? Array.Empty<string>())
                if (n.EndsWith(ModFileExtension, StringComparison.OrdinalIgnoreCase)) files.Add(folder + "/" + n);
        }
#endif
    }
}
