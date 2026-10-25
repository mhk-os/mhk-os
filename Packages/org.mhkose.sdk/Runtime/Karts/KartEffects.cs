using System;
using System.Collections.Generic;
using MHK.Audio;
using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Data-driven status effects on a kart (<see cref="StatusEffectDefinition"/>): applies, times and ends them and
    /// aggregates their modifiers for physics and visuals (MHK2 rules: one exclusive buff, hits cancel buffs).
    /// </summary>
    [RequireComponent(typeof(Kart))]
    public class KartEffects : MonoBehaviour
    {
        /// <summary>One running effect instance.</summary>
        public sealed class ActiveEffect
        {
            public StatusEffectDefinition Definition { get; }
            /// <summary>The effect's attached visual (<see cref="StatusEffectDefinition.attachedVfxPrefab"/>), or null.</summary>
            public GameObject Vfx { get; }
            public float TimeLeft { get; internal set; }
            public float Duration { get; internal set; }
            public float Progress => Duration > 0 ? 1f - TimeLeft / Duration : 1f;
            /// <summary>Seconds since the effect started (or was last extended by a refresh).</summary>
            public float Elapsed => Duration - TimeLeft;

            internal ActiveEffect(StatusEffectDefinition definition, float duration, GameObject vfx)
            {
                Definition = definition;
                TimeLeft = duration;
                Duration = duration;
                Vfx = vfx;
            }
        }

        const float ParticleReleasePadding = 0.1f;

        readonly List<ActiveEffect> active = new();
        readonly List<ActiveEffect> expired = new();
        readonly List<StatusEffectDefinition> landingEffects = new();
        Kart kart;

        public IReadOnlyList<ActiveEffect> Active => active;

        /// <summary>The modifiers of all active effects combined (recomputed whenever effects change and every tick).</summary>
        public StatusModifiers Modifiers { get; private set; } = StatusModifiers.Identity;

        public float AccelerationMultiplier => Modifiers.acceleration;
        public float MaxSpeedMultiplier => Modifiers.maxSpeed;
        public float SteeringMultiplier => Modifiers.steering;
        public float GripMultiplier => Modifiers.grip;
        public float ScaleMultiplier => Modifiers.scale;
        public bool ControlsLocked => Modifiers.controlsLocked;
        public bool SteeringInverted => Modifiers.steeringInverted;
        public bool Shielded => Modifiers.shielded;
        public bool Frozen => Modifiers.frozen;
        public bool ForceThrottle => Modifiers.forceThrottle;
        public bool ThrottleBlocked => Modifiers.throttleBlocked;
        public bool ItemUseBlocked => Modifiers.itemUseBlocked;
        public float VerticalScale => Modifiers.verticalScale;
        public float DamageDealtMultiplier => Modifiers.damageDealt;
        public float DamageTakenMultiplier => Modifiers.damageTaken;
        public bool Burned => Modifiers.burned;
        /// <summary>Strongest <see cref="StatusEffectDefinition.modelTint"/> of the active effects, alpha 0 = none.</summary>
        public Color ModelTint => Modifiers.modelTint;
        /// <summary>An effect with <see cref="StatusEffectDefinition.hideWheels"/> is active: the model's wheels are not drawn.</summary>
        public bool WheelsHidden => Modifiers.wheelsHidden;
        /// <summary>Strongest body bob of the active effects (<see cref="StatusEffectDefinition.bodyBob"/>), null when none bobs.</summary>
        public BodyBob BodyBob
        {
            get
            {
                BodyBob strongest = null;
                foreach (var e in active) strongest = BodyBob.Stronger(strongest, e.Definition.bodyBob);
                return strongest;
            }
        }
        public Color ScreenOverlay => Modifiers.screenOverlay;
        /// <summary>Product of the active effects' gravity multipliers (wings: 0.5).</summary>
        public float GravityMultiplier => Modifiers.gravity;
        /// <summary>A ghost effect (<see cref="StatusEffectDefinition.ghost"/>) is active.</summary>
        public bool GhostActive => Modifiers.ghost;
        /// <summary>
        /// Intangible: a ghost effect is active, or one just ended while the kart is still inside something it passed
        /// through (<see cref="KartPhysics.Intangible"/>). Powerups and hazards neither hit nor target such karts.
        /// </summary>
        public bool Ghost => GhostActive || (kart != null && kart.Physics != null && kart.Physics.Intangible);
        /// <summary>A run-over effect (<see cref="StatusEffectDefinition.runOver"/>, flattened) is active: other karts drive through it.</summary>
        public bool RunOverActive => Modifiers.runOver;
        /// <summary>Lowest <see cref="StatusEffectDefinition.opacity"/> of the active effects (1 = opaque).</summary>
        public float Opacity => Modifiers.opacity;
        /// <summary>A vanish effect (<see cref="StatusEffectDefinition.hideKart"/>) has hidden the kart: not drawn, no item pickups.</summary>
        public bool Hidden => Modifiers.hidden;
        /// <summary>Visual yaw offset (degrees) of spinning effects.</summary>
        public float SpinAngle { get; private set; }
        /// <summary>Hostile effects are ignored until this time (respawn protection).</summary>
        public float ProtectedUntil { get; set; }

        /// <summary>Every successful <see cref="Apply"/>, including refreshes of a running effect (ghost recording replays these).</summary>
        public event Action<StatusEffectDefinition, Kart> Applied;
        /// <summary>
        /// An effect began on this kart (a new instance, not a refresh). Once-per-hit reactions (driver animation, hit
        /// voice, <see cref="StatusEffectDefinition.applySound"/>) use this.
        /// </summary>
        public event Action<StatusEffectDefinition, Kart> Started;
        public event Action<StatusEffectDefinition> Ended;

        void Awake() => kart = GetComponent<Kart>();

        /// <summary>Hostile effects are blocked: shield, ghost or respawn protection.</summary>
        public bool IsProtected => Shielded || Ghost || Time.time < ProtectedUntil;

        /// <summary>Inside the protection after a respawn (<see cref="ProtectedUntil"/>), shield and ghost aside.</summary>
        public bool RespawnProtected => Time.time < ProtectedUntil;

        /// <summary>
        /// Applies an effect. Returns false if a shield / protection blocked it, or if it flattens
        /// (<see cref="StatusEffectDefinition.verticalScale"/> &lt; 1) a kart that is already flattened or waiting to
        /// respawn (<see cref="HasRespawnPending"/>): nothing is applied, refreshed or extended then.
        /// </summary>
        public bool Apply(StatusEffectDefinition def, Kart source = null, float durationOverride = -1f)
        {
            if (IsBlocked(def)) return false;
            var duration = ResolveDuration(def, durationOverride);
            EndConflictingBuffs(def);
            var existing = def.refreshOnReapply ? Find(def) : null;
            if (existing != null)
            {
                existing.TimeLeft = Mathf.Max(existing.TimeLeft, duration);
                existing.Duration = Mathf.Max(existing.Duration, duration);
            }
            else active.Add(new ActiveEffect(def, duration, SpawnAttachedVfx(def)));

            ApplyImpulses(def);
            ApplyHealthAndInventory(def, source);
            if (existing == null && def.applySound != null) AudioManager.PlaySfx(def.applySound, transform.position);

            Recalculate();
            Applied?.Invoke(def, source);
            if (existing == null) Started?.Invoke(def, source);
            return true;
        }

        bool IsBlocked(StatusEffectDefinition def)
        {
            if (def == null || (kart != null && kart.Eliminated)) return true;
            if (def.hostile && IsProtected) return true;
            return def.verticalScale < 1f && (VerticalScale < 1f || HasRespawnPending);
        }

        float ResolveDuration(StatusEffectDefinition def, float durationOverride)
        {
            var duration = durationOverride > 0 ? durationOverride : def.duration;
            if (def.durationFromAnimation && kart != null && def.useCharacterAnimation
                && kart.AnimationDuration(def.characterAnimation) is var animLength && animLength > 0f)
                duration = animLength;
            return duration;
        }

        void EndConflictingBuffs(StatusEffectDefinition def)
        {
            // Plain loops, no lambdas capturing 'def': fields re-apply their effect every frame.
            if (def.exclusiveBuff)
            {
                for (var i = active.Count - 1; i >= 0; i--)
                    if (active[i].Definition.exclusiveBuff && active[i].Definition != def) End(i, false);
                Recalculate();
            }
            if (def.hostile && def.cancelsBuffs) EndWhere(e => !e.Definition.hostile && !e.Definition.survivesHits);
        }

        GameObject SpawnAttachedVfx(StatusEffectDefinition def)
        {
            if (def.attachedVfxPrefab == null) return null;
            var parent = def.attachToVisual && kart != null ? Powerups.KartMounts.Parent(kart) : transform;
            var vfx = Instantiate(def.attachedVfxPrefab, parent);
            if (def.attachMount != HeldMount.Origin && kart != null)
                vfx.transform.localPosition += Powerups.KartMounts.Point(kart, def.attachMount);
            return vfx;
        }

        void ApplyImpulses(StatusEffectDefinition def)
        {
            if (kart == null || kart.Physics == null) return;
            var rb = kart.Physics.Body;
            if (!Mathf.Approximately(def.velocityRetain, 1f))
                rb.linearVelocity = Vector3.Scale(rb.linearVelocity, new Vector3(def.velocityRetain, 1f, def.velocityRetain));
            if (def.freezeKart)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            if (def.forwardImpulse != 0f) rb.linearVelocity += transform.forward * (def.forwardImpulse * kart.Physics.SpeedScale);
            if (def.upwardImpulse != 0f) rb.linearVelocity += Vector3.up * def.upwardImpulse;
        }

        void ApplyHealthAndInventory(StatusEffectDefinition def, Kart source)
        {
            if (def.dropsHeldItem && kart != null && kart.Inventory != null) kart.Inventory.Clear();
            if (kart == null) return;
            if (def.heal > 0) kart.Heal(def.heal);
            if (def.hostile && source != null && source != kart)
                kart.TakeDamage(def.damage >= 0 ? def.damage : Core.GameRoot.Instance.Config.defaultHitDamage, source);
        }

        public bool Has(StatusEffectDefinition def) => Find(def) != null;

        // Same object or a definition with the same id: a mod's prefab holds its own copy of a core effect.
        ActiveEffect Find(StatusEffectDefinition def)
        {
            for (var i = 0; i < active.Count; i++)
                if (Same(active[i].Definition, def)) return active[i];
            return null;
        }

        static bool Same(StatusEffectDefinition a, StatusEffectDefinition b) => a == b || (a != null && b != null && a.Id == b.Id);

        public bool HasRespawnPending => active.Exists(a => a.Definition.respawnOnEnd);

        public void Remove(StatusEffectDefinition def)
        {
            for (var i = active.Count - 1; i >= 0; i--)
                if (Same(active[i].Definition, def)) End(i, false);
            Recalculate();
        }

        /// <summary>Ends all effects without triggering their end-of-effect respawn (used by respawns).</summary>
        public void ClearAll()
        {
            for (var i = active.Count - 1; i >= 0; i--) End(i, false);
            Recalculate();
        }

        /// <summary>Ends exclusive buffs (MHK2: using any item ends shield / blue light / turbo).</summary>
        public void EndExclusiveBuffs() => EndWhere(e => e.Definition.exclusiveBuff);

        void EndWhere(Predicate<ActiveEffect> match)
        {
            for (var i = active.Count - 1; i >= 0; i--)
                if (match(active[i])) End(i, false);
            Recalculate();
        }

        void End(int index, bool natural)
        {
            var e = active[index];
            var d = e.Definition;
            active.RemoveAt(index);
            if (natural && d.endVfxPrefab != null)
                Powerups.PowerupFx.Spawn(d.endVfxPrefab, e.Vfx != null ? e.Vfx.transform.position : transform.position);
            if (e.Vfx != null)
            {
                if (natural) ReleaseWorldParticles(e.Vfx);
                Destroy(e.Vfx);
            }
            if (natural && d.endSound != null) AudioManager.PlaySfx(d.endSound, transform.position);
            Ended?.Invoke(d);
            if (natural && d.respawnOnEnd && kart != null) kart.RespawnBehind(d.respawnBackDistance);
        }

        static void ReleaseWorldParticles(GameObject vfx)
        {
            foreach (var ps in vfx.GetComponentsInChildren<ParticleSystem>())
            {
                var main = ps.main;
                if (ps.gameObject == vfx || main.simulationSpace != ParticleSystemSimulationSpace.World || ps.particleCount == 0) continue;
                ps.transform.SetParent(Powerups.PowerupFx.Root, true);
                ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                Destroy(ps.gameObject, main.startLifetime.constantMax + ParticleReleasePadding);
            }
        }

        void FixedUpdate()
        {
            if (active.Count == 0) return;
            var dt = Time.fixedDeltaTime;
            TickTimers(dt);
            if (kart != null && kart.Physics != null && kart.Physics.JustLanded) Land();
            Recalculate();
            ApplyContinuous(dt);
            UpdateSpin();
            ApplyAuras();
        }

        void TickTimers(float dt)
        {
            expired.Clear();
            foreach (var e in active)
            {
                var before = e.TimeLeft;
                e.TimeLeft -= dt;
                var lead = e.Definition.expiringSoundLead;
                if (e.Definition.expiringSound != null && before > lead && e.TimeLeft <= lead)
                    AudioManager.PlaySfx(e.Definition.expiringSound, transform.position);
                if (e.TimeLeft <= 0f) expired.Add(e);
            }
            // Ending can respawn the kart, which clears the list: resolve indices one at a time.
            foreach (var e in expired)
            {
                var idx = active.IndexOf(e);
                if (idx >= 0) End(idx, true);
            }
        }

        void Land()
        {
            landingEffects.Clear();
            for (var i = active.Count - 1; i >= 0; i--)
            {
                var d = active[i].Definition;
                if (d.landingEffect != null) landingEffects.Add(d.landingEffect);
                if (d.endOnLanding) End(i, true);
            }
            foreach (var d in landingEffects) Apply(d, kart);
        }

        void ApplyAuras()
        {
            foreach (var e in active)
            {
                var aura = e.Definition.auraEffect;
                if (aura == null) continue;
                var r2 = e.Definition.auraRadius * e.Definition.auraRadius;
                var karts = Kart.All;
                for (var i = 0; i < karts.Count; i++)
                {
                    var other = karts[i];
                    if (other == kart || other.Effects.Has(aura) || other.Effects.Ghost) continue;
                    if ((other.transform.position - transform.position).sqrMagnitude < r2) KartReactions.Apply(other, aura, kart);
                }
            }
        }

        void ApplyContinuous(float dt)
        {
            if (kart == null || kart.Physics == null) return;
            var rb = kart.Physics.Body;
            var damping = 0f;
            foreach (var e in active) damping += e.Definition.velocityDamping;
            if (Frozen)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            else if (damping > 0f)
            {
                var k = Mathf.Exp(-damping * dt);
                var v = rb.linearVelocity;
                rb.linearVelocity = new Vector3(v.x * k, v.y, v.z * k);
            }
        }

        void UpdateSpin()
        {
            var spin = 0f;
            foreach (var e in active)
            {
                if (e.Definition.spinTurns <= 0f) continue;
                // Ease-out: fast at the start, settling to a full multiple of 360°.
                var t = 1f - Mathf.Pow(1f - e.Progress, 2f);
                spin += t * e.Definition.spinTurns * 360f;
            }
            SpinAngle = spin % 360f;
        }

        void Recalculate()
        {
            var forwardSpeed = kart != null && kart.Physics != null ? kart.Physics.ForwardSpeedOriginal : 0f;
            var m = StatusModifiers.Identity;
            foreach (var e in active) m.Accumulate(e, forwardSpeed);
            Modifiers = m;
            if (active.Count == 0) SpinAngle = 0f;
        }

        /// <summary>Whether <paramref name="t"/> belongs to the attached visual of an active effect (stays opaque when the kart fades).</summary>
        public bool IsEffectAttachment(Transform t)
        {
            foreach (var e in active)
                if (e.Vfx != null && t.IsChildOf(e.Vfx.transform)) return true;
            return false;
        }
    }
}
