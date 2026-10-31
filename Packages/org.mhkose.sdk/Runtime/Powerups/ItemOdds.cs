using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>One powerup random item boxes can give in a race and its pick weight by race position.</summary>
    public readonly struct ItemPoolEntry
    {
        public readonly PowerupDefinition powerup;
        readonly float scale;
        readonly bool flat;

        public ItemPoolEntry(PowerupDefinition powerup, float scale, bool flat)
        {
            this.powerup = powerup;
            this.scale = scale;
            this.flat = flat;
        }

        /// <summary>Pick weight at race position <paramref name="positionFraction"/> (0 = leader, 1 = last).</summary>
        public float WeightAt(float positionFraction) => flat ? scale : scale * powerup.WeightFor(positionFraction);
    }

    /// <summary>
    /// Item odds: every item box powerup of every loaded pack has the same chance on every track. Single races and
    /// battles use the player's own odds in percent (else <see cref="Defaults"/>), championships the defaults. Online
    /// races give only the lobby's powerups.
    /// </summary>
    public static partial class ItemOdds
    {
        const int AverageSamples = 11;
        const float Hundred = 100f;

        [AutoStaticsCleanup] static readonly List<PowerupOdds> scratch = new();

        /// <summary>Random item boxes may give <paramref name="p"/> (character specials are excluded).</summary>
        public static bool Eligible(PowerupDefinition p) => p != null && !p.excludeFromItemBoxes;

        /// <summary>Mean of <see cref="PowerupDefinition.WeightFor"/> over all race positions (leader to last).</summary>
        public static float AverageWeight(PowerupDefinition p)
        {
            var sum = 0f;
            for (var i = 0; i < AverageSamples; i++) sum += p.WeightFor(i / (AverageSamples - 1f));
            return sum / AverageSamples;
        }

        /// <summary>
        /// Default odds of every item box powerup (only the lobby content of <paramref name="lobbyPacks"/> when it is
        /// given): 100 % split by <see cref="AverageWeight"/>, the same on every track.
        /// </summary>
        public static void Defaults(ContentRegistry content, IReadOnlyList<string> lobbyPacks, List<PowerupOdds> into)
        {
            into.Clear();
            var all = content.All<PowerupDefinition>();
            var sum = 0f;
            for (var i = 0; i < all.Count; i++)
                if (Gives(content, all[i], lobbyPacks)) sum += AverageWeight(all[i]);
            for (var i = 0; i < all.Count; i++)
                if (Gives(content, all[i], lobbyPacks))
                    into.Add(new PowerupOdds(all[i].Id, sum > 0f ? Hundred * AverageWeight(all[i]) / sum : 0f));
        }

        static bool Gives(ContentRegistry content, PowerupDefinition p, IReadOnlyList<string> lobbyPacks) =>
            Eligible(p) && (lobbyPacks == null || ContentManifests.IsLobbyContent(content, p, lobbyPacks));

        /// <summary><see cref="Defaults"/>, with the percentages of <paramref name="custom"/> where it names a powerup (null = defaults only).</summary>
        public static void Resolve(ContentRegistry content, IReadOnlyList<string> lobbyPacks, IReadOnlyList<PowerupOdds> custom, List<PowerupOdds> into)
        {
            Defaults(content, lobbyPacks, into);
            if (custom == null) return;
            for (var i = 0; i < into.Count; i++)
                if (TryFind(custom, into[i].id, out var percent)) into[i] = new PowerupOdds(into[i].id, Mathf.Max(0f, percent));
        }

        /// <summary>
        /// The player's odds into <paramref name="into"/>: their own (<see cref="UserSettings.ownItemOdds"/>), else the
        /// defaults. True when the own odds apply.
        /// </summary>
        public static bool PlayerOdds(ContentRegistry content, UserSettings settings, List<PowerupOdds> into)
        {
            var own = settings.ownItemOdds.Count > 0;
            Resolve(content, null, own ? settings.ownItemOdds : null, into);
            return own;
        }

        /// <summary>The percentage <paramref name="odds"/> give powerup <paramref name="id"/>, false when it is not listed.</summary>
        public static bool TryFind(IReadOnlyList<PowerupOdds> odds, string id, out float percent)
        {
            for (var i = 0; i < odds.Count; i++)
            {
                if (odds[i].id != id) continue;
                percent = odds[i].percent;
                return true;
            }
            percent = 0f;
            return false;
        }

        /// <summary>Sum of all percentages (0 = item boxes give nothing).</summary>
        public static float Total(IReadOnlyList<PowerupOdds> odds)
        {
            var sum = 0f;
            for (var i = 0; i < odds.Count; i++) sum += Mathf.Max(0f, odds[i].percent);
            return sum;
        }

        /// <summary>
        /// Powerups the random item boxes give in <paramref name="session"/>: the session's odds (championships: the
        /// defaults), each powerup's position curve scaled so that its average over all places is its percentage.
        /// Online races give only the lobby's powerups, so every racer builds the same pool.
        /// </summary>
        public static void BuildPool(ContentRegistry content, GameSession session, List<ItemPoolEntry> into)
        {
            into.Clear();
            var custom = session != null && session.mode != GameMode.Championship && session.powerupOdds.Count > 0 ? session.powerupOdds : null;
            var lobbyPacks = session != null && session.online && OnlineLobby.Current != null ? OnlineLobby.Current.settings.packs : null;
            Resolve(content, lobbyPacks, custom, scratch);
            for (var i = 0; i < scratch.Count; i++)
            {
                var p = content.Get<PowerupDefinition>(scratch[i].id);
                var percent = scratch[i].percent;
                if (p == null || percent <= 0f) continue;
                var average = AverageWeight(p);
                into.Add(average > 0f ? new ItemPoolEntry(p, percent / average, false) : new ItemPoolEntry(p, percent, true));
            }
        }
    }
}
