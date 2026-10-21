using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Placed magnet (MHK3): catches the nearest kart that comes within <see cref="radius"/>, one at a time, and holds it
    /// on a leash that shortens towards the magnet. The kart keeps its controls and velocity, only its position is held,
    /// so it drives off at the speed it built up when the magnet lets go. Never catches its user or ghost karts.
    /// </summary>
    [DefaultExecutionOrder(100)] // after Kart: holds the position the physics step is about to move from
    public partial class MagnetPowerup : PowerupBehaviour, IPlantedHazard
    {
        const float SightHeight = 0.5f;

        [Tooltip("A kart whose centre comes this close (m) is caught: the nearest one, if the magnet is free. MHK3: 5.")]
        public float radius = 5f;
        [Tooltip("Applied to the caught kart. The magnet holds the kart as long as it has this effect, so its duration is " +
                 "the hold time (MHK3: 2.7 s). Use one that leaves throttle, steering and speed alone.")]
        [SerializeField] StatusEffectDefinition catchEffect;
        [Tooltip("How fast (m/s) the leash shortens from the catch distance towards holdDistance: the slow pull towards " +
                 "the magnet, whatever the kart does.")]
        [SerializeField] float pullSpeed = 2f;
        [Tooltip("The leash stops shortening at this distance (m). Inside it the held kart moves freely.")]
        [SerializeField] float holdDistance = 1.5f;
        [Tooltip("Seconds the magnet waits for a kart before it vanishes. After a catch it vanishes when it lets go.")]
        [SerializeField] float lifetime = 120f;
        [Tooltip("Visual scale grows from 0 to 1 over this time.")]
        [SerializeField] float growTime = 0.5f;
        [Tooltip("One-shot at the kart when it is caught.")]
        [SerializeField] AudioClip catchSound;

        [AutoStaticsCleanup] static readonly HashSet<Kart> held = new();

        Kart victim;
        float leash;
        float active;
        Vector3 baseScale;

        /// <summary>A magnet holds <paramref name="kart"/> right now (its driver should not count it as stuck).</summary>
        public static bool Holds(Kart kart) => kart != null && held.Contains(kart);

        public Vector3 Position => transform.position;
        /// <summary>The catch radius while the magnet is down and free. 0 in flight or while it holds a kart.</summary>
        public float DangerRadius => InFlight || victim != null ? 0f : radius;
        public bool LooksLikeItemBox => false;

        protected override void OnLaunch() => baseScale = transform.localScale;

        void OnEnable() => PlantedHazards.Register(this);

        void OnDisable()
        {
            PlantedHazards.Unregister(this);
            if (ReferenceEquals(victim, null)) return;
            held.Remove(victim);
            if (victim != null && victim.Effects.Has(catchEffect)) victim.Effects.Remove(catchEffect);
            victim = null;
        }

        void Update()
        {
            if (InFlight) return;
            transform.localScale = baseScale * Mathf.Clamp01(growTime > 0f ? active / growTime : 1f);
        }

        void FixedUpdate()
        {
            if (InFlight) return;
            active += Time.fixedDeltaTime;
            if (!ReferenceEquals(victim, null)) Hold(Time.fixedDeltaTime);
            else if (active > lifetime) Destroy(gameObject);
            else TryCatch();
        }

        void TryCatch()
        {
            if (catchEffect == null) return;
            var centre = transform.position;
            var k = Nearest(centre);
            if (k == null || !KartReactions.Apply(k, catchEffect, Owner)) return;
            victim = k;
            held.Add(k);
            var p = k.Physics.Body.position;
            leash = new Vector2(p.x - centre.x, p.z - centre.z).magnitude;
            PlaySfx(catchSound, p);
        }

        Kart Nearest(Vector3 centre)
        {
            Kart best = null;
            var bestSqr = radius * radius;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (k == Owner || !IsTangible(k) || k.Effects.IsProtected || held.Contains(k)) continue;
                var sqr = (k.Physics.Body.position - centre).sqrMagnitude;
                if (sqr > bestSqr || !InSight(k, centre)) continue;
                best = k;
                bestSqr = sqr;
            }
            return best;
        }

        // Nothing is caught through a wall or the road: the hold would pull it through.
        static bool InSight(Kart k, Vector3 centre) =>
            !Physics.Linecast(centre + Vector3.up * SightHeight, k.Physics.Body.position + Vector3.up * SightHeight,
                k.Physics.GroundMask, QueryTriggerInteraction.Ignore);

        // Moves the kart back onto the leash circle where this step would carry it beyond. Its velocity is not touched.
        void Hold(float h)
        {
            if (!IsTangible(victim) || !victim.Effects.Has(catchEffect))
            {
                held.Remove(victim);
                victim = null;
                Destroy(gameObject);
                return;
            }
            leash = Mathf.Max(holdDistance, leash - pullSpeed * h);
            var body = victim.Physics.Body;
            var centre = transform.position;
            var next = body.position + body.linearVelocity * h;
            var off = new Vector3(next.x - centre.x, 0f, next.z - centre.z);
            var dist = off.magnitude;
            if (dist <= leash) return;
            body.MovePosition(body.position + off * (leash / dist - 1f));
        }
    }
}
