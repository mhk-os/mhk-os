using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
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
    /// Item families and odds. A family is a top-level pack with the packs it includes (the core games, or one mod) plus
    /// the powerups it declares shared (<see cref="ContentPack.sharedPowerups"/>). Championships give their cup's family,
    /// single races and battles give every powerup by odds in percent (the player's own, else <see cref="Defaults"/>).
    /// </summary>
    public static partial class ItemOdds
    {
        const int AverageSamples = 11;
        const int MaxIncludeDepth = 8;
        const float Hundred = 100f;

        [AutoStaticsCleanup] static readonly List<PowerupOdds> scratch = new();

        /// <summary>Random item boxes may give <paramref name="p"/> (character specials are excluded).</summary>
        public static bool Eligible(PowerupDefinition p) => p != null && !p.excludeFromItemBoxes;

        /// <summary>The item family (top-level pack) of <paramref name="def"/>, null when it is missing or unregistered.</summary>
        public static ContentPack FamilyOf(ContentRegistry content, ContentDefinition def) =>
            def != null ? content.RootOf(def.OwnerPack) : null;

        /// <summary>Settings key of <paramref name="family"/> (its pack id, "" for none).</summary>
        public static string FamilyKey(ContentPack family) => family != null ? family.packId : "";

        /// <summary>Item boxes of <paramref name="family"/> give <paramref name="p"/>: one of its own powerups or one it declares shared.</summary>
        public static bool InFamily(ContentRegistry content, PowerupDefinition p, ContentPack family) =>
            family != null && (content.RootOf(p.OwnerPack) == family || Shares(family, p.Id, 0));

        static bool Shares(ContentPack pack, string id, int depth)
        {
            if (pack == null || depth > MaxIncludeDepth) return false;
            if (pack.sharedPowerups != null && pack.sharedPowerups.Contains(id)) return true;
            for (var i = 0; i < pack.includes.Count; i++)
                if (Shares(pack.includes[i], id, depth + 1)) return true;
            return false;
        }

        /// <summary>Mean of <see cref="PowerupDefinition.WeightFor"/> over all race positions (leader to last).</summary>
        public static float AverageWeight(PowerupDefinition p)
        {
            var sum = 0f;
            for (var i = 0; i < AverageSamples; i++) sum += p.WeightFor(i / (AverageSamples - 1f));
            return sum / AverageSamples;
        }

        /// <summary>
        /// Default odds of every item box powerup on a track of <paramref name="family"/>: the family's powerups share
        /// <see cref="GameConfig.trackFamilyItemShare"/> of 100 %, all others the rest, each side split by
        /// <see cref="AverageWeight"/>. A side without powerups hands its share to the other.
        /// </summary>
        public static void Defaults(ContentRegistry content, ContentPack family, List<PowerupOdds> into)
        {
            into.Clear();
            var all = content.All<PowerupDefinition>();
            float own = 0f, other = 0f;
            for (var i = 0; i < all.Count; i++)
            {
                if (!Eligible(all[i])) continue;
                if (InFamily(content, all[i], family)) own += AverageWeight(all[i]);
                else other += AverageWeight(all[i]);
            }
            var ownShare = other <= 0f ? 1f : own <= 0f ? 0f : Mathf.Clamp01(GameRoot.Instance.Config.trackFamilyItemShare);
            for (var i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (!Eligible(p)) continue;
                var mine = InFamily(content, p, family);
                var side = mine ? own : other;
                var share = mine ? ownShare : 1f - ownShare;
                into.Add(new PowerupOdds(p.Id, side > 0f ? Hundred * share * AverageWeight(p) / side : 0f));
            }
        }

        /// <summary><see cref="Defaults"/>, with the percentages of <paramref name="custom"/> where it names a powerup (null = defaults only).</summary>
        public static void Resolve(ContentRegistry content, ContentPack family, IReadOnlyList<PowerupOdds> custom, List<PowerupOdds> into)
        {
            Defaults(content, family, into);
            if (custom == null) return;
            for (var i = 0; i < into.Count; i++)
                if (TryFind(custom, into[i].id, out var percent)) into[i] = new PowerupOdds(into[i].id, Mathf.Max(0f, percent));
        }

        /// <summary>
        /// The player's odds for races on <paramref name="track"/> into <paramref name="into"/>: their own set for the
        /// track's family, else the defaults. Returns the own set, null when the defaults apply.
        /// </summary>
        public static ItemOddsSet PlayerOdds(ContentRegistry content, UserSettings settings, TrackDefinition track, List<PowerupOdds> into)
        {
            var family = FamilyOf(content, track);
            var own = settings.ItemOddsFor(FamilyKey(family));
            Resolve(content, family, own != null ? own.odds : null, into);
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
        /// Powerups the random item boxes give in <paramref name="session"/> on <paramref name="track"/>. Championships:
        /// the cup's family (every powerup when it has none) with the powerups' own position weights. Other modes: the
        /// odds, each powerup's position curve scaled so that its average over all places is its percentage.
        /// </summary>
        public static void BuildPool(ContentRegistry content, GameSession session, TrackDefinition track, List<ItemPoolEntry> into)
        {
            into.Clear();
            if (session != null && session.mode == GameMode.Championship)
            {
                var cup = content.Get<CupDefinition>(session.cupId);
                FamilyPool(content, cup != null ? FamilyOf(content, cup) : FamilyOf(content, track), into);
                return;
            }
            var custom = session != null && session.powerupOdds.Count > 0 ? session.powerupOdds : null;
            Resolve(content, FamilyOf(content, track), custom, scratch);
            for (var i = 0; i < scratch.Count; i++)
            {
                var p = content.Get<PowerupDefinition>(scratch[i].id);
                var percent = scratch[i].percent;
                if (p == null || percent <= 0f) continue;
                var average = AverageWeight(p);
                into.Add(average > 0f ? new ItemPoolEntry(p, percent / average, false) : new ItemPoolEntry(p, percent, true));
            }
        }

        static void FamilyPool(ContentRegistry content, ContentPack family, List<ItemPoolEntry> into)
        {
            var all = content.All<PowerupDefinition>();
            for (var i = 0; i < all.Count; i++)
                if (Eligible(all[i]) && InFamily(content, all[i], family)) into.Add(new ItemPoolEntry(all[i], 1f, false));
            if (into.Count > 0) return;
            for (var i = 0; i < all.Count; i++)
                if (Eligible(all[i])) into.Add(new ItemPoolEntry(all[i], 1f, false));
        }
    }
}
