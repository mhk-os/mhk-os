using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>Combined modifiers of a kart's active status effects, as <see cref="KartEffects"/> last aggregated them.</summary>
    public struct StatusModifiers
    {
        /// <summary>Speed and acceleration multipliers always apply during the first 2 % of an effect.</summary>
        const float ModifierGraceProgress = 0.02f;
        /// <summary>Screen overlays fade out over this last share of their effect.</summary>
        const float OverlayFadeShare = 0.4f;

        public float acceleration, maxSpeed, steering, grip, scale, gravity, opacity, verticalScale, damageDealt, damageTaken;
        public bool controlsLocked, steeringInverted, shielded, burned, frozen, forceThrottle, throttleBlocked, itemUseBlocked;
        public bool ghost, runOver, hidden, wheelsHidden;
        public Color screenOverlay;
        /// <summary>
        /// An effect speeds the kart up (max speed or acceleration multiplier above 1, or a forward impulse), whatever its
        /// speed gate. Arms <see cref="KartPhysicsProfile.boostOverspeedDecay"/>.
        /// </summary>
        public bool speedBoost;

        /// <summary>No effect active: every multiplier 1, every flag off, no overlay.</summary>
        public static StatusModifiers Identity => new()
        {
            acceleration = 1f, maxSpeed = 1f, steering = 1f, grip = 1f, scale = 1f, gravity = 1f, opacity = 1f,
            verticalScale = 1f, damageDealt = 1f, damageTaken = 1f, screenOverlay = new Color(0, 0, 0, 0),
        };

        /// <summary>
        /// Adds one active effect. <paramref name="forwardSpeedOriginal"/> (original m/s) gates the speed multipliers by
        /// <see cref="StatusEffectDefinition.modifiersMinSpeed"/>.
        /// </summary>
        public void Accumulate(KartEffects.ActiveEffect e, float forwardSpeedOriginal)
        {
            var d = e.Definition;
            if (forwardSpeedOriginal >= d.modifiersMinSpeed || e.Progress < ModifierGraceProgress)
            {
                acceleration *= d.accelerationMultiplier;
                maxSpeed *= d.maxSpeedMultiplier;
            }
            speedBoost |= d.maxSpeedMultiplier > 1f || d.accelerationMultiplier > 1f || d.forwardImpulse > 0f;
            steering *= d.steeringMultiplier;
            grip *= d.gripMultiplier;
            scale *= d.scaleMultiplier;
            gravity *= d.gravityMultiplier;
            opacity = Mathf.Min(opacity, d.opacity);
            ghost |= d.ghost || d.hideKart;
            hidden |= d.hideKart && e.Elapsed >= d.hideDelay;
            runOver |= d.runOver;
            controlsLocked |= d.lockControls || d.freezeKart;
            steeringInverted |= d.invertSteering;
            shielded |= d.blocksHostileEffects;
            burned |= d.burnVisual;
            wheelsHidden |= d.hideWheels;
            frozen |= d.freezeKart;
            forceThrottle |= d.forceThrottle;
            throttleBlocked |= d.blockThrottle;
            itemUseBlocked |= d.blocksItemUse;
            verticalScale *= d.verticalScale;
            damageDealt *= d.damageDealtMultiplier;
            damageTaken *= d.damageTakenMultiplier;
            AccumulateOverlay(d, e);
        }

        void AccumulateOverlay(StatusEffectDefinition d, KartEffects.ActiveEffect e)
        {
            var fade = Mathf.Clamp01(e.Duration > 0f ? e.TimeLeft / (e.Duration * OverlayFadeShare) : 1f);
            var o = new Color(d.screenOverlay.r, d.screenOverlay.g, d.screenOverlay.b, d.screenOverlay.a * fade);
            if (o.a > screenOverlay.a) screenOverlay = o;
        }
    }
}
