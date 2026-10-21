using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Marks a non-kart object (e.g. a homing missile or glibber ball) to be drawn on the race minimap as a
    /// coloured dot at its world position while enabled. Karts are drawn by the minimap itself and must not carry one.
    /// </summary>
    public partial class MinimapBlip : MonoBehaviour
    {
        [Tooltip("Dot colour.")]
        public Color color = Color.white;
        [Tooltip("Dot size relative to a kart dot.")]
        public float size = 1f;
        [Tooltip("Drawn as a ring (a border in the colour around a faint fill) instead of a filled dot: time-trial ghosts.")]
        public bool ring;

        [AutoStaticsCleanup] static readonly List<MinimapBlip> all = new();
        /// <summary>All blips currently enabled in the scene.</summary>
        public static IReadOnlyList<MinimapBlip> All => all;

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);
    }
}
