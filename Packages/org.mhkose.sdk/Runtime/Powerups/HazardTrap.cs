using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Track hazard (oil slick, banana, mine, slime, fake item box, x-field...) that applies <see cref="effect"/> to karts
    /// driving over it, its owner too once <see cref="PowerupBehaviour.OwnerHazardImmunity"/> has passed. Lives in a use
    /// prefab (waits for an <see cref="ILandingPowerup"/> to land) or directly in a track scene.
    /// </summary>
    public class HazardTrap : PowerupBehaviour, IPlantedHazard
    {
        const float TriggerHeight = 2f;
        const float MinCloseTime = 0.01f;

        [SerializeField] StatusEffectDefinition effect;
        [Tooltip("Hits karts whose centre comes within this distance (m, horizontally), unless hitModelFootprint is on.")]
        [SerializeField] float radius = 1.2f;
        [Tooltip("Hit area = the model: hits a kart whose chassis touches the trap's meshes seen from above (x-field " +
                 "plate, measured once it has landed) instead of using radius.")]
        [SerializeField] bool hitModelFootprint;
        [Tooltip("Number of karts it affects before disappearing (0 = unlimited).")]
        [SerializeField] int maxHits = 1;
        [Tooltip("Seconds until the hazard disappears on its own (0 = never).")]
        [SerializeField] float lifetime = 60f;
        [Tooltip("Seconds after landing before the hazard hits anyone: a visible arming (pair it with armSound). 0 = it " +
                 "hits at once, so a kart right behind the dropper is caught.")]
        [SerializeField] float armDelay;
        [Tooltip("Scale-in time after landing (black hole opening).")]
        [SerializeField] float growTime;
        [Tooltip("After the last hit, shrink away over this time instead of vanishing.")]
        [SerializeField] float closeTime;
        [SerializeField] GameObject triggerVfx;
        [Tooltip("Same kart cannot be hit again for this many seconds (for multi-hit hazards).")]
        [SerializeField] float rehitCooldown = 2f;
        [Tooltip("Looks like an item box (a fake box placed in a track scene). Traps of powerup family 'decoy' count " +
                 "as well. Computer drivers that do not see through it drive into it like into a real box.")]
        [SerializeField] bool looksLikeItemBox;

        [Header("Sounds")]
        [Tooltip("Loop while the hazard lies on the track (from landing until it is used up, closes or expires).")]
        [SerializeField] Audio.LoopSound idleLoop = new();
        [Tooltip("One-shot when the hazard becomes active, armDelay seconds after landing (e.g. a mine arming).")]
        [SerializeField] AudioClip armSound;
        [Tooltip("One-shot when a kart drives into it and gets the effect (MHK2 PFP_hit / PXF_hit).")]
        [SerializeField] AudioClip triggerSound;
        [Tooltip("One-shot instead of triggerSound when the kart is protected (shield, blue light) and refuses the effect " +
                 "(MHK2 PFP_dissolve). Empty = triggerSound.")]
        [SerializeField] AudioClip refusedSound;
        [Tooltip("One-shot when the hazard disappears because its lifetime ran out.")]
        [SerializeField] AudioClip expireSound;

        float age, activeAge, closing = -1f;
        int hits;
        Kart lastVictim;
        float lastHitTime = float.NegativeInfinity;
        Vector3 baseScale;
        Vector2 footprintCentre, footprintHalfSize;
        bool hasFootprint, footprintMeasured;
        bool armed;
        Effects.EffectLoopSound loop;

        public Vector3 Position => transform.position;
        public float DangerRadius => closing >= 0f || InFlight ? 0f : HitRadius;
        public bool LooksLikeItemBox => looksLikeItemBox || (Context.definition != null && Context.definition.family == PlantedHazards.DecoyFamily);

        float HitRadius
        {
            get
            {
                if (!hasFootprint) return radius;
                var s = transform.lossyScale;
                return new Vector2(footprintCentre.x * s.x, footprintCentre.y * s.z).magnitude
                       + new Vector2(footprintHalfSize.x * s.x, footprintHalfSize.y * s.z).magnitude;
            }
        }

        protected override void OnLaunch()
        {
            baseScale = transform.localScale;
            if (growTime > 0f) transform.localScale = Vector3.zero;
        }

        void OnEnable() => PlantedHazards.Register(this);

        void OnDisable()
        {
            PlantedHazards.Unregister(this);
            Effects.EffectLoopSound.Stop(ref loop);
        }

        void Start()
        {
            if (baseScale == Vector3.zero) baseScale = transform.localScale;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            age += dt;
            if (TickClosing(dt) || TickLifetime() || InFlight) return;
            if (loop == null) loop = Effects.EffectLoopSound.Play(gameObject, idleLoop);
            if (!footprintMeasured) MeasureFootprint();
            if (TickArming(dt)) HitKartsInRange();
        }

        bool TickClosing(float dt)
        {
            if (closing < 0f) return false;
            closing += dt;
            transform.localScale = baseScale * Mathf.Clamp01(1f - closing / Mathf.Max(MinCloseTime, closeTime));
            if (closing >= closeTime) Destroy(gameObject);
            return true;
        }

        bool TickLifetime()
        {
            if (lifetime <= 0 || age <= lifetime) return false;
            Despawn(null, transform.position, expireSound);
            return true;
        }

        bool TickArming(float dt)
        {
            activeAge += dt;
            if (growTime > 0f) transform.localScale = baseScale * Mathf.Clamp01(activeAge / growTime);
            if (activeAge < armDelay) return false;
            if (!armed)
            {
                armed = true;
                PlaySfx(armSound, transform.position);
            }
            return true;
        }

        void HitKartsInRange()
        {
            var p = transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (OwnerImmune(k) || k.Effects.Ghost) continue;
                if (k == lastVictim && Time.time - lastHitTime < rehitCooldown) continue;
                if (!InHitArea(k, p)) continue;
                Trigger(k);
                if (maxHits > 0 && hits >= maxHits)
                {
                    UsedUp();
                    return;
                }
            }
        }

        bool InHitArea(Kart k, Vector3 p)
        {
            var d = k.transform.position - p;
            if (Mathf.Abs(d.y) > TriggerHeight) return false;
            d.y = 0;
            return hasFootprint ? TouchesFootprint(k) : d.sqrMagnitude <= radius * radius;
        }

        void Trigger(Kart k)
        {
            hits++;
            lastVictim = k;
            lastHitTime = Time.time;
            var landed = HitKart(k, effect);
            PowerupFx.Spawn(triggerVfx, transform.position, !landed && refusedSound != null ? refusedSound : triggerSound, soundVolume);
        }

        void UsedUp()
        {
            Effects.EffectLoopSound.Stop(ref loop);
            if (closeTime > 0f) closing = 0f;
            else Destroy(gameObject);
        }

        void MeasureFootprint()
        {
            var s = transform.lossyScale;
            if (hitModelFootprint && (s.x == 0f || s.z == 0f)) return; // growing in from zero: measure next frame
            footprintMeasured = true;
            if (!hitModelFootprint) return;
            var toLocal = transform.worldToLocalMatrix;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = -min;
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (!BoundsUtil.ModelSpace(r, out var space)) continue;
                var b = r.localBounds;
                var m = toLocal * space;
                for (var i = 0; i < 8; i++)
                {
                    var c = m.MultiplyPoint3x4(BoundsUtil.Corner(b, i));
                    min = Vector2.Min(min, new Vector2(c.x, c.z));
                    max = Vector2.Max(max, new Vector2(c.x, c.z));
                }
            }
            if (min.x > max.x) return; // no meshes: keep the radius
            footprintCentre = (min + max) * 0.5f;
            footprintHalfSize = (max - min) * 0.5f;
            hasFootprint = true;
        }

        // Separating-axis test of the kart's chassis box and the trap's footprint, seen from above.
        bool TouchesFootprint(Kart k)
        {
            var chassis = k.Physics.Chassis;
            if (chassis == null) return false;
            var t = transform;
            var s = t.lossyScale;
            var a1 = FlatAxis(t.right);
            var a2 = FlatAxis(t.forward);
            var ca = t.TransformPoint(new Vector3(footprintCentre.x, 0f, footprintCentre.y));
            var ha = new Vector2(footprintHalfSize.x * Mathf.Abs(s.x), footprintHalfSize.y * Mathf.Abs(s.z));
            var kt = chassis.transform;
            var ks = kt.lossyScale;
            var b1 = FlatAxis(kt.right);
            var b2 = FlatAxis(kt.forward);
            var cb = kt.TransformPoint(chassis.center);
            var hb = new Vector2(chassis.size.x * 0.5f * Mathf.Abs(ks.x), chassis.size.z * 0.5f * Mathf.Abs(ks.z));
            var d = new Vector2(cb.x - ca.x, cb.z - ca.z);
            return Within(a1) && Within(a2) && Within(b1) && Within(b2);

            bool Within(Vector2 axis) =>
                Mathf.Abs(Vector2.Dot(d, axis)) <= ha.x * Mathf.Abs(Vector2.Dot(a1, axis)) + ha.y * Mathf.Abs(Vector2.Dot(a2, axis))
                                                  + hb.x * Mathf.Abs(Vector2.Dot(b1, axis)) + hb.y * Mathf.Abs(Vector2.Dot(b2, axis));
        }

        static Vector2 FlatAxis(Vector3 v)
        {
            var f = GeometryUtil.FlatNormalized(v, Vector3.right);
            return new Vector2(f.x, f.z);
        }
    }
}
