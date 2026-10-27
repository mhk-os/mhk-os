using System.Collections.Generic;
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

        // Reference fields whose content the loader removed because its pack is not loaded: field name → the ids as
        // declared (one per list entry, null for an empty entry).
        Dictionary<string, string[]> unresolvedReferences;

        /// <summary>
        /// The ids reference field <paramref name="field"/> declared when the loader removed some of them because their
        /// pack is not loaded (the field itself holds null or fewer entries then).
        /// </summary>
        internal bool TryGetUnresolved(string field, out string[] declaredIds)
        {
            declaredIds = null;
            return unresolvedReferences != null && unresolvedReferences.TryGetValue(field, out declaredIds);
        }

        /// <summary>Records the declared ids of <paramref name="field"/> before its first removal (later passes keep it).</summary>
        internal void RecordUnresolved(string field, string[] declaredIds)
        {
            unresolvedReferences ??= new Dictionary<string, string[]>();
            unresolvedReferences.TryAdd(field, declaredIds);
        }

#if UNITY_EDITOR
        public void EditorSetIcon(Texture2D tex) => icon = tex;
#endif
    }
}
