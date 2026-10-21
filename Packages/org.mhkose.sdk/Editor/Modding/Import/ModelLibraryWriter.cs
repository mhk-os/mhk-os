using System;
using System.Collections.Generic;
using MHK.Modding;
using UnityEditor;
using Object = UnityEngine.Object;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Stores imported meshes and animation clips as sub-assets of a <see cref="ModelLibrary"/>. Sub-assets are matched
    /// by type and name, so a re-import updates the existing objects in place and every reference to them stays valid.
    /// </summary>
    public sealed class ModelLibraryWriter
    {
        readonly Dictionary<(Type type, string name), Object> existing = new();

        public ModelLibrary Library { get; }

        public ModelLibraryWriter(ModelLibrary library)
        {
            Library = library;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(library)))
                if (asset != null && asset != library) existing.TryAdd((asset.GetType(), asset.name), asset);
        }

        /// <summary>Loads the library at <paramref name="assetPath"/> or creates it with its folders.</summary>
        public static ModelLibraryWriter Open(string assetPath) => new(AssetPaths.LoadOrCreate<ModelLibrary>(assetPath));

        /// <summary>
        /// Adds <paramref name="obj"/> as a sub-asset, or copies it over the stored sub-asset of the same type and name
        /// (destroying <paramref name="obj"/>) and returns the stored object. Assets are returned unchanged.
        /// </summary>
        public T Store<T>(T obj) where T : Object
        {
            if (obj == null || EditorUtility.IsPersistent(obj)) return obj;
            var key = (obj.GetType(), obj.name);
            if (existing.TryGetValue(key, out var stored) && stored != null)
            {
                EditorUtility.CopySerialized(obj, stored);
                stored.name = key.name;
                Object.DestroyImmediate(obj);
                EditorUtility.SetDirty(stored);
                return (T)stored;
            }
            AssetDatabase.AddObjectToAsset(obj, Library);
            existing[key] = obj;
            EditorUtility.SetDirty(Library);
            return obj;
        }

        public void Save() => AssetDatabase.SaveAssetIfDirty(Library);
    }
}
