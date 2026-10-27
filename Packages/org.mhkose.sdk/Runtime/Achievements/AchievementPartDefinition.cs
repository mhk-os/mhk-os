using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;

namespace MHK.Achievements
{
    /// <summary>
    /// A pack's share of another pack's achievement (e.g. its tracks for "race on every track"): the owner pack becomes
    /// a required pack of that achievement, so it shows only while every contributing pack is loaded. Once seen, the
    /// requirement is remembered (achievements.json): disabling or removing the pack hides the achievement, it never
    /// shows without the pack's items.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Achievement Part", fileName = "AchievementPart")]
    public class AchievementPartDefinition : ContentDefinition
    {
        [Tooltip("Id of the achievement this part adds to.")]
        public string achievement;
        [Tooltip("Collection achievements: adds every item of the achievement's collect kind owned by this part's pack.")]
        public bool collectOwnPack = true;
        [Tooltip("Collection achievements: further item ids to collect (track or championship ids).")]
        public List<string> items = new();
    }
}
