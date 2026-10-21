using System.Collections.Generic;
using UnityEditor;
using Object = UnityEngine.Object;

namespace MHK.Editor
{
    /// <summary>Asset database queries by type.</summary>
    public static class EditorAssets
    {
        /// <summary>
        /// Every main asset of type <typeparamref name="T"/> (searched by its type name), in database order, optionally
        /// only under <paramref name="folders"/>.
        /// </summary>
        public static IEnumerable<T> FindAll<T>(params string[] folders) where T : Object
        {
            var filter = "t:" + typeof(T).Name;
            var guids = folders is { Length: > 0 } ? AssetDatabase.FindAssetGUIDs(filter, folders) : AssetDatabase.FindAssetGUIDs(filter);
            foreach (var guid in guids)
            {
                var asset = AssetDatabase.LoadAssetByGUID<T>(guid);
                if (asset != null) yield return asset;
            }
        }

        /// <summary>Asset paths matching an AssetDatabase search <paramref name="filter"/>, optionally only under <paramref name="folders"/>.</summary>
        public static IEnumerable<string> FindPaths(string filter, params string[] folders)
        {
            var guids = folders is { Length: > 0 } ? AssetDatabase.FindAssetGUIDs(filter, folders) : AssetDatabase.FindAssetGUIDs(filter);
            foreach (var guid in guids) yield return AssetDatabase.GUIDToAssetPath(guid);
        }
    }
}
