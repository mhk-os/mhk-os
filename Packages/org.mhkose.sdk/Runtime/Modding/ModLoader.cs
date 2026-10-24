using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MHK.Modding
{
    /// <summary>
    /// Discovers content packs and registers the enabled ones in load order: the game's own packs (built-in catalog,
    /// always on), editor project packs and <c>.mhkmod</c> AssetBundles in the dev mods folders, the user mods folder
    /// and the build's <see cref="ShippedFolder"/> (the base content).
    /// </summary>
    public static partial class ModLoader
    {
        public const string ModFileExtension = ".mhkmod";
        public const string SceneBundleSuffix = ".scenes" + ModFileExtension;
        public const string BuiltinCatalogResource = "BuiltinContent";

        static readonly List<AssetBundle> loadedBundles = new();
        static readonly List<UnityEngine.Object> standIns = new();
        static readonly HashSet<string> bundleScenes = new();
        [AutoStaticsCleanup] static readonly List<ContentPack> available = new();

        /// <summary>
        /// Editor only: project packs to load like installed mods (the shipped base content, plus the project's own mods
        /// with "Load Project Mods in Play Mode"), replacing an installed mod with the same pack id. Null in players. Set
        /// by the editor, so it survives play-mode entry.
        /// </summary>
        [NoAutoStaticsCleanup] public static Func<IEnumerable<ContentPack>> EditorProjectPacks { get; set; }

        /// <summary>
        /// Editor only: extra folders with built mods (MHK ▸ Modding ▸ Dev Mods Folders), searched before the user folder
        /// so a fresh build wins over an installed copy. Null in players. Set by the editor.
        /// </summary>
        [NoAutoStaticsCleanup] public static Func<IEnumerable<string>> EditorModFolders { get; set; }

        /// <summary>
        /// Command-line option with extra mod folders (several joined by the path separator), searched first. Like the
        /// dev mods folders, a folder with a subfolder named after this platform (a mod build output) is searched there.
        /// </summary>
        public const string ModsOption = "-mods";

        /// <summary>Folder for user-installed mods (shown in the Mods menu).</summary>
        public static string UserFolder => DataPaths.Mods;

        /// <summary>Mods that ship with the game (the base content), StreamingAssets/Mods of the build.</summary>
        public static string ShippedFolder => Path.Combine(Application.streamingAssetsPath, DataPaths.ModsFolder);

        /// <summary>
        /// Every mod found by the last <see cref="LoadAll"/>, enabled or disabled, in load order (the Mods screen). The
        /// game's own packs are not listed. A disabled bundled mod is a stand-in that only carries its metadata and logo.
        /// </summary>
        public static IReadOnlyList<ContentPack> AvailablePacks => available;

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

        /// <summary>Whether <paramref name="scenePath"/> comes from a loaded mod scene bundle (load it with SceneManager, not as a project scene).</summary>
        public static bool IsBundleScene(string scenePath) => bundleScenes.Contains(scenePath);

        // Bundles of the previous play session must be unloaded, not just forgotten, or they cannot load again.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void UnloadPreviousSession() => UnloadExternal();

        /// <summary>Loads the game's own packs, then every enabled mod, into <paramref name="registry"/> in load order.</summary>
        public static void LoadAll(ContentRegistry registry, ICollection<string> disabledPackIds = null)
        {
            DestroyStandIns();
            available.Clear();
            RegisterBuiltin(registry);
            var packs = new List<ContentPack>();
            AddProjectPacks(packs);
            var sceneBundles = new List<string>();
            var bundleFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var external = AddExternalPacks(packs, sceneBundles, bundleFolders, disabledPackIds);
            LoadSceneBundles(sceneBundles, bundleFolders);
            RegisterEnabled(registry, packs, disabledPackIds);
            foreach (var pack in external) ResolveReferences(registry, pack);
        }

        /// <summary>Unloads every mod bundle and the stand-ins of disabled mods.</summary>
        public static void UnloadExternal()
        {
            foreach (var b in loadedBundles)
                if (b != null) b.Unload(true);
            loadedBundles.Clear();
            bundleScenes.Clear();
            DestroyStandIns();
        }

        static bool IsEnabled(ContentPack p, ICollection<string> disabledPackIds) =>
            disabledPackIds == null || !disabledPackIds.Contains(p.packId);

        static void RegisterBuiltin(ContentRegistry registry)
        {
            var catalog = Resources.Load<BuiltinContentCatalog>(BuiltinCatalogResource);
            if (catalog == null)
            {
                Debug.LogError($"[Mods] No Resources/{BuiltinCatalogResource} catalog found: the game's own definitions are missing.");
                return;
            }
            foreach (var pack in catalog.RootPacks())
            {
                registry.RegisterRoot(pack);
                Debug.Log($"[Mods] Game pack '{pack.packId}' ({pack.TotalContentCount} definitions, always on).");
            }
        }

        static void AddProjectPacks(List<ContentPack> packs)
        {
            if (EditorProjectPacks == null) return;
            var projectPacks = EditorProjectPacks();
            if (projectPacks == null) return;
            foreach (var p in projectPacks.Where(p => p != null && packs.All(b => b.packId != p.packId)).ToList())
            {
                packs.Add(p);
                Debug.Log($"[Mods] Pack '{p.packId}' loaded from the project (editor).");
            }
        }

        // A disabled mod's bundle is unloaded right away: only a metadata stand-in stays for the Mods screen. A file named
        // like a pack already loaded from a bundle is skipped, a second copy of a loaded bundle cannot load (Unity logs an
        // error). bundleFolders gets the folder of each enabled pack's content bundle, where its scene bundle must be.
        static List<ContentPack> AddExternalPacks(List<ContentPack> packs, List<string> sceneBundles, Dictionary<string, string> bundleFolders,
            ICollection<string> disabledPackIds)
        {
            var enabled = new List<ContentPack>();
            foreach (var folder in SearchFolders)
            foreach (var file in ModFiles(folder))
            {
                if (file.EndsWith(SceneBundleSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    sceneBundles.Add(file);
                    continue;
                }
                if (bundleFolders.TryGetValue(Path.GetFileNameWithoutExtension(file), out var loadedFrom))
                {
                    Debug.LogWarning($"[Mods] Pack '{Path.GetFileNameWithoutExtension(file)}' is already loaded from '{loadedFrom}': the copy in '{file}' is ignored.");
                    continue;
                }
                var bundle = AssetBundle.LoadFromFile(file);
                if (bundle == null)
                {
                    Debug.LogWarning($"[Mods] Mod bundle '{file}' could not be loaded (wrong platform, corrupt, or already loaded).");
                    continue;
                }
                if (RegisterPacks(bundle, file, packs, enabled, bundleFolders, disabledPackIds)) loadedBundles.Add(bundle);
                else bundle.Unload(true);
            }
            return enabled;
        }

        static bool RegisterPacks(AssetBundle bundle, string file, List<ContentPack> packs, List<ContentPack> enabled,
            Dictionary<string, string> bundleFolders, ICollection<string> disabledPackIds)
        {
            var keep = false;
            foreach (var pack in TopLevelPacks(bundle, file))
            {
                if (packs.Any(p => SameId(p, pack)))
                    Debug.LogWarning($"[Mods] Pack '{pack.packId}' is already loaded: the copy in '{file}' is ignored.");
                else if (!IsEnabled(pack, disabledPackIds)) packs.Add(StandIn(pack));
                else
                {
                    packs.Add(pack);
                    enabled.Add(pack);
                    bundleFolders[pack.packId] = Path.GetDirectoryName(file);
                    ModSdk.LogStamp(pack, file);
                    keep = true;
                }
            }
            return keep;
        }

        static bool SameId(ContentPack a, ContentPack b) => string.Equals(a.packId, b.packId, StringComparison.OrdinalIgnoreCase);

        static IEnumerable<ContentPack> TopLevelPacks(AssetBundle bundle, string file)
        {
            if (bundle.isStreamedSceneAssetBundle)
            {
                Debug.LogWarning($"[Mods] '{file}' holds scenes: name it '<packId>{SceneBundleSuffix}' next to its content bundle.");
                return Array.Empty<ContentPack>();
            }
            var all = bundle.LoadAllAssets<ContentPack>();
            var included = new HashSet<ContentPack>(all.SelectMany(p => p.SelfAndIncluded().Where(x => x != p)));
            return all.Where(p => !included.Contains(p)).ToList();
        }

        static ContentPack StandIn(ContentPack pack)
        {
            var s = ScriptableObject.CreateInstance<ContentPack>();
            s.name = pack.name;
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s.packId = pack.packId;
            s.title = pack.title;
            s.author = pack.author;
            s.version = pack.version;
            s.description = pack.description;
            s.loadOrder = pack.loadOrder;
            s.logo = CopyTexture(pack.logo);
            standIns.Add(s);
            if (s.logo != null) standIns.Add(s.logo);
            return s;
        }

        // A GPU copy: the source texture goes with its bundle and is not readable.
        static Texture2D CopyTexture(Texture2D source)
        {
            if (source == null || SystemInfo.copyTextureSupport == UnityEngine.Rendering.CopyTextureSupport.None) return null;
            var copy = new Texture2D(source.width, source.height, source.graphicsFormat, source.mipmapCount, TextureCreationFlags.None)
            {
                name = source.name,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
                filterMode = source.filterMode,
                wrapMode = source.wrapMode,
            };
            Graphics.CopyTexture(source, copy);
            return copy;
        }

        static void DestroyStandIns()
        {
            foreach (var o in standIns)
            {
                if (o == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(o);
                else UnityEngine.Object.DestroyImmediate(o);
            }
            standIns.Clear();
        }

        // Scene bundles of enabled mods stay loaded so the race can load their scenes by path.
        static void LoadSceneBundles(List<string> files, Dictionary<string, string> bundleFolders)
        {
            foreach (var file in files)
            {
                var id = Path.GetFileName(file);
                id = id.Substring(0, id.Length - SceneBundleSuffix.Length);
                if (!bundleFolders.TryGetValue(id, out var folder) || Path.GetDirectoryName(file) != folder) continue;
                var bundle = AssetBundle.LoadFromFile(file);
                if (bundle == null)
                {
                    Debug.LogWarning($"[Mods] Scene bundle '{file}' could not be loaded (wrong platform, corrupt, or already loaded).");
                    continue;
                }
                loadedBundles.Add(bundle);
                bundleScenes.UnionWith(bundle.GetAllScenePaths());
            }
        }

        static void RegisterEnabled(ContentRegistry registry, List<ContentPack> packs, ICollection<string> disabledPackIds)
        {
            foreach (var pack in packs.OrderBy(p => p.loadOrder))
            {
                available.Add(pack);
                if (!IsEnabled(pack, disabledPackIds))
                {
                    Debug.Log($"[Mods] Pack '{pack.packId}' disabled by settings.");
                    continue;
                }
                registry.RegisterRoot(pack);
                var commit = string.IsNullOrEmpty(pack.version) ? "" : $" (commit {pack.version})";
                Debug.Log($"[Mods] Loaded pack '{pack.packId}'{commit}, load order {pack.loadOrder} ({pack.TotalContentCount} definitions" +
                          (pack.includes.Count > 0 ? $" in {pack.SelfAndIncluded().Count()} packs" : "") + ").");
            }
        }

        static void ResolveReferences(ContentRegistry registry, ContentPack root)
        {
            foreach (var p in root.SelfAndIncluded())
            {
                var remapped = ContentReferenceResolver.Resolve(registry, p);
                if (remapped > 0) Debug.Log($"[Mods] '{p.packId}': {remapped} references to other packs' content resolved by id.");
            }
        }

        static IEnumerable<string> ModFiles(string folder)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (folder == ShippedFolder) return ApkModFiles(folder);
#endif
            try
            {
                return Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*" + ModFileExtension, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mods] Cannot scan '{folder}': {e.Message}");
                return Array.Empty<string>();
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // StreamingAssets live inside the APK (or an install-time asset pack): the asset manager lists them.
        static IEnumerable<string> ApkModFiles(string folder)
        {
            try
            {
                using var assets = UnityEngine.Android.AndroidApplication.currentContext.Call<AndroidJavaObject>("getAssets");
                var names = assets.Call<string[]>("list", DataPaths.ModsFolder) ?? Array.Empty<string>();
                return names.Where(n => n.EndsWith(ModFileExtension, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.Ordinal).Select(n => folder + "/" + n).ToArray();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mods] Cannot list '{folder}': {e.Message}");
                return Array.Empty<string>();
            }
        }
#endif
    }
}
