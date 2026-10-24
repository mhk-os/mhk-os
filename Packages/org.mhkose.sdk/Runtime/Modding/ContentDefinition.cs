using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Base class of every piece of moddable content (characters, karts, tracks, cups, powerups, effects).
    /// Content is identified by a globally unique, namespaced <see cref="Id"/> ("mhk2.moorhuhn").
    /// A later-loaded pack may override an earlier definition by reusing its id.
    /// </summary>
    public abstract class ContentDefinition : ScriptableObject
    {
        [Tooltip("Globally unique id, namespaced by pack: '<pack>.<name>'.")]
        [SerializeField] string id;
        [SerializeField] string displayName;
        [Tooltip("Pack id of the original game this content comes from (mhke, mhk2, mhk3, mhkt) or the mod's pack id.")]
        [SerializeField] string sourceGame;
        [SerializeField] Texture2D icon;
        [Tooltip("Hidden content is registered but not offered in menus (e.g. variants, debug content).")]
        [SerializeField] bool hidden;

        public string Id => string.IsNullOrEmpty(id) ? name : id;
        public string DisplayName => string.IsNullOrEmpty(displayName) ? Id : displayName;
        public string SourceGame => sourceGame;
        public Texture2D Icon => icon;
        public bool Hidden => hidden;

        /// <summary>Pack that registered this definition (set by the registry at load time).</summary>
        public ContentPack OwnerPack { get; internal set; }

        /// <summary>Id of <see cref="OwnerPack"/>, "" when unregistered.</summary>
        public string OwnerPackId => OwnerPack != null ? OwnerPack.packId : "";

        /// <summary>Commit of <see cref="OwnerPack"/> (<see cref="ContentPack.version"/>), "" when unregistered or not built.</summary>
        public string OwnerPackCommit => OwnerPack != null ? OwnerPack.version : "";

#if UNITY_EDITOR
        public void EditorSetIcon(Texture2D tex) => icon = tex;
#endif
    }
}
