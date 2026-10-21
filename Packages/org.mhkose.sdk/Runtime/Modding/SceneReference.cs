using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// A scene asset referenced from content. The editor keeps the asset (so moves and renames are followed, see
    /// <c>TrackSceneSync</c>) and stores its path, which players use to load the scene from the build's scene list or
    /// from a mod's scene bundle.
    /// </summary>
    [Serializable]
    public class SceneReference
    {
#if UNITY_EDITOR
        [SerializeField] UnityEditor.SceneAsset asset;
#endif
        [SerializeField] string path = "";

        /// <summary>Project path of the scene ("Assets/.../name.unity"), empty when unset.</summary>
        public string Path => path;
        public bool IsSet => !string.IsNullOrEmpty(path);

#if UNITY_EDITOR
        /// <summary>Updates <see cref="Path"/> from the asset, true when it changed.</summary>
        public bool Sync()
        {
            var p = asset != null ? UnityEditor.AssetDatabase.GetAssetPath(asset) : "";
            if (p == path) return false;
            path = p;
            return true;
        }
#endif
    }
}
