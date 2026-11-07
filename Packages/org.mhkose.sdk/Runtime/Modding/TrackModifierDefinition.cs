using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// One way a track can vary (direction, time of day, weather): the axis that <see cref="TrackOptionDefinition"/>s choose
    /// on. Which tracks offer which options comes from <see cref="TrackOverlayDefinition"/>s, so a mod adds a new modifier
    /// with this definition, its options and overlays for the tracks it covers.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Track Modifier", fileName = "TrackModifier")]
    public class TrackModifierDefinition : ContentDefinition
    {
        [Tooltip("Sort key: menus list the modifiers in this order, variant keys name their options in it, and the overlays " +
                 "of a variant apply in it (a weather overlay after a time-of-day one).")]
        public int order;
    }
}
