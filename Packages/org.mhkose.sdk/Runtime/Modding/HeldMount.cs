using UnityEngine;

namespace MHK.Modding
{
    /// <summary>Where a held item is attached to the kart (centre of a face of the kart's chassis box, or its origin).</summary>
    public enum HeldMount
    {
        [Tooltip("Above the kart (top of the chassis box).")] Top,
        [Tooltip("In front of the kart (front of the chassis box).")] Front,
        [Tooltip("Behind the kart (back of the chassis box).")] Back,
        [Tooltip("At the kart's origin, the prefab's own offset places it.")] Origin,
    }
}
