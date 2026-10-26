using System;
using System.Collections.Generic;
using MHK.Modding;

namespace MHK.Validation
{
    /// <summary>What the active effects of a sample allow the kart (<see cref="EffectSet.Describe"/>).</summary>
    public struct EffectInfluence
    {
        /// <summary>Any effect is active.</summary>
        public bool any;
        /// <summary>An effect that cannot be resolved (content not installed): nothing about it is known.</summary>
        public bool unknown;
        /// <summary>An effect speeds the kart up (multipliers above 1, a forward impulse).</summary>
        public bool speedBoost;
        /// <summary>An effect moves the kart by itself (impulses, freezing, velocity changes, respawn).</summary>
        public bool movesKart;
        /// <summary>Products of the active effects' multipliers, never below 1 for speed and acceleration.</summary>
        public float maxSpeed, acceleration;
        /// <summary>Product of the active effects' gravity multipliers.</summary>
        public float gravity;

        public static EffectInfluence None => new() { maxSpeed = 1f, acceleration = 1f, gravity = 1f };
    }

    /// <summary>
    /// Status effect ids as bits of <see cref="KartSample.effects"/>: up to 63 distinct ids, all further ones share the
    /// last bit (treated as unknown). One set serves every validator that resolves ids the same way.
    /// </summary>
    public sealed class EffectSet
    {
        const int OverflowBit = 63;

        readonly Func<string, StatusEffectDefinition> resolve;
        readonly Dictionary<string, int> indices = new();
        readonly List<StatusEffectDefinition> definitions = new();

        /// <param name="resolve">Definition of an effect id, null when it is not installed.</param>
        public EffectSet(Func<string, StatusEffectDefinition> resolve) => this.resolve = resolve;

        /// <summary>The bit of <paramref name="effectId"/>, registered on first use; 0 for an empty id.</summary>
        public ulong Bit(string effectId)
        {
            if (string.IsNullOrEmpty(effectId)) return 0;
            if (!indices.TryGetValue(effectId, out var index))
            {
                index = definitions.Count < OverflowBit ? definitions.Count : OverflowBit;
                if (index < OverflowBit) definitions.Add(resolve(effectId));
                indices[effectId] = index;
            }
            return 1UL << index;
        }

        /// <summary>Combined influence of the effects in <paramref name="mask"/>.</summary>
        public EffectInfluence Describe(ulong mask)
        {
            var fx = EffectInfluence.None;
            if (mask == 0) return fx;
            fx.any = true;
            for (var i = 0; i <= OverflowBit && mask >> i != 0; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                var d = i < definitions.Count ? definitions[i] : null;
                if (d == null)
                {
                    fx.unknown = true;
                    fx.movesKart = true;
                    continue;
                }
                fx.maxSpeed *= Math.Max(1f, d.maxSpeedMultiplier);
                fx.acceleration *= Math.Max(1f, d.accelerationMultiplier);
                fx.gravity *= d.gravityMultiplier;
                fx.speedBoost |= d.maxSpeedMultiplier > 1f || d.accelerationMultiplier > 1f || d.forwardImpulse > 0f;
                fx.movesKart |= d.forwardImpulse != 0f || d.upwardImpulse != 0f || d.velocityRetain != 1f || d.velocityDamping != 0f
                                || d.snapToTopSpeed || d.freezeKart || d.respawnOnEnd || d.spinTurns != 0f || d.ghost || d.hideKart;
            }
            return fx;
        }
    }
}
