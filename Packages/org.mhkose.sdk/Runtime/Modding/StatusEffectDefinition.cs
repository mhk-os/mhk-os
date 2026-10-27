using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Data-driven effect on a kart: buffs (turbo, blue light, shield), hits (shot down, fell in a hole), field effects
    /// (x-field slide, freeze, shrink, flash). Hits usually start from a shared reaction (core.reaction.*) and override
    /// only what their game does differently. Powerups apply these. They never contain code.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Status Effect", fileName = "StatusEffect")]
    public class StatusEffectDefinition : ContentDefinition
    {
        [Header("Reaction")]
        [Tooltip("Shared reaction this effect is based on (core.reaction.blast, flatten, spinout, stun, freeze, slide, " +
                 "slip, fall, vanish). In the editor every value not listed in overrides is copied from it, so an effect " +
                 "picks a reaction and changes only what its game does differently. Empty = a standalone effect.")]
        public StatusEffectDefinition reaction;
        [Tooltip("Fields this effect sets itself instead of taking them from reaction. The inspector keeps this list: " +
                 "editing a field overrides it, right-click a field to revert it to the reaction's value.")]
        public List<string> overrides = new();

        [Header("Timing")]
        public float duration = 2f;
        [Tooltip("Use the length of the victim's character animation (e.g. MHK2 'shotdown') as duration when available.")]
        public bool durationFromAnimation;
        [Tooltip("Re-applying refreshes the timer instead of stacking a second instance.")]
        public bool refreshOnReapply = true;
        [Tooltip("Exclusive buff: applying it ends any other exclusive buff (MHK2: one active effect at a time).")]
        public bool exclusiveBuff;

        [Header("Driving modifiers (multiplicative)")]
        [Tooltip("Drive torque multiplier (MHK2 'accel factor').")]
        public float accelerationMultiplier = 1f;
        [Tooltip("Engine rpm is divided by this before the torque lookup (MHK2 'max. speed factor').")]
        public float maxSpeedMultiplier = 1f;
        [Tooltip("Modifiers only apply while forward speed is above this (original MHK2 m/s, compared ÷ KartPhysics.SpeedScale, turbo can: 15).")]
        public float modifiersMinSpeed;
        public float steeringMultiplier = 1f;
        public float gripMultiplier = 1f;
        [Tooltip("Velocity change applied once along the kart's forward axis (original m/s, × KartPhysics.SpeedScale).")]
        public float forwardImpulse;
        [Tooltip("Multiply current velocity once on apply (0 = full stop, 1 = unchanged).")]
        public float velocityRetain = 1f;
        [Tooltip("Continuous velocity damping while active (1/s), e.g. x-field slide.")]
        public float velocityDamping;
        [Tooltip("On apply the kart's forward speed jumps straight to its current top speed (KartPhysics.MaxSpeed, " +
                 "including this effect's maxSpeedMultiplier) unless it is already faster. Sideways and vertical " +
                 "velocity are kept (TINcan turbo oil, leapfrog landing).")]
        public bool snapToTopSpeed;

        [Header("Control")]
        public bool lockControls;
        [Tooltip("Throttle is held fully open (MHK2 power start).")]
        public bool forceThrottle;
        [Tooltip("Throttle input is ignored, steering still works (MHK3 flash).")]
        public bool blockThrottle;
        [Tooltip("The kart cannot use items while affected (MHK3 shrinker).")]
        public bool blocksItemUse;
        public bool invertSteering;
        [Tooltip("Kart is held in place (velocities zeroed every tick) while active.")]
        public bool freezeKart;
        [Tooltip("Above 0: the kart model spins out when this effect starts or is re-applied, the same spin for every " +
                 "effect (GameConfig.spinTurns over GameConfig.spinSeconds). The number itself no longer matters. Visual only.")]
        public float spinTurns;
        /// <summary>The effect spins the kart model out (<see cref="spinTurns"/> above 0, the shared GameConfig spin).</summary>
        public bool Spins => spinTurns > 0f;
        [Tooltip("Launch upwards (m/s) e.g. for explosions.")]
        public float upwardImpulse;
        [Tooltip("Respawn the kart on the track when the effect ends (MHK2 hits).")]
        public bool respawnOnEnd;
        [Tooltip("Respawn this far behind the last safe point (m).")]
        public float respawnBackDistance = 15f;
        [Tooltip("Gravity on the kart is multiplied by this while active (wings: 0.5 = floats longer through jumps).")]
        public float gravityMultiplier = 1f;
        [Tooltip("Applied when the kart lands after at least 0.3 s in the air while this effect is active (spring: the " +
                 "landing boost).")]
        public StatusEffectDefinition landingEffect;
        [Tooltip("This effect ends as soon as the kart lands (after at least 0.3 s in the air).")]
        public bool endOnLanding;

        [Header("Protection")]
        [Tooltip("Shield: incoming hostile effects are blocked while active.")]
        public bool blocksHostileEffects;
        [Tooltip("This effect is hostile (can be blocked by shields).")]
        public bool hostile = true;
        [Tooltip("Removes the victim's held powerup.")]
        public bool dropsHeldItem;
        [Tooltip("Ends the victim's active buffs (shield, turbo...).")]
        public bool cancelsBuffs;
        [Tooltip("Buff that hits do not cancel. Only expiry or a respawn ends it (Thunder quad damage, hammer).")]
        public bool survivesHits;
        [Tooltip("Ghost: the kart is intangible. It passes through other karts, movable props, track hazards and dropped " +
                 "items, and powerups and hazards neither hit nor target it. Track geometry still carries it.")]
        public bool ghost;
        [Tooltip("Can be run over: other karts drive through this kart as if it were not there (no kart-to-kart collision, " +
                 "their wheels do not rest on it), e.g. flattened karts. Track, items and hazards still treat it normally.")]
        public bool runOver;
        [Tooltip("Vanish: the kart is intangible while active (as ghost) and is not drawn from hideDelay on, e.g. blown to " +
                 "dust before a respawn. Combine with freezeKart and respawnOnEnd (core.reaction.vanish).")]
        public bool hideKart;
        [Tooltip("Seconds after the effect starts before hideKart hides the kart (MHK Extra rocket: 0.5).")]
        public float hideDelay;

        [Header("Health (used in battles)")]
        [Tooltip("Damage when another kart inflicts this hostile effect, -1 = GameConfig.defaultHitDamage (Thunder: 10).")]
        public int damage = -1;
        [Tooltip("Health restored when applied (Thunder health pickup: +10).")]
        public int heal;
        [Tooltip("Damage this kart deals is multiplied while active (Thunder quad damage 4, hammer 2).")]
        public float damageDealtMultiplier = 1f;
        [Tooltip("Damage this kart takes is multiplied while active (Thunder hammer 0.5).")]
        public float damageTakenMultiplier = 1f;

        [Header("Size")]
        public float scaleMultiplier = 1f;
        [Tooltip("Extra vertical squash of the visual (flattened by a hammer / weight).")]
        public float verticalScale = 1f;

        [Header("Aura")]
        [Tooltip("Applied every tick to other karts within auraRadius (Thunder Kröt panzer ram).")]
        public StatusEffectDefinition auraEffect;
        public float auraRadius = 2f;

        [Header("Presentation")]
        public GameObject attachedVfxPrefab;
        [Tooltip("Attach attachedVfxPrefab to the kart's visual (follows its tilt, spins and squash, e.g. wings on the " +
                 "driver) instead of the kart root.")]
        public bool attachToVisual;
        [Tooltip("Where attachedVfxPrefab sits on the kart: Origin = the prefab's own offset from the kart's origin. " +
                 "Front / Top / Back = that edge of the kart's chassis box (plus the prefab's offset), so one visual fits " +
                 "karts of every size (e.g. a ram in front of the kart).")]
        public HeldMount attachMount = HeldMount.Origin;
        [Tooltip("Kart and driver are drawn with this opacity while active (ghost: 0.5), 1 = opaque.")]
        [Range(0f, 1f)] public float opacity = 1f;
        public bool useCharacterAnimation = true;
        [Tooltip("Driver / kart animation role played when the effect starts (held while it freezes or locks the kart). " +
                 "Reaction roles such as Dizzy, Shocked, Burning fall back to Shake / ShotDown for characters without them.")]
        public CharacterAnimRole characterAnimation = CharacterAnimRole.ShotDown;
        [Tooltip("One-shot at the kart when the effect starts (MHK2 PPS_activate), not when a running effect is refreshed " +
                 "(fields re-apply every frame). Active loops: an EffectLoopSound on attachedVfxPrefab.")]
        public AudioClip applySound;
        [Tooltip("One-shot at the kart expiringSoundLead seconds before the effect runs out on its own (MHK2 shield PPS_end, " +
                 "3 s before the end).")]
        public AudioClip expiringSound;
        [Tooltip("Seconds before the natural end at which expiringSound plays.")]
        public float expiringSoundLead = 3f;
        [Tooltip("One-shot at the kart when the effect runs out on its own, not when it is cancelled (MHK2 PPS_dissolve).")]
        public AudioClip endSound;
        [Tooltip("Tints the victim's screen (Flash effect), alpha 0 = none.")]
        public Color screenOverlay = new(1, 1, 1, 0);
        [Tooltip("Makes the kart model burn/blacken (MHK2 shot-down look).")]
        public bool burnVisual;
        [Tooltip("Tints kart and driver while active, multiplied onto their colours (MHK2 slime ball: green). Alpha is " +
                 "the strength, 0 = none. Fades in and out like the burnt look, and burnVisual wins over it.")]
        public Color modelTint = new(1, 1, 1, 0);
        [Tooltip("The kart model's wheels are not drawn while active (MHK2 x-field: the tyres are gone and the rims " +
                 "scrape along). Driving physics are unchanged.")]
        public bool hideWheels;
        [Tooltip("The kart body bobs while active, visual only (MHK Extra spike strip: flat tyres). Height 0 = none.")]
        public BodyBob bodyBob = new();
        [Tooltip("Spawned at the kart when the effect runs out on its own (Thunder snowball: snow bursts off the kart).")]
        public GameObject endVfxPrefab;
        [Tooltip("Speed rush: the chase camera widens its horizontal field of view by up to this many degrees while " +
                 "the effect is active, scaled by the kart's speed as a fraction of its normal top speed (boosts).")]
        public float cameraFovBoost;
    }
}
