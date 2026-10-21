using UnityEngine;

namespace MHK.Modding
{
    /// <summary>How computer drivers use a powerup.</summary>
    public enum PowerupAiUsage
    {
        [Tooltip("Fire at karts ahead.")] OffensiveForward,
        [Tooltip("Drop/fire behind when someone is close behind.")] Defensive,
        [Tooltip("Use on straights / when behind.")] Boost,
        [Tooltip("Use when a threat is near or immediately.")] Protect,
        [Tooltip("Use as soon as possible.")] Immediate,
    }
}
