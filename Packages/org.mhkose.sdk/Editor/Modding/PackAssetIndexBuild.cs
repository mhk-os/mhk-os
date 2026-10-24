using System.Collections.Generic;
using MHK.Assets;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>Creates the temporary <see cref="PackAssetIndex"/> assets of a build. The journal deletes them afterwards.</summary>
    public static class PackAssetIndexBuild
    {
        /// <summary>Folder of the indexes of mod bundles during their build.</summary>
        public const string TempFolder = "Assets/MHKBuildIndex";

        /// <summary>
        /// Creates an index of <paramref name="items"/> at <paramref name="assetPath"/>, recorded in
        /// <paramref name="journal"/>. With <paramref name="bundle"/> a scene-only asset is named, not referenced, so
        /// loading the pack does not load every track's textures.
        /// </summary>
        public static PackAssetIndex Create(string assetPath, string packId, bool loose, IReadOnlyList<PackAssetPaths.Item> items,
                                            bool bundle, BuildRestoreJournal journal)
        {
            var folder = AssetPaths.DirectoryOf(assetPath);
            if (!AssetDatabase.IsValidFolder(folder))
            {
                journal.Save(folder);
                AssetPaths.EnsureFolder(folder);
            }
            journal.Save(assetPath);
            journal.Save(assetPath + ".meta");
            var index = ScriptableObject.CreateInstance<PackAssetIndex>();
            index.packId = packId;
            index.loose = loose;
            foreach (var item in items)
            {
                var named = bundle && item.scenes.Length > 0;
                index.entries.Add(new PackAssetIndex.Entry
                {
                    asset = named ? null : AssetDatabase.LoadMainAssetAtPath(item.assetPath),
                    path = item.path,
                    scenes = item.scenes,
                    bundleAsset = named ? item.assetPath : "",
                });
            }
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(index, assetPath);
            return index;
        }
    }
}
