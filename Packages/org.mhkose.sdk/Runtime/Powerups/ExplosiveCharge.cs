using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// A thrown charge (Pumpkin's grenade, combine with a <see cref="PlacementPowerup"/>) that explodes when a kart comes
    /// within <see cref="contactRadius"/> or when it lands. Karts in <see cref="hitRadius"/> get <see cref="hitEffect"/>,
    /// karts out to <see cref="splashRadius"/> get <see cref="splashEffect"/> and are pushed away.
    /// </summary>
    public class ExplosiveCharge : PowerupBehaviour
    {
        const float SmokeLinger = 3f;
        const float MinSmokeDuration = 0.05f;
        const float MinPushDistance = 0.01f;

        [Header("Trigger")]
        [Tooltip("A kart whose origin comes this close to the flying charge sets it off (m).")]
        [SerializeField] float contactRadius = 1.4f;
        [Tooltip("Tumble of the flying charge (deg/s, local axes).")]
        [SerializeField] Vector3 flightSpin = new(360f, 90f, 0f);
        [Tooltip("Model spun while flying (defaults to this object).")]
        [SerializeField] Transform model;

        [Header("Blast")]
        [Tooltip("Karts within this distance of the blast get the hit effect (m).")]
        public float hitRadius = 3f;
        [SerializeField] StatusEffectDefinition hitEffect;
        [Tooltip("Karts beyond the hit radius up to this distance get the splash effect (m).")]
        [SerializeField] float splashRadius = 6f;
        [SerializeField] StatusEffectDefinition splashEffect;
        [Tooltip("Horizontal push away from the blast for splashed karts at the hit radius, fading to 0 at the splash radius (m/s).")]
        [SerializeField] float splashPush = 8f;
        [Tooltip("Upward speed given to splashed karts (m/s).")]
        [SerializeField] float splashLift = 3f;
        [Tooltip("Karts higher or lower than this above the blast are not affected (m).")]
        [SerializeField] float blastHeight = 3f;

        [Header("Presentation")]
        [Tooltip("Spawned at the blast (e.g. VFX_Explosion).")]
        [SerializeField] GameObject explosionVfx;
        [Tooltip("Additional one-shot burst (e.g. pumpkin pieces).")]
        [SerializeField] GameObject debrisVfx;
        [Tooltip("Smoke left where it went off. Its emitters are stopped after smokeSeconds.")]
        [SerializeField] GameObject smokeVfx;
        [SerializeField] float smokeSeconds = 1.2f;
        [Tooltip("Height above the blast point at which the effects are spawned (m).")]
        [SerializeField] float vfxHeight = 0.6f;
        [Tooltip("One-shot at the blast. A flight loop or throw sound goes on the PlacementPowerup or the definition's useSound.")]
        [SerializeField] AudioClip explosionSound;
        [Tooltip("Visual hop of the karts around the blast (m, 0 = none).")]
        [SerializeField] float quakeAmplitude = 0.25f;
        [Tooltip("Length of the visual hop (s).")]
        [SerializeField] float quakeDuration = 0.5f;
        [Tooltip("Hops per second.")]
        [SerializeField] float quakeFrequency = 6f;
        [Tooltip("The hop spreads outwards at this speed (m/s).")]
        [SerializeField] float quakeWaveSpeed = 25f;
        [Tooltip("Share of the hop height lost by its end.")]
        [SerializeField, Range(0f, 1f)] float quakeDecay = 0.8f;

        bool exploded;

        protected override void OnLaunch()
        {
            if (model == null) model = transform;
        }

        // The tumble is visual only: it follows frame time.
        void Update()
        {
            if (!exploded && !HasLanded && model != null && model != transform) model.Rotate(flightSpin * Time.deltaTime, Space.Self);
        }

        protected override void Step(float dt)
        {
            if (exploded || Owner == null) return;
            if (HasLanded || KartInContact()) Explode(transform.position);
        }

        protected internal override float NetHitReach => splashRadius;

        // Another client saw the blast hit a racer: it goes off here too, where it is now.
        protected internal override void OnNetHit(Kart victim)
        {
            if (!exploded && Owner != null) Explode(SimulatedPosition);
        }

        bool KartInContact()
        {
            var p = transform.position;
            var r2 = contactRadius * contactRadius;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (OwnerImmune(k) || !IsTangible(k)) continue;
                if ((k.transform.position - p).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        void Explode(Vector3 at)
        {
            exploded = true;
            SpawnBlastFx(at);
            PlaySfx(explosionSound, at);
            if (quakeAmplitude > 0f) KartQuake.Shake(at, splashRadius, quakeDuration, quakeAmplitude, quakeFrequency, quakeWaveSpeed, quakeDecay);
            var karts = Kart.All;
            for (var i = karts.Count - 1; i >= 0; i--)
            {
                var k = karts[i];
                if (!IsTangible(k) || OwnerImmune(k)) continue;
                AffectKart(k, at);
            }
            Remove();
        }

        void SpawnBlastFx(Vector3 at)
        {
            var vfxPos = at + Vector3.up * vfxHeight;
            PowerupFx.Spawn(explosionVfx, vfxPos);
            PowerupFx.Spawn(debrisVfx, vfxPos);
            if (smokeVfx != null) SpawnSmoke(vfxPos);
        }

        void SpawnSmoke(Vector3 at)
        {
            var smoke = PowerupFx.Instantiate(smokeVfx, at, Quaternion.identity);
            foreach (var ps in smoke.GetComponentsInChildren<ParticleSystem>())
            {
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.loop = false;
                main.duration = Mathf.Max(MinSmokeDuration, smokeSeconds);
                ps.Play(false);
            }
            Destroy(smoke, smokeSeconds + SmokeLinger);
        }

        void AffectKart(Kart k, Vector3 at)
        {
            var d = k.transform.position - at;
            if (Mathf.Abs(d.y) > blastHeight) return;
            var flat = new Vector3(d.x, 0f, d.z);
            var dist = flat.magnitude;
            if (dist <= hitRadius)
            {
                KartReactions.Apply(k, hitEffect, Owner, -1f, Usage);
            }
            else if (dist <= splashRadius && KartReactions.Apply(k, splashEffect, Owner, -1f, Usage) && MovesHere(k))
            {
                var fade = 1f - Mathf.InverseLerp(hitRadius, splashRadius, dist);
                var away = dist > MinPushDistance ? flat / dist : Vector3.zero;
                k.Physics.Body.linearVelocity += away * (splashPush * fade) + Vector3.up * (splashLift * fade);
            }
        }
    }
}
