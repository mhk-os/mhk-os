using System;
using System.Collections.Generic;
using System.IO;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Assets
{
    /// <summary>The registered asset indexes of the loaded content (the player's and one per loaded mod bundle) and where their files are.</summary>
    static partial class Catalog
    {
        /// <summary>One registered index: its loose folder (holding &lt;entry.path&gt;) and its content bundle for scene-only entries.</summary>
        internal sealed class Source
        {
            public PackAssetIndex index;
            public string looseRoot;
            public AssetBundle bundle;
        }

        /// <summary>An entry of a registered index.</summary>
        internal readonly struct Located
        {
            public readonly Source source;
            public readonly int entry;

            public Located(Source source, int entry)
            {
                this.source = source;
                this.entry = entry;
            }

            public PackAssetIndex.Entry Entry => source.index.entries[entry];
        }

        const string AssetsFolder = "Assets";
        const string CorePackFolder = "core";

        [AutoStaticsCleanup] static readonly List<Source> sources = new();
        [AutoStaticsCleanup] static readonly Dictionary<UnityEngine.Object, string> pathOf = new();
        [AutoStaticsCleanup] static readonly Dictionary<string, Located> byPath = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly Dictionary<string, List<Located>> byScene = new(StringComparer.OrdinalIgnoreCase);

        internal static IReadOnlyList<Source> Sources => sources;

        /// <summary>Registers the player's index and the index of every enabled bundle-loaded mod.</summary>
        internal static void Register()
        {
            Clear();
            var game = Resources.Load<PackAssetIndex>(PackAssetIndex.GameResource);
            var install = DataPaths.InstallRoot;
            if (game != null)
                Add(game, install != null ? Path.Combine(install, DataPaths.ModsFolder, CorePackFolder, AssetsFolder) : null, null);
            foreach (var pack in ModLoader.AvailablePacks)
            {
                var folder = ModLoader.FolderOf(pack);
                if (pack.assetIndex != null && folder != null) Add(pack.assetIndex, Path.Combine(folder, AssetsFolder), ModLoader.BundleOf(pack));
            }
        }

        internal static void Clear()
        {
            sources.Clear();
            pathOf.Clear();
            byPath.Clear();
            byScene.Clear();
        }

        static void Add(PackAssetIndex index, string looseRoot, AssetBundle bundle)
        {
            var source = new Source { index = index, looseRoot = looseRoot, bundle = bundle };
            sources.Add(source);
            var entries = index.entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (string.IsNullOrEmpty(e.path)) continue;
                var at = new Located(source, i);
                byPath.TryAdd(e.path, at);
                if (e.asset != null) pathOf.TryAdd(e.asset, e.path);
                if (e.scenes == null) continue;
                foreach (var scene in e.scenes) AddScene(scene, at);
            }
            Debug.Log($"[Assets] Index '{index.packId}': {entries.Count} replaceable assets{(index.loose ? ", loose files in '" + looseRoot + "'" : "")}.");
        }

        static void AddScene(string scene, Located at)
        {
            if (string.IsNullOrEmpty(scene)) return;
            if (!byScene.TryGetValue(scene, out var list)) byScene[scene] = list = new List<Located>();
            list.Add(at);
        }

        internal static bool TryPathOf(UnityEngine.Object asset, out string path) => pathOf.TryGetValue(asset, out path);

        internal static void Remember(UnityEngine.Object asset, string path)
        {
            if (asset != null) pathOf.TryAdd(asset, path);
        }

        internal static bool TryFind(string path, out Located at) => byPath.TryGetValue(path, out at);

        internal static IEnumerable<string> AllPaths() => byPath.Keys;

        /// <summary>Entries only the scene at <paramref name="scenePath"/> (and maybe others) uses, empty when none.</summary>
        internal static IReadOnlyList<Located> SceneEntries(string scenePath) =>
            scenePath != null && byScene.TryGetValue(scenePath, out var list) ? list : Array.Empty<Located>();

        internal static bool IsSceneOnly(in PackAssetIndex.Entry e) => e.scenes != null && e.scenes.Length > 0;

        /// <summary>
        /// The file that replaces <paramref name="path"/>: the highest enabled asset pack's, else the loose file next to
        /// <paramref name="source"/>'s bundle when its index is loose. Null when the bundled asset stays.
        /// </summary>
        internal static string WinningFile(string path, Source source)
        {
            var file = AssetPacks.FileFor(path);
            if (file != null) return file;
            if (source == null || !source.index.loose || source.looseRoot == null) return null;
            var loose = Path.Combine(source.looseRoot, path);
            return File.Exists(loose) ? loose : null;
        }

        /// <summary>The asset of an entry: its reference, else (scene-only) loaded from the content bundle by name.</summary>
        internal static UnityEngine.Object Load(in Located at)
        {
            var e = at.Entry;
            if (e.asset != null) return e.asset;
            if (at.source.bundle == null || string.IsNullOrEmpty(e.bundleAsset)) return null;
            var asset = GameAssets.KindOf(e.path) == GameAssetKind.Texture
                ? at.source.bundle.LoadAsset<Texture2D>(e.bundleAsset)
                : (UnityEngine.Object)at.source.bundle.LoadAsset<AudioClip>(e.bundleAsset);
            Remember(asset, e.path);
            return asset;
        }
    }
}
