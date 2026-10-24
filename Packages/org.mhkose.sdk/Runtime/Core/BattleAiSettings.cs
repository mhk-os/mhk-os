using System;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>How computer drivers of one engine class fight in Battle (<see cref="GameConfig.battleAi"/>).</summary>
    [Serializable]
    public class BattleAiSettings
    {
        [Tooltip("Seconds between goal decisions (item box, opponent to attack, escape, wander), ±20 %.")]
        [Min(0.05f)] public float decisionInterval = 0.5f;
        [Tooltip("Eagerness to hunt opponents (0 to 1): chance per decision to ram a nearby kart without an item, and how " +
                 "much a wounded or close opponent is preferred as a target.")]
        [Range(0, 1)] public float aggression = 0.6f;
        [Tooltip("Below this share of the maximum health a driver avoids fights: it escapes from opponents near it and " +
                 "heads for health pickups.")]
        [Range(0, 1)] public float fleeHealth = 0.25f;
        [Tooltip("Chance per decision to notice an opponent that is lining up an attack on this kart (and get away).")]
        [Range(0, 1)] public float threatAwareness = 0.6f;
        [Tooltip("Share of a fleeing or crossing opponent's motion a hunter leads its chase by (0 = drives at where it is).")]
        [Range(0, 1)] public float aimLead = 0.6f;
    }
}
