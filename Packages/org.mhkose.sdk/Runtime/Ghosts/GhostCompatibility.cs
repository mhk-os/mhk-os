using System;
using MHK.Core;
using MHK.Modding;

namespace MHK.Ghosts
{
    /// <summary>
    /// Whether a ghost was driven with what this game runs now: decided by the rules version and the content versions of
    /// the track variant and kart, never by the game or pack commit, so builds that change no lap times keep every ghost
    /// and record valid.
    /// </summary>
    public enum GhostCompatibility
    {
        /// <summary>Same rules, track variant version and kart version: a fair rival and a valid record.</summary>
        Current,
        /// <summary>
        /// Recorded with another rules version (<see cref="RulesVersion"/>) or another version of its track variant or
        /// kart: shown and raced, never a record.
        /// </summary>
        OtherVersion,
        /// <summary>Its track, track variant or kart is not installed (or its lap count is invalid).</summary>
        OtherContent,
    }

    /// <summary>Decides <see cref="GhostCompatibility"/>.</summary>
    public static class GhostCompatibilityRules
    {
        /// <summary>
        /// Compatibility of <paramref name="h"/>: rules version, the installed track variant's content version and the
        /// installed kart's content version against those it was recorded with.
        /// </summary>
        public static GhostCompatibility Evaluate(GhostHeader h)
        {
            var content = GameRoot.Instance.Content;
            var track = content.Get<TrackDefinition>(h.trackId);
            var kart = content.Get<KartDefinition>(h.kartId);
            var variant = track != null ? TrackVariants.Resolve(track, h.variant) : null;
            if (variant == null || kart == null || h.laps < 1) return GhostCompatibility.OtherContent;
            return h.rulesVersion == RulesVersion.Current && h.trackVersion == variant.ContentVersion && h.kartVersion == kart.contentVersion
                ? GhostCompatibility.Current
                : GhostCompatibility.OtherVersion;
        }

        /// <summary>Whether <paramref name="h"/> was driven on variant <paramref name="variantKey"/> of its track over <paramref name="laps"/> laps.</summary>
        public static bool Matches(GhostHeader h, string variantKey, int laps) =>
            string.Equals(h.variant ?? "", variantKey ?? "", StringComparison.Ordinal) && h.laps == laps;
    }
}
