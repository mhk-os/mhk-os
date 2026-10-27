using System;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>One engine class (50cc, 100cc, 150cc): kart speed and the computer drivers' skill.</summary>
    [Serializable]
    public class DifficultySettings
    {
        public string label = "Medium";
        [Tooltip("Engine class factor applied to every kart: diff ÷ f, torque × f (MHK2 50/100/150cc = 0.9/1.1/1.3, the " +
                 "remake's classes run one step up: 1.1/1.3/1.5).")]
        public float engineClassFactor = 1.1f;
        [Tooltip("How precisely AI follows the racing line (0 = sloppy, 1 = perfect).")]
        [Range(0, 1)] public float aiPrecision = 0.8f;
        [Tooltip("Seconds between AI powerup usage decisions.")]
        public float aiItemReactionTime = 1.5f;

        [Header("Engine class top speed")]
        [Tooltip("Top speed (km/h, as shown on the HUD) of the reference kart (GameConfig.defaultPhysics) in this engine " +
                 "class at full throttle on flat road. Sets the class's kart speed scale (GameConfig.SpeedScale): every " +
                 "kart's top speed and accelerations scale with it, so other karts keep their relative differences.")]
        public float topSpeedKmh = 90f;

        [Header("AI behaviour (engine class)")]
        [Tooltip("Chance to steer around a planted trap (mine, black hole, x-field, puddle, ...) seen ahead, rolled per trap. " +
                 "Keep it below 1: traps must still catch the best computer drivers now and then.")]
        [Range(0, 1)] public float aiAvoidTrapChance;
        [Tooltip("Chance to recognise a fake item box and avoid it, otherwise the AI drives into it like into a real box.")]
        [Range(0, 1)] public float aiAvoidFakeBoxChance;
        [Tooltip("Chance to keep a dropped trap until a narrow track segment (where others cannot avoid it), rolled per item.")]
        [Range(0, 1)] public float aiNarrowPlantChance;
        [Tooltip("Ranged items are aimed: fired at a kart in line of sight and lined up for straight shots (otherwise random use).")]
        public bool aiAimedShots;
        [Tooltip("Uses a protecting item (shield, blue light) when a homing missile comes for this kart.")]
        public bool aiDefendMissile;
        [Tooltip("Leader targeted by a homing missile with no defence and a kart close behind: chance to brake and let it pass.")]
        [Range(0, 1)] public float aiMissileYieldChance;
        [Tooltip("Chance to give a wide berth to an opponent holding a melee item (wooden hammer), rolled per holder and " +
                 "lap: a low-priority lane nudge to about the item's reach beside it. Never overrides kart, trap or wall " +
                 "avoidance and never brakes or leaves the road.")]
        [Range(0, 1)] public float aiAvoidHammerChance;
        [Tooltip("Chance that a computer driver hunts karts it can flatten (shrunk karts, CrushOnKartContact), rolled once " +
                 "per driver and race: it steers into such a kart close ahead on the road (a few tens of metres, within " +
                 "reach of its lane) instead of passing it. Never chases karts far away or behind and never leaves the road.")]
        [Range(0, 1)] public float aiFlattenSeekChance;
        [Tooltip("Chance to steer out of the way of an opponent ramming with a hostile aura (TINcan ram, Thunder panzer) " +
                 "that would reach this kart within about two seconds, rolled per ram: an urgent lane change in races, a " +
                 "heading away from its course in battle manoeuvres.")]
        [Range(0, 1)] public float aiAvoidRamChance = 0.8f;
    }
}
