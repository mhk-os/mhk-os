using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;

namespace MHK.Achievements
{
    /// <summary>How an achievement measures its goal.</summary>
    public enum AchievementGoalKind
    {
        [Tooltip("The sum of the counter stats reaches the target (races won, metres driven).")]
        Counter,
        [Tooltip("The largest value of the maxima stats reaches the target (most karts hit by one use).")]
        Maximum,
        [Tooltip("The set stat holds the items (tracks, arenas or championships of the required packs, plus those of parts).")]
        Collection,
    }

    /// <summary>The content a <see cref="AchievementGoalKind.Collection"/> achievement collects.</summary>
    public enum AchievementCollect
    {
        [Tooltip("Circuit tracks.")]
        RaceTrack,
        [Tooltip("Battle arenas.")]
        BattleArena,
        [Tooltip("Championships.")]
        Cup,
    }

    /// <summary>
    /// An achievement: a goal over the player's stats (<see cref="AchievementStats"/>). The title is the display name.
    /// It is shown and can be unlocked only while every pack it needs is loaded: <see cref="requiredPacks"/> and the
    /// packs of its <see cref="AchievementPartDefinition"/>s. An unlocked achievement is never taken away.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Achievement", fileName = "Achievement")]
    public class AchievementDefinition : ContentDefinition
    {
        [TextArea] public string description;
        [Tooltip("Material icon shown when there is no icon texture (the UI icon font's i-<name> class, e.g. 'emoji_events').")]
        public string iconName = "emoji_events";
        [Tooltip("Title and description stay hidden until it is unlocked.")]
        public bool secret;
        [Tooltip("Shown instead of the title while a secret achievement is locked.")]
        public string secretTitle = "Secret achievement";
        [Tooltip("Shown instead of the description while a secret achievement is locked.")]
        public string secretDescription = "Keep playing to find out.";
        [Tooltip("Sort key in the achievements list (then the title).")]
        public int order;

        [Header("Goal")]
        public AchievementGoalKind kind;
        [Tooltip("Counter: the sum of these counter stats. Maximum: the largest of these maxima stats.")]
        public List<string> stats = new();
        [Tooltip("Value to reach. Collection: number of items to collect, 0 = all of them.")]
        public long target = 1;
        [Tooltip("Collection: the set stat holding the collected item ids (e.g. 'tracks.finished', 'cups.perfect').")]
        public string stat;
        [Tooltip("Collection: the items are the content of this kind owned by the required packs, plus the items of parts.")]
        public AchievementCollect collect;
        [Tooltip("Pack ids that must all be loaded and enabled to show it. The packs of its parts are required as well.")]
        public List<string> requiredPacks = new();

        [Header("Progress text")]
        [Tooltip("Unit after the progress numbers (e.g. 'km', 'tracks'), empty = none.")]
        public string unit;
        [Tooltip("Progress numbers are shown multiplied by this (0.001 shows metres as km). Values other than 1 show one decimal.")]
        public float displayScale = 1f;
    }
}
