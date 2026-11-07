using System;
using System.Collections.Generic;
using MHK.Modding;

namespace MHK.Core
{
    /// <summary>
    /// Settings of one race of a custom championship: the track options the player fixed (every other modifier with a
    /// choice is rolled at random when the championship starts) and the laps. Field names are JSON keys
    /// (<see cref="UserSettings.customChampionshipSetups"/>): never rename them.
    /// </summary>
    [Serializable]
    public class CustomRaceSetup
    {
        /// <summary>Most laps a race can be set to.</summary>
        public const int MaxLaps = 9;

        /// <summary>
        /// Ids of the options the player fixed (<see cref="TrackOptionDefinition"/>), at most one per modifier. A modifier
        /// with none of its options here is random; ids the track does not offer are ignored.
        /// </summary>
        public List<string> options = new();
        /// <summary>Laps of the race, 1 to <see cref="MaxLaps"/>; 0 races the track's default laps.</summary>
        public int laps;

        /// <summary>A copy that shares nothing with this one.</summary>
        public CustomRaceSetup Clone() => new() { options = new List<string>(options), laps = laps };

        /// <summary>Index of the option of <paramref name="modifier"/> this setup fixes, -1 when it is left to chance.</summary>
        public int PickOf(TrackModifierChoice modifier)
        {
            foreach (var id in options)
            {
                var index = modifier.IndexOf(id);
                if (index >= 0) return index;
            }
            return -1;
        }

        /// <summary>Fixes option <paramref name="index"/> of <paramref name="modifier"/>, or leaves the modifier to chance for -1.</summary>
        public void SetPick(TrackModifierChoice modifier, int index)
        {
            options.RemoveAll(id => modifier.IndexOf(id) >= 0);
            if (index >= 0 && index < modifier.Options.Count) options.Add(modifier.Options[index].Id);
        }

        /// <summary>
        /// The variant raced on <paramref name="track"/>: the fixed options, a random option of every other modifier with a
        /// choice (<see cref="TrackVariantSet.Random"/> with <paramref name="rng"/>). Null when the track has no variant set.
        /// </summary>
        public TrackVariant Roll(TrackDefinition track, System.Random rng)
        {
            var set = TrackVariants.Of(track);
            if (set == null) return null;
            var variant = set.Random(rng);
            for (var m = 0; m < set.Modifiers.Count; m++)
            {
                if (!set.Modifiers[m].IsChoice) continue;
                var pick = PickOf(set.Modifiers[m]);
                if (pick >= 0) variant = set.With(variant, m, pick);
            }
            return variant;
        }
    }
}
