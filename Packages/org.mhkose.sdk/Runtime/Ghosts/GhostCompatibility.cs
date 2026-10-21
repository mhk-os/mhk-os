using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;

namespace MHK.Ghosts
{
    /// <summary>
    /// Whether a ghost was driven with what this game runs now: decided by the simulation version and the content
    /// fingerprint, never by the game version or commit, so builds that change no lap times keep every ghost and record valid.
    /// </summary>
    public enum GhostCompatibility
    {
        /// <summary>Same simulation version and the same track, rules, class and kart data: a fair rival and a valid record.</summary>
        Current,
        /// <summary>Recorded with another simulation version (<see cref="SimulationVersion"/>): shown and raced, never a record.</summary>
        OtherSimulation,
        /// <summary>Same simulation version, but the track, rules or kart data (or the lap count) differ, or its kart is not installed.</summary>
        OtherContent,
    }

    /// <summary>Decides <see cref="GhostCompatibility"/>. Setup hashes are memoised per (engine class, kart, laps) in a caller-owned cache.</summary>
    public static class GhostCompatibilityRules
    {
        /// <summary>
        /// Compatibility of <paramref name="h"/>: simulation version, track pack version, the setup hash recomputed for its
        /// class, kart and the track's current lap count, and the track hash when this build knows it.
        /// </summary>
        public static GhostCompatibility Evaluate(GhostHeader h, string knownTrackHash, Dictionary<(int, string, int), string> setupHashes = null)
        {
            if (h.simVersion != SimulationVersion.Current) return GhostCompatibility.OtherSimulation;
            var content = GameRoot.Instance.Content;
            var track = content.Get<TrackDefinition>(h.trackId);
            var kart = content.Get<KartDefinition>(h.kartId);
            if (track == null || kart == null) return GhostCompatibility.OtherContent;
            var pack = track.OwnerPack;
            if (pack != null && (pack.packId != h.trackPackId || pack.version != h.trackPackVersion)) return GhostCompatibility.OtherContent;
            if (SetupHash(h.engineClass, kart, track.defaultLaps, setupHashes) != h.setupHash) return GhostCompatibility.OtherContent;
            if (!string.IsNullOrEmpty(knownTrackHash) && knownTrackHash != h.trackHash) return GhostCompatibility.OtherContent;
            return GhostCompatibility.Current;
        }

        static string SetupHash(int engineClass, KartDefinition kart, int laps, Dictionary<(int, string, int), string> cache)
        {
            if (cache == null) return GhostFingerprint.SetupHash((Difficulty)engineClass, kart, laps);
            var key = (engineClass, kart.Id, laps);
            if (!cache.TryGetValue(key, out var hash)) cache[key] = hash = GhostFingerprint.SetupHash((Difficulty)engineClass, kart, laps);
            return hash;
        }
    }
}
