using System;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>How computer drivers of one engine class fight in Battle (<see cref="GameConfig.battleAi"/>).</summary>
    [Serializable]
    public class BattleAiSettings
    {
        [Tooltip("Seconds between decisions (start or stop hunting an opponent, pick an item box or a spot to roam to " +
                 "in arenas), ±20 %.")]
        [Min(0.05f)] public float decisionInterval = 0.5f;
        [Tooltip("A driver holding an attacking item hunts the best opponent it can see within this distance (m), else it " +
                 "drives the track like a race.")]
        [Min(1f)] public float huntRange = 45f;
        [Tooltip("A hunt ends when the target has been farther than huntRange times this, or out of sight, for " +
                 "huntLoseSeconds.")]
        [Min(1f)] public float huntLeaveShare = 1.5f;
        [Tooltip("Seconds a hunted opponent may stay too far or out of sight before the driver gives up and races on.")]
        [Min(0f)] public float huntLoseSeconds = 3f;
        [Tooltip("Without an item, an opponent this close (m) right in front is rammed (ramming damages in battles), " +
                 "with the chance aggression per decision. 0 = never.")]
        [Min(0f)] public float ramRange = 12f;
        [Tooltip("Eagerness to fight (0 to 1): chance per decision to ram an opponent within ramRange, and how much a " +
                 "wounded opponent is preferred as a target.")]
        [Range(0, 1)] public float aggression = 0.6f;
        [Tooltip("Share of a fleeing or crossing opponent's motion a hunter leads its chase by (0 = drives at where it is).")]
        [Range(0, 1)] public float aimLead = 0.6f;
        [Tooltip("Each driver turns around once per battle at a random time between this and reverseMaxSeconds after GO " +
                 "(race clock), then keeps that direction (on circuits: drives the track backwards).")]
        [Min(0f)] public float reverseMinSeconds = 20f;
        [Tooltip("Latest time (s after GO) of the one turn around (see reverseMinSeconds).")]
        [Min(0f)] public float reverseMaxSeconds = 60f;
    }
}
