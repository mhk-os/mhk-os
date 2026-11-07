using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// One option of a <see cref="TrackModifierDefinition"/> (reverse, night, rain) and the gameplay rules it brings to every
    /// track that offers it. What it looks like on a track is up to that track's overlay scene
    /// (<see cref="TrackOverlayDefinition.scene"/>).
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Track Option", fileName = "TrackOption")]
    public class TrackOptionDefinition : ContentDefinition
    {
        [Tooltip("Id of the TrackModifierDefinition this option belongs to (e.g. 'core.time').")]
        public string modifierId;
        [Tooltip("Sort key among the options of the modifier.")]
        public int order;
        [Tooltip("Gameplay rules of the option, the same on every track.")]
        public TrackOptionRules rules = new();
    }

    /// <summary>
    /// Gameplay rules of a track option. A variant combines the rules of its selected options (<see cref="Combine"/>).
    /// </summary>
    [Serializable]
    public class TrackOptionRules
    {
        [Tooltip("The track runs the other way: racing lines, start grid, speed pads and respawn points are turned round.")]
        public bool reverse;
        [Tooltip("Karts start with their headlights on and computer drivers keep them on.")]
        public bool headlights;
        [Tooltip("Tyre grip multiplier where nothing covers the track (wet road). 1 = unchanged.")]
        [Range(0.2f, 1f)] public float outdoorGrip = 1f;
        [Tooltip("Wet look of the surfaces nothing covers: darker ground, highlights and puddles. 0 = dry.")]
        [Range(0f, 1f)] public float wetness;

        /// <summary>Adds <paramref name="other"/> to these rules: flags or-ed, grips multiplied, the wetter look kept.</summary>
        public void Combine(TrackOptionRules other)
        {
            if (other == null) return;
            reverse |= other.reverse;
            headlights |= other.headlights;
            outdoorGrip *= other.outdoorGrip;
            wetness = Mathf.Max(wetness, other.wetness);
        }
    }
}
