using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor
{
    /// <summary>Project asset path helpers (forward slashes, folders created on demand).</summary>
    public static class AssetPaths
    {
        /// <summary>Folder of an asset path ("Assets/A/b.asset" gives "Assets/A"), "" for a bare name.</summary>
        public static string DirectoryOf(string assetPath) =>
            string.IsNullOrEmpty(assetPath) ? "" : (Path.GetDirectoryName(assetPath) ?? "").Replace('\\', '/');

        /// <summary>Folder of a persistent asset, "" when it is not one.</summary>
        public static string DirectoryOf(Object asset) => DirectoryOf(AssetDatabase.GetAssetPath(asset));

        /// <summary>Creates the folder "Assets/…" and its missing parents. Null or empty does nothing.</summary>
        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = DirectoryOf(folder);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        /// <summary>Keeps letters, digits, '_', '-' and '.', replaces everything else with '_'.</summary>
        public static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// The <typeparamref name="T"/> asset at <paramref name="assetPath"/>, created with its folders when missing.
        /// Throws <see cref="IOException"/> when another asset type occupies the path.
        /// </summary>
        public static T LoadOrCreate<T>(string assetPath) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset != null) return asset;
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                throw new IOException($"{assetPath} exists but is not a {typeof(T).Name}");
            EnsureFolder(DirectoryOf(assetPath));
            asset = ScriptableObject.CreateInstance<T>();
            asset.name = Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(asset, assetPath);
            return asset;
        }
    }
}
