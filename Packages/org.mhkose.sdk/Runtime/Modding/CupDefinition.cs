using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A championship: an ordered list of tracks.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Cup", fileName = "Cup")]
    public class CupDefinition : ContentDefinition
    {
        public List<TrackDefinition> tracks = new();
        [Tooltip("Laps per race in this cup (0 = use track default).")]
        public int lapsOverride;
        [Tooltip("Sort key in menus.")]
        public int order;
    }
}
