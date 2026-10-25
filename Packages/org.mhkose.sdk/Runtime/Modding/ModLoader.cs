using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MHK.Modding
{
    /// <summary>
    /// Discovers content packs and registers the enabled ones in load order: the game's own packs (built-in catalog,
    /// always on, always lowest), the game's base content <see cref="CorePackId"/> (shipped like a mod, always on, right
    /// above them), then editor project packs and mods (folders, zips and <c>.mhkmod</c> files in the dev mods folders,
    /// the user mods folder and <see cref="ShippedFolder"/>) from the bottom of the load order up, so a mod higher in
    /// the order replaces definitions with the same id. A mod built for another mod API level, or whose files or content
    /// fail to load or register, is left out for the session (<see cref="ProblemOf"/>), the others load as usual.
    /// </summary>
    public static partial class ModLoader
    {
        public const string ModFileExtension = ".mhkmod";
        public const string SceneBundleSuffix = ".scenes" + ModFileExtension;
        public const string BuiltinCatalogResource = "BuiltinContent";

        /// <summary>
        /// Pack id of the game's base content. It ships with the game in <see cref="ShippedFolder"/> like a mod, but it is
        /// part of the game: always loaded below every mod, never disabled, reordered or listed in the Mods screen.
        /// </summary>
        public const string CorePackId = "core";

        static readonly List<AssetBundle> loadedBundles = new();
        static readonly List<UnityEngine.Object> standIns = new();
        static readonly HashSet<string> bundleScenes = new();
        [AutoStaticsCleanup] static readonly List<ContentPack> ordered = new();
        [AutoStaticsCleanup] static readonly List<ContentPack> available = new();

        /// <summary>
        /// Editor only: project packs to load like installed mods (the shipped base content, plus the project's own mods
        /// with "Load Project Mods in Play Mode"), replacing an installed mod with the same pack id. Null in players. Set
        /// by the editor, so it survives play-mode entry.
        /// </summary>
        [NoAutoStaticsCleanup] public static Func<IEnumerable<ContentPack>> EditorProjectPacks { get; set; }

        /// <summary>
        /// Editor only: extra folders with built mods (MHK ▸ Modding ▸ Dev Mods Folders), searched before the user folder
        /// so a fresh build wins over an installed copy of the same pack. Null in players. Set by the editor.
        /// </summary>
        [NoAutoStaticsCleanup] public static Func<IEnumerable<string>> EditorModFolders { get; set; }

        /// <summary>
        /// Every mod found by the last <see cref="LoadAll"/>, enabled or disabled, in load order: first = highest priority
        /// (the Mods screen). The game's own packs and <see cref="CorePackId"/> are not listed. A disabled, incompatible or
        /// failed bundled mod is a stand-in that only carries its metadata and logo.
        /// </summary>
        public static IReadOnlyList<ContentPack> AvailablePacks => available;

        /// <summary><see cref="AvailablePacks"/> and the base content below them: every pack loaded from the project or from files.</summary>
        internal static IReadOnlyList<ContentPack> OrderedPacks => ordered;

        /// <summary>Whether <paramref name="pack"/> is the game's base content (<see cref="CorePackId"/>), not a mod.</summary>
        public static bool IsCore(ContentPack pack) => pack != null && string.Equals(pack.packId, CorePackId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="scenePath"/> comes from a loaded mod scene bundle (load it with SceneManager, not as a project scene).</summary>
        public static bool IsBundleScene(string scenePath) => bundleScenes.Contains(scenePath);

        // Bundles of the previous play session must be unloaded, not just forgotten, or they cannot load again.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void UnloadPreviousSession() => UnloadExternal();

        /// <summary>
        /// Loads the game's own packs and base content, then every mod not in <paramref name="disabledPackIds"/> into
        /// <paramref name="registry"/>, in the load order <paramref name="order"/> (pack ids, first = highest, see
        /// <see cref="ModOrder"/>). Both lists ignore <see cref="CorePackId"/>.
        /// </summary>
        public static void LoadAll(ContentRegistry registry, ICollection<string> disabledPackIds = null, IReadOnlyList<string> order = null)
        {
            ContentReferenceResolver.RestoreRecorded();
            DestroyStandIns();
            ordered.Clear();
            folderOf.Clear();
            bundleOf.Clear();
            sceneBundleOf.Clear();
            problems.Clear();
            RegisterBuiltin(registry, true);
            var packs = new List<ContentPack>();
            AddProjectPacks(packs);
            var sceneBundles = new List<string>();
            AddExternalPacks(packs, sceneBundles, disabledPackIds);
            ordered.AddRange(ModOrder.Sort(packs.Where(p => !IsCore(p)).ToList(), order));
            ordered.AddRange(packs.Where(IsCore));
            ListMods();
            LoadSceneBundles(sceneBundles);
            RegisterMods(registry, disabledPackIds);
        }

        /// <summary>Unloads every mod bundle and the stand-ins of disabled mods, after restoring the references resolved into the game's own packs.</summary>
        public static void UnloadExternal()
        {
            ContentReferenceResolver.RestoreRecorded();
            foreach (var b in loadedBundles)
                if (b != null) b.Unload(true);
            loadedBundles.Clear();
            bundleScenes.Clear();
            folderOf.Clear();
            bundleOf.Clear();
            sceneBundleOf.Clear();
            DestroyStandIns();
        }

        /// <summary>
        /// Points the definition references of the game's own and project packs back at the original definitions (the
        /// editor calls it when play mode ends, so project assets never keep a mod's replacement).
        /// </summary>
        public static void RestoreResolvedReferences() => ContentReferenceResolver.RestoreRecorded();

        static bool IsEnabled(ContentPack p, ICollection<string> disabledPackIds) =>
            disabledPackIds == null || IsCore(p) || !disabledPackIds.Contains(p.packId);

        static void ListMods()
        {
            available.Clear();
            foreach (var p in ordered)
                if (!IsCore(p)) available.Add(p);
        }

        static void RegisterBuiltin(ContentRegistry registry, bool log)
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
                if (log) Debug.Log($"[Mods] Game pack '{pack.packId}' ({pack.TotalContentCount} definitions, always on).");
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

        // A disabled or incompatible mod's bundle is unloaded right away: only a metadata stand-in stays for the Mods
        // screen. A file named like a pack already loaded from a bundle is skipped, a second copy of a loaded bundle cannot
        // load (Unity logs an error). folderOf gets the folder of each enabled pack's content bundle, where its scene bundle must be.
        static void AddExternalPacks(List<ContentPack> packs, List<string> sceneBundles, ICollection<string> disabledPackIds)
        {
            var bundleFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in AllModFiles())
            {
                if (file.EndsWith(SceneBundleSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    sceneBundles.Add(file);
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(file);
                if (bundleFolders.TryGetValue(name, out var loadedFrom))
                {
                    Debug.LogWarning($"[Mods] Pack '{name}' is already loaded from '{loadedFrom}': the copy in '{file}' is ignored.");
                    continue;
                }
                bundleFolders[name] = Path.GetDirectoryName(file);
                AddBundle(file, name, packs, disabledPackIds);
            }
        }

        // A bundle that does not load is listed as a failed mod named like its file.
        static void AddBundle(string file, string name, List<ContentPack> packs, ICollection<string> disabledPackIds)
        {
            AssetBundle bundle = null;
            IReadOnlyList<ContentPack> found;
            try
            {
                bundle = LooksLikeBundle(file) ? AssetBundle.LoadFromFile(file) : null;
                if (bundle == null) throw new ModLoadException(UnreadableFile);
                found = TopLevelPacks(bundle, file);
            }
            catch (Exception e)
            {
                if (bundle != null) bundle.Unload(true);
                if (packs.Any(p => string.Equals(p.packId, name, StringComparison.OrdinalIgnoreCase))) return;
                Fail(name, file, e);
                packs.Add(StandIn(name, name, 0));
                return;
            }
            var keep = false;
            foreach (var pack in found) keep |= AddPack(pack, bundle, file, packs, disabledPackIds);
            if (keep) loadedBundles.Add(bundle);
            else bundle.Unload(true);
        }

        static bool AddPack(ContentPack pack, AssetBundle bundle, string file, List<ContentPack> packs, ICollection<string> disabledPackIds)
        {
            if (packs.Any(p => SameId(p, pack)))
            {
                Debug.LogWarning($"[Mods] Pack '{pack.packId}' is already loaded: the copy in '{file}' is ignored.");
                return false;
            }
            var folder = Path.GetDirectoryName(file);
            try
            {
                ModSdk.LogStamp(pack, file);
                if (!CheckBuild(pack, file) || !IsEnabled(pack, disabledPackIds))
                {
                    packs.Add(StandIn(pack, folder));
                    return false;
                }
            }
            catch (Exception e)
            {
                Fail(pack.packId, file, e);
                packs.Add(StandIn(pack.packId, pack.title, pack.loadOrder));
                return false;
            }
            packs.Add(pack);
            folderOf[pack] = folder;
            bundleOf[pack] = bundle;
            return true;
        }

        static bool SameId(ContentPack a, ContentPack b) => string.Equals(a.packId, b.packId, StringComparison.OrdinalIgnoreCase);

        static IReadOnlyList<ContentPack> TopLevelPacks(AssetBundle bundle, string file)
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

        static ContentPack StandIn(ContentPack pack, string folder)
        {
            var s = StandIn(pack.packId, pack.title, pack.loadOrder);
            s.name = pack.name;
            s.author = pack.author;
            s.version = pack.version;
            s.description = pack.description;
            MHK.Assets.GameAssets.ApplyNow(pack.logo, pack.assetIndex, folder);
            s.logo = CopyTexture(pack.logo);
            if (s.logo != null) standIns.Add(s.logo);
            return s;
        }

        static ContentPack StandIn(string packId, string title, int loadOrder)
        {
            var s = ScriptableObject.CreateInstance<ContentPack>();
            s.name = packId;
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s.packId = packId;
            s.title = string.IsNullOrEmpty(title) ? packId : title;
            s.loadOrder = loadOrder;
            standIns.Add(s);
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

        // Scene bundles of enabled mods stay loaded so the race can load their scenes by path. They load from the top of
        // the load order down: a scene path a higher mod already provides is left to it. A mod whose scene bundle fails is dropped.
        static void LoadSceneBundles(List<string> files)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var pack = ordered[i];
                if (!folderOf.TryGetValue(pack, out var folder)) continue;
                var file = files.Find(f => Path.GetDirectoryName(f) == folder &&
                                           string.Equals(Path.GetFileName(f), pack.packId + SceneBundleSuffix, StringComparison.OrdinalIgnoreCase));
                if (file == null) continue;
                try
                {
                    LoadSceneBundle(file, pack);
                }
                catch (Exception e)
                {
                    Fail(pack.packId, file, e);
                    Drop(pack);
                }
            }
        }

        static void LoadSceneBundle(string file, ContentPack pack)
        {
            var bundle = LooksLikeBundle(file) ? AssetBundle.LoadFromFile(file) : null;
            if (bundle == null) throw new ModLoadException(UnreadableSceneFile);
            var paths = bundle.GetAllScenePaths();
            var shadowed = paths.Count(bundleScenes.Contains);
            if (shadowed > 0 && shadowed == paths.Length)
            {
                Debug.Log($"[Mods] Every scene of '{pack.packId}' comes from a mod higher in the load order: its scene bundle is not used.");
                bundle.Unload(true);
                return;
            }
            if (shadowed > 0)
                Debug.LogWarning($"[Mods] '{pack.packId}' has {shadowed} scenes with the same path as a mod higher in the load order. " +
                                 "Give a replacing track its own scene path.");
            loadedBundles.Add(bundle);
            sceneBundleOf[pack] = bundle;
            bundleScenes.UnionWith(paths);
        }
    }
}
