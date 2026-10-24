using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Assets;
using UnityEditor;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// The project's replaceable textures and sounds for <see cref="GameAssets"/> in play mode, where the game and the
    /// project packs have no built index. Paths follow <see cref="PackAssetPaths"/>, the list is built once per play session.
    /// </summary>
    [InitializeOnLoad]
    public sealed class ProjectAssetSource : IEditorAssetSource
    {
        List<PackAssetPaths.PackFolder> folders;
        List<string> paths;
        Dictionary<string, string> assetPathOf;
        Dictionary<string, string> virtualOf;

        static ProjectAssetSource()
        {
            var source = new ProjectAssetSource();
            GameAssets.EditorSource = source;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode || s == PlayModeStateChange.EnteredEditMode) source.Forget();
            };
        }

        void Forget()
        {
            folders = null;
            paths = null;
            assetPathOf = null;
            virtualOf = null;
        }

        /// <inheritdoc/>
        public string PathOf(UnityEngine.Object asset)
        {
            var assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.IsMainAsset(asset)) return null;
            if (virtualOf != null) return virtualOf.TryGetValue(assetPath, out var known) ? known : null;
            return PackAssetPaths.ProjectPath(assetPath, Folders());
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> Paths()
        {
            Build();
            return paths;
        }

        /// <inheritdoc/>
        public UnityEngine.Object Load(string path)
        {
            Build();
            return assetPathOf.TryGetValue(path, out var assetPath) ? AssetDatabase.LoadMainAssetAtPath(assetPath) : null;
        }

        // The game's own packs count as packs too: the player's index lists their assets under their pack id.
        List<PackAssetPaths.PackFolder> Folders() =>
            folders ??= PackAssetPaths.Folders(ContentPackAssets.BuiltinPacks().Concat(ContentPackAssets.ShippedPacks()).Concat(ProjectModPacks.All()));

        void Build()
        {
            if (paths != null) return;
            var packFolders = Folders();
            var search = new List<string> { "Assets", SdkPaths.Root };
            foreach (var f in packFolders)
            {
                var folder = f.folder.TrimEnd('/');
                if (!search.Any(s => folder.StartsWith(s + "/", StringComparison.Ordinal))) search.Add(folder);
            }
            paths = new List<string>();
            assetPathOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            virtualOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D t:AudioClip", search.ToArray()))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (virtualOf.ContainsKey(assetPath)) continue;
                var path = PackAssetPaths.ProjectPath(assetPath, packFolders);
                if (path == null || !assetPathOf.TryAdd(path, assetPath)) continue;
                virtualOf[assetPath] = path;
                paths.Add(path);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }
}
