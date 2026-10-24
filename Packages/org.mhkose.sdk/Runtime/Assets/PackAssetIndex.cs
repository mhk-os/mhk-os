using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Assets
{
    /// <summary>
    /// The replaceable textures and sounds of one content bundle (or of the player itself, pack id "game") with their
    /// virtual paths. Written by the build, read by <c>GameAssets</c>. The loose files sit in &lt;mod folder&gt;/Assets/&lt;path&gt;.
    /// </summary>
    public sealed class PackAssetIndex : ScriptableObject
    {
        /// <summary>Name of the player's own index in Resources.</summary>
        public const string GameResource = "GameAssetIndex";

        [Tooltip("Pack the bundle or the player belongs to.")]
        public string packId;
        [Tooltip("The textures and sounds are placeholders, the real files are in the mod folder's Assets/.")]
        public bool loose;
        public List<Entry> entries = new();

        /// <summary>A replaceable asset and its virtual path ("&lt;packId&gt;/&lt;path inside the pack&gt;" with the source file's extension).</summary>
        [Serializable]
        public struct Entry
        {
            [Tooltip("The asset, null for an asset only scenes of a bundle use (load it by bundleAsset when a scene needs it).")]
            public UnityEngine.Object asset;
            public string path;
            [Tooltip("Asset paths of the scenes that use the asset, empty when anything outside scenes (definitions, prefabs, Resources) uses it.")]
            public string[] scenes;
            [Tooltip("Name of a scene-only asset in the content bundle (its asset path), empty otherwise.")]
            public string bundleAsset;
        }
    }
}
