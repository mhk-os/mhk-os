using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MHK.Achievements
{
    /// <summary>
    /// An achievement as menus show it (<see cref="Achievements.Visible"/>): texts (a locked secret one shows its
    /// secret texts), icon, unlock and progress. Updated in place; <see cref="Achievements.Changed"/> tells when.
    /// </summary>
    public sealed class AchievementView
    {
        /// <summary>Icon of achievements that name none.</summary>
        public const string DefaultIconName = "emoji_events";

        internal AchievementView(AchievementDefinition definition, HashSet<string> items)
        {
            Definition = definition;
            Items = items;
        }

        internal AchievementDefinition Definition { get; }
        /// <summary>Collection achievements: the item ids to collect, null for the other kinds.</summary>
        internal HashSet<string> Items { get; }

        public string Id => Definition.Id;
        public string Title => Hidden ? Definition.secretTitle : Definition.DisplayName;
        public string Description => Hidden ? Definition.secretDescription : Definition.description;
        /// <summary>The icon picture, null = use <see cref="IconName"/>.</summary>
        public Texture2D Icon => Definition.Icon;
        /// <summary>Material icon name (the UI icon font's i-&lt;name&gt; class).</summary>
        public string IconName => string.IsNullOrEmpty(Definition.iconName) ? DefaultIconName : Definition.iconName;
        public bool Secret => Definition.secret;
        public bool Unlocked { get; private set; }
        /// <summary>Local time of the unlock, default while locked.</summary>
        public DateTime UnlockedAt { get; private set; }
        /// <summary>Progress towards <see cref="Target"/>, at most the target (the target once unlocked).</summary>
        public long Current { get; private set; }
        public long Target { get; private set; }
        /// <summary><see cref="Current"/> / <see cref="Target"/>, 0..1.</summary>
        public float Progress { get; private set; }
        /// <summary>"23 / 50", "12.4 / 100 km", "31 / 58 tracks".</summary>
        public string ProgressText { get; private set; } = "";
        /// <summary>Share of the online players who unlocked it (0..1), null while unknown (<see cref="Achievements.RefreshShares"/>).</summary>
        public float? PlayerShare { get; private set; }

        bool Hidden => Secret && !Unlocked;

        internal void Set(long current, long target, long unlockedAtMs, float? share)
        {
            Unlocked = unlockedAtMs >= 0;
            UnlockedAt = Unlocked ? DateTimeOffset.FromUnixTimeMilliseconds(unlockedAtMs).LocalDateTime : default;
            Target = target;
            Current = Unlocked ? target : Math.Min(current, target);
            Progress = Unlocked ? 1f : target > 0 ? Mathf.Clamp01(Current / (float)target) : 0f;
            var unit = string.IsNullOrEmpty(Definition.unit) ? "" : " " + Definition.unit;
            ProgressText = $"{Format(Current)} / {Format(Target)}{unit}";
            PlayerShare = share;
        }

        internal void SetShare(float? share) => PlayerShare = share;

        // Scaled values show one decimal, rounded down so the goal never shows as reached early.
        string Format(long value)
        {
            var scale = Definition.displayScale;
            if (scale == 1f || scale <= 0f) return value.ToString(CultureInfo.InvariantCulture);
            return (Math.Floor(value * (double)scale * 10.0) / 10.0).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
