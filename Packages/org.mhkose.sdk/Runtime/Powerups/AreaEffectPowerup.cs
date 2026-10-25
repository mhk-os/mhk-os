using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

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

        protected override void OnLaunch() => baseScale = transform.localScale;

        void OnEnable() => PlantedHazards.Register(this);

        void OnDisable()
        {
            PlantedHazards.Unregister(this);
            Effects.EffectLoopSound.Stop(ref loop);
        }

        void Update()
        {
            if (InFlight) return;
            if (followOwner && Owner != null) transform.position = Owner.transform.position;
            if (loop == null) loop = Effects.EffectLoopSound.Play(gameObject, idleLoop);
            active += Time.deltaTime;
            transform.localScale = baseScale * Mathf.Clamp01(growTime > 0 ? active / growTime : 1f);
            if (active > lifetime)
            {
                Despawn(null, transform.position, expireSound);
                return;
            }
            if (active < armDelay) return;

            var center = transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!Affects(k)) continue;
                var dist = (center - k.transform.position).magnitude;
                if (dist > radius) continue;
                ApplyField(k);
                if (TryCoreHit(k, dist)) return;
            }
        }

        // The pull is a force, so it runs once per physics step whatever the frame rate.
        void FixedUpdate()
        {
            if (gravity <= 0f || !Acting) return;
            var center = transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!Affects(k) || k.Effects.Shielded) continue;
                var to = center - k.transform.position;
                var dist = to.magnitude;
                if (dist > radius) continue;
                var strength = gravity * Mathf.Lerp(CentreGravityFactor, 1f, dist / radius);
                var dir = new Vector3(to.x, 0, to.z).normalized;
                k.Physics.Body.AddForce(dir * strength, ForceMode.Acceleration);
            }
        }

        bool Affects(Kart k) => !(k == Owner && (!affectsOwner || OwnerImmune(k))) && !k.Effects.Ghost;

        void ApplyField(Kart k)
        {
            if (fieldEffect == null) return;
            if (!fieldEffectFullDuration) KartReactions.Apply(k, fieldEffect, Owner, FieldRefreshSeconds, Usage);
            else if (!k.Effects.Has(fieldEffect)) KartReactions.Apply(k, fieldEffect, Owner, -1f, Usage);
        }

        bool TryCoreHit(Kart k, float dist)
        {
            if (dist >= coreRadius || coreEffect == null || !coreHit.Add(k)) return false;
            if (KartReactions.Apply(k, coreEffect, Owner, -1f, Usage)) PlaySfx(hitSound, k.transform.position);
            if (!destroyOnCoreHit) return false;
            Destroy(gameObject);
            return true;
        }
    }
}
