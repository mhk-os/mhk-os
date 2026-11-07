using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Makes a track offer an option (<see cref="TrackOptionDefinition"/>), optionally with an overlay scene loaded on top of
    /// the track scene while the option is selected (ramps for reverse, a night sky and lamps, rain). Any mod may add
    /// overlays to any track: for one track and option the definition of the mod loaded last wins, whatever its id.
    /// Ids follow '&lt;track id&gt;.&lt;option&gt;' (mhk2.island.rain).
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Track Overlay", fileName = "TrackOverlay")]
    public class TrackOverlayDefinition : ContentDefinition
    {
        [Tooltip("Id of the track that offers the option.")]
        public string trackId;
        [Tooltip("Id of the TrackOptionDefinition offered.")]
        public string optionId;
        [Tooltip("The track is in this option unless a variant picks another one of the modifier (the castle at night). " +
                 "Without a default the modifier's first option is.")]
        public bool isDefault;
        [Tooltip("Overlay scene loaded on top of the track scene while the option is selected: an 'Overlay' root with a " +
                 "TrackOverlay. Empty = the option only brings its rules.")]
        public SceneReference scene = new();
        [Tooltip("Version of what this overlay changes about driving. Bump it when the overlay's geometry or zones change lap " +
                 "times: records of the variants with it start over.")]
        [Min(1)] public int contentVersion = 1;

#if UNITY_EDITOR
        void OnValidate()
        {
            if (!string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(this))) scene.Sync();
        }
#endif
    }
}
