using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;
using UnityEngine.Pool;

namespace MHK.Powerups
{
    /// <summary>
    /// Field that acts on karts inside a radius: pulls them towards the centre and/or applies an effect inside it or its
    /// core (swallowed, frozen, x-field, electroshock...). Waits for an <see cref="ILandingPowerup"/> to land, or stays
    /// centred on its user (<see cref="followOwner"/>). Ghost karts are not affected.
    /// </summary>
    public class AreaEffectPowerup : PowerupBehaviour, IPlantedHazard
    {
        const float FieldRefreshSeconds = 0.25f;
        const float CentreGravityFactor = 2f;
        const float FlatThickness = 0.001f;

        public float radius = 8f;
        [Tooltip("Acceleration (m/s²) towards the centre at the edge. It scales up towards the centre.")]
        [SerializeField] float gravity;
        [SerializeField] float coreRadius = 1.5f;
        [SerializeField] StatusEffectDefinition coreEffect;
        [Tooltip("Applied continuously (refreshing) while inside the radius.")]
        [SerializeField] StatusEffectDefinition fieldEffect;
        [Tooltip("Off: the field effect is kept alive while the kart is inside and ends shortly after it leaves. On: a kart " +
                 "inside gets the effect with its full duration, renewed only once it has run out while the kart is still " +
                 "inside (TINcan jamming signal, shocker, flame thrower).")]
        [SerializeField] bool fieldEffectFullDuration;
        [SerializeField] float lifetime = 8f;
        [Tooltip("Seconds after landing before the field acts on anyone: a visible arming. 0 = at once, so a kart right " +
                 "behind is caught.")]
        [SerializeField] float armDelay;
        [Tooltip("Acts on its user too once GameConfig.ownerHazardImmunity has passed since it was placed (placed fields). " +
                 "Off for fields released around the user (electroshock).")]
        [SerializeField] bool affectsOwner;
        [SerializeField] bool destroyOnCoreHit;
        [Tooltip("Visual scale grows from 0 to 1 over this time.")]
        [SerializeField] float growTime = 0.5f;
        [Tooltip("Stays centred on the user's kart (blasts released around the kart) instead of where it was spawned.")]
        [SerializeField] bool followOwner;

        [Header("Sounds")]
        [Tooltip("Loop while the field is active (from landing / activation until it ends).")]
        [SerializeField] Audio.LoopSound idleLoop = new();
        [Tooltip("One-shot at a kart that gets the core effect.")]
        [SerializeField] AudioClip hitSound;
        [Tooltip("One-shot when the field ends because its lifetime ran out.")]
        [SerializeField] AudioClip expireSound;

        float active;
        readonly HashSet<Kart> coreHit = new();
        Vector3 baseScale;
        Effects.EffectLoopSound loop;

        public Vector3 Position => transform.position;
        /// <summary>
        /// The whole field with a field effect. With only a pull, half-way in, where it gets too strong to escape. None
        /// for a field carried by its user (nothing to steer around).
        /// </summary>
        public float DangerRadius => InFlight || followOwner ? 0f
            : fieldEffect != null ? radius : gravity > 0f ? Mathf.Lerp(coreRadius, radius, 0.5f) : coreRadius;
        public bool LooksLikeItemBox => false;

        bool Acting => !InFlight && active >= armDelay;

        protected override void OnLaunch()
        {
            baseScale = transform.localScale;
            StampGroundVisuals();
        }

        // Flat visuals at or below the field's origin (its user's kart origin) lie on the track, like the signal jammer
        // wave: stamped onto the ground under them (GroundStamp), not left in the plane of the kart's tilt at activation.
        // Flat rings floating around the kart (flame thrower, electroshock) stay as modelled.
        void StampGroundVisuals()
        {
            using (ListPool<MeshFilter>.Get(out var filters))
            {
                GetComponentsInChildren(filters);
                foreach (var f in filters)
                {
                    var mesh = f.sharedMesh;
                    if (mesh == null || mesh.bounds.extents.y > FlatThickness || !f.TryGetComponent<MeshRenderer>(out var r)) continue;
                    if (transform.InverseTransformPoint(f.transform.TransformPoint(mesh.bounds.center)).y > 0f) continue;
                    GroundStamp.Attach(f, r);
                }
            }
        }

        void OnEnable() => PlantedHazards.Register(this);

        void OnDisable()
        {
            PlantedHazards.Unregister(this);
            Effects.EffectLoopSound.Stop(ref loop);
        }

        // Growth and following the user are drawn every frame; the field acts in physics steps.
        void LateUpdate()
        {
            if (InFlight) return;
            if (followOwner && Owner != null) transform.position = Owner.transform.position;
            transform.localScale = baseScale * Mathf.Clamp01(growTime > 0 ? active / growTime : 1f);
        }

        protected override void Step(float dt)
        {
            if (InFlight) return;
            if (followOwner && Owner != null) transform.position = Owner.transform.position;
            if (loop == null) loop = Effects.EffectLoopSound.Play(gameObject, idleLoop);
            active += dt;
            if (active > lifetime)
            {
                // A field carried by its user ends at the kart and its expire sound goes with it.
                if (followOwner && Owner != null)
                {
                    PlaySfx(expireSound, Owner.transform);
                    Remove();
                }
                else Despawn(null, transform.position, expireSound);
                return;
            }
            if (active < armDelay) return;

            var center = transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!Affects(k)) continue;
                var to = center - k.transform.position;
                var dist = to.magnitude;
                if (dist > radius) continue;
                Pull(k, to, dist);
                ApplyField(k);
                if (TryCoreHit(k, dist)) return;
            }
        }

        // The pull is an acceleration on karts simulated here.
        void Pull(Kart k, Vector3 to, float dist)
        {
            if (gravity <= 0f || k.Effects.Shielded || !MovesHere(k)) return;
            var strength = gravity * Mathf.Lerp(CentreGravityFactor, 1f, dist / radius);
            var dir = new Vector3(to.x, 0, to.z).normalized;
            k.Physics.Body.AddForce(dir * strength, ForceMode.Acceleration);
        }

        protected internal override float NetHitReach => destroyOnCoreHit && coreEffect != null ? coreRadius : 0f;

        // Another client saw a kart reach the core of a field that ends there: it ends here too.
        protected internal override void OnNetHit(Kart victim)
        {
            if (destroyOnCoreHit && coreEffect != null && !InFlight) Remove();
        }

        bool Affects(Kart k) => !(k == Owner && (!affectsOwner || OwnerImmune(k))) && !k.Effects.Ghost;

        void ApplyField(Kart k)
        {
            if (fieldEffect == null) return;
            if (!fieldEffectFullDuration) HitKart(k, fieldEffect, FieldRefreshSeconds);
            else if (!k.Effects.Has(fieldEffect)) HitKart(k, fieldEffect);
        }

        bool TryCoreHit(Kart k, float dist)
        {
            if (dist >= coreRadius || coreEffect == null || !coreHit.Add(k)) return false;
            if (HitKart(k, coreEffect)) PlaySfx(hitSound, k.transform);
            if (!destroyOnCoreHit) return false;
            Remove();
            return true;
        }
    }
}
