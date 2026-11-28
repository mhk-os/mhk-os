using System;
using System.Collections.Generic;
using MHK.Achievements;
using MHK.Core;
using MHK.Modding;
using UnityEngine;

namespace MHK.Cosmetics
{
    /// <summary>
    /// The player's cosmetics, kept in the settings. <see cref="UserSettings.wornCosmetics"/> lists what the player wears
    /// (newest first, the first loaded and unlocked one of each slot counts) and <see cref="UserSettings.cosmeticColors"/>
    /// the colour picked for each cosmetic. Ids of packs that are not loaded stay stored and are ignored, like a
    /// cosmetic whose achievement is not unlocked.
    /// </summary>
    public static class CosmeticWardrobe
    {
        // The base colours offered for every tintable cosmetic, before the player's own one.
        static readonly Color32[] Base =
        {
            new(245, 245, 245, 255), new(40, 40, 44, 255), new(140, 144, 150, 255), new(220, 38, 38, 255),
            new(249, 115, 22, 255), new(250, 204, 21, 255), new(34, 197, 94, 255), new(20, 184, 166, 255),
            new(37, 99, 235, 255), new(147, 51, 234, 255), new(236, 72, 153, 255), new(133, 77, 39, 255),
        };

        /// <summary>The base colours every tintable cosmetic comes in (white, black, gray, red, orange, yellow, green, teal, blue, purple, pink, brown).</summary>
        public static IReadOnlyList<Color32> BaseColors => Base;

        /// <summary>Whether the player may wear <paramref name="cosmetic"/>: free from the start or its achievement is unlocked.</summary>
        public static bool IsUnlocked(CosmeticDefinition cosmetic) =>
            cosmetic != null && (cosmetic.UnlockedByDefault || Achievements.Achievements.IsUnlocked(cosmetic.unlockAchievement));

        /// <summary>The loaded achievement that unlocks <paramref name="cosmetic"/>, null when it is free or the achievement is not loaded.</summary>
        public static AchievementView UnlockedBy(CosmeticDefinition cosmetic) =>
            cosmetic == null || cosmetic.UnlockedByDefault ? null : Achievements.Achievements.Find(cosmetic.unlockAchievement);

        /// <summary>The cosmetic the player wears in <paramref name="slot"/>, null for none.</summary>
        public static CosmeticDefinition Worn(UserSettings settings, ContentRegistry content, CosmeticSlot slot)
        {
            foreach (var id in settings.wornCosmetics)
            {
                var cosmetic = content.Get<CosmeticDefinition>(id);
                if (cosmetic != null && cosmetic.slot == slot && IsUnlocked(cosmetic)) return cosmetic;
            }
            return null;
        }

        /// <summary>What the player wears, one cosmetic per slot with its colour, as racers and the menu turntable show it.</summary>
        public static List<CosmeticChoice> WornChoices(UserSettings settings, ContentRegistry content)
        {
            var worn = new List<CosmeticChoice>();
            if (settings == null || content == null) return worn;
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                var cosmetic = Worn(settings, content, slot);
                if (cosmetic != null) worn.Add(new CosmeticChoice(cosmetic.Id, ColorOf(settings, cosmetic)));
            }
            return worn;
        }

        /// <summary>
        /// A random outfit of the cosmetics the player has unlocked, for a computer driver (<see cref="UserSettings.aiCosmetics"/>).
        /// Each slot holds one of them or stays empty, a tintable one in a random base colour.
        /// </summary>
        public static List<CosmeticChoice> RandomOutfit(ContentRegistry content, System.Random random)
        {
            var outfit = new List<CosmeticChoice>();
            if (content == null) return outfit;
            var candidates = new List<CosmeticDefinition>();
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                candidates.Clear();
                foreach (var cosmetic in content.All<CosmeticDefinition>())
                    if (cosmetic.slot == slot && cosmetic.prefab != null && IsUnlocked(cosmetic)) candidates.Add(cosmetic);
                // The draw past the last cosmetic leaves the slot empty.
                var pick = random.Next(candidates.Count + 1);
                if (pick == candidates.Count) continue;
                var chosen = candidates[pick];
                outfit.Add(new CosmeticChoice(chosen.Id, chosen.tintable ? Base[random.Next(Base.Length)] : chosen.defaultColor));
            }
            return outfit;
        }

        /// <summary>Wears <paramref name="cosmetic"/> instead of the loaded cosmetic of its slot.</summary>
        public static void Wear(UserSettings settings, ContentRegistry content, CosmeticDefinition cosmetic)
        {
            TakeOff(settings, content, cosmetic.slot);
            settings.wornCosmetics.Insert(0, cosmetic.Id);
        }

        /// <summary>Takes off the loaded cosmetics of <paramref name="slot"/>.</summary>
        public static void TakeOff(UserSettings settings, ContentRegistry content, CosmeticSlot slot) =>
            settings.wornCosmetics.RemoveAll(id =>
            {
                var worn = content.Get<CosmeticDefinition>(id);
                return worn != null && worn.slot == slot;
            });

        /// <summary>The colour the player picked for <paramref name="cosmetic"/>, else its default colour.</summary>
        public static Color32 ColorOf(UserSettings settings, CosmeticDefinition cosmetic)
        {
            foreach (var picked in settings.cosmeticColors)
                if (string.Equals(picked.id, cosmetic.Id, StringComparison.OrdinalIgnoreCase)) return picked.color;
            return cosmetic.defaultColor;
        }

        /// <summary>Remembers <paramref name="color"/> as the colour of <paramref name="cosmetic"/>.</summary>
        public static void SetColor(UserSettings settings, CosmeticDefinition cosmetic, Color32 color)
        {
            settings.cosmeticColors.RemoveAll(c => string.Equals(c.id, cosmetic.Id, StringComparison.OrdinalIgnoreCase));
            settings.cosmeticColors.Add(new CosmeticChoice(cosmetic.Id, color));
        }
    }
}
