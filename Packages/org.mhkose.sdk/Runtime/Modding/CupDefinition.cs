using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A championship: an ordered list of tracks, each raced in the variant the cup sets for it.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Cup", fileName = "Cup")]
    public class CupDefinition : ContentDefinition
    {
        public List<TrackDefinition> tracks = new();
        [Tooltip("Variant key of each race, same order as tracks: option ids joined by '+' (e.g. core.clear or " +
                 "core.reverse+core.night). Empty or missing races the track's default variant.")]
        public List<string> variants = new();
        [Tooltip("Laps per race in this cup (0 = use track default).")]
        public int lapsOverride;
        [Tooltip("Sort key in menus.")]
        public int order;

        /// <summary>The variant key set for race <paramref name="index"/> (<see cref="variants"/>), "" when none is set.</summary>
        public string VariantKeyAt(int index) => index >= 0 && index < variants.Count ? variants[index] ?? "" : "";

        /// <summary>
        /// The variant race <paramref name="index"/> runs in: <see cref="VariantKeyAt"/> resolved on its track, else the
        /// track's default (the key names an option the track does not offer, e.g. a mod with that option is missing). An
        /// arena runs its circuit course (<see cref="TrackVariantSet.ForMode"/>). Null for a missing track or a track
        /// without a variant set.
        /// </summary>
        public TrackVariant VariantAt(int index)
        {
            var track = index >= 0 && index < tracks.Count ? tracks[index] : null;
            var set = track != null ? TrackVariants.Of(track) : null;
            return set?.ForMode(set.Resolve(VariantKeyAt(index)) ?? set.Default, false);
        }
    }
}
