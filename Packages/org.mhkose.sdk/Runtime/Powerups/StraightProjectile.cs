using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    public enum ProjectileAim
    {
        [Tooltip("Along the shooter's facing, including its pitch and roll (MHK3 froster / shrinker beams, TINcan rocket).")] KartForward,
        [Tooltip("Along the racing line's direction at the shooter, including slope (MHK Extra fireworks rocket).")] TrackDirection,
    }

    /// <summary>
    /// Weightless shot that flies in a straight 3D line, slowing down with its deceleration, and dies on solid geometry
    /// (unless it passes through the world) or at the end of its lifetime. It can pierce karts and starts at a held
    /// <see cref="HeldProjectileAim"/> visual when there is one.
    /// </summary>
    public class StraightProjectile : ProjectileBehaviour, INetProjectile
    {
        const float SegmentEpsilon = 1e-8f;
        const float MinCorrectSpeedSqr = 0.01f;

        [Header("Flight")]
        public ProjectileAim aim = ProjectileAim.KartForward;
        [Tooltip("Fly at least this much faster than the shooter's speed along the aim, so it is never overtaken (m/s, negative = off).")]
        public float minSpeedOverOwner = -1f;
        [Tooltip("Spawn height above the kart origin, along the kart's up axis (m).")]
        [SerializeField] float launchHeight = 0.6f;
        [Tooltip("Radius of the swept sphere that detects solid level geometry (m).")]
        [SerializeField] float collisionRadius = 0.15f;
        [Tooltip("Fly through walls, props and terrain, ended only by its lifetime or a kart hit (MHK3 soap bubbles).")]
        public bool passThroughWorld;
        [Tooltip("Smoothing (m) of the racing-line tangent used by TrackDirection aiming.")]
        public float trackTangentSmoothing = 3f;

        [Header("Hits")]
        [Tooltip("Pass through karts and keep flying (each kart is hit at most once).")]
        [SerializeField] bool piercing;
        [Tooltip("The shooter can be hit by its own projectile once this many seconds passed (negative = never).")]
        [SerializeField] float ownerImmunity = -1f;

        [Header("Presentation")]
        [Tooltip("Spawned where a kart is hit.")]
        [SerializeField] GameObject hitVfx;
        [Tooltip("Spawned where the projectile meets solid level geometry.")]
        [SerializeField] GameObject terrainVfx;
        [Tooltip("Spawned when the lifetime runs out.")]
        [SerializeField] GameObject expireVfx;
        [Tooltip("Height above the kart origin at which kart-hit effects are spawned (m).")]
        [SerializeField] float hitVfxHeight = 0.6f;
        [Tooltip("Model child that starts at the held item (HeldProjectileAim) and glides onto the flight line (optional).")]
        [SerializeField] Transform visual;
        [Tooltip("Distance flown (m) over which the model glides from the held item onto the flight line.")]
        [SerializeField] float joinDistance = 6f;

        Vector3 direction;
        bool launched;
        readonly HashSet<Kart> hitKarts = new();
        Vector3 visualRest, joinOffset;
        float flown;
        bool joining;

        void Reset()
        {
            lifetime = 30f;
            hitRadius = 1.2f;
        }

        /// <summary>
        /// Unit direction a shot of <paramref name="owner"/> flies with <paramref name="aim"/>, reversed when aiming
        /// <paramref name="backwards"/>. Shared by the shot and its held preview.
        /// </summary>
        public static Vector3 AimDirection(Kart owner, ProjectileAim aim, float tangentSmoothing, bool backwards)
        {
            var dir = owner.transform.forward;
            if (aim == ProjectileAim.TrackDirection && TrackRuntime.TryGetMainPath(out var path))
            {
                var tangent = path.GetTangent(owner.Progress.PathDistance, tangentSmoothing);
                if (tangent.sqrMagnitude > 0.5f) dir = tangent.normalized;
            }
            return backwards ? -dir : dir;
        }

        /// <summary>Orientation of a shot or held preview flying along <paramref name="direction"/>: rolled with the kart when aiming along it.</summary>
        public static Quaternion AimRotation(Kart owner, ProjectileAim aim, Vector3 direction) =>
            Quaternion.LookRotation(direction, aim == ProjectileAim.KartForward ? owner.transform.up : Vector3.up);

        public bool Flying => launched;
        public Vector3 NetPosition => SimulatedPosition;
        public Vector3 NetVelocity => direction * speed;

        public void Correct(Vector3 position, Vector3 velocity)
        {
            BeginCorrection(position);
            if (velocity.sqrMagnitude > MinCorrectSpeedSqr) direction = velocity.normalized;
        }

        protected internal override float NetHitReach => hitRadius;

        protected internal override void OnNetHit(Kart victim)
        {
            if (!hitKarts.Add(victim) || piercing) return;
            PowerupFx.Spawn(hitVfx, victim.transform.position + victim.transform.up * hitVfxHeight, hitSound, soundVolume);
            Remove();
        }

        protected override void OnLaunch()
        {
            var t = Owner.transform;
            direction = AimDirection(Owner, aim, trackTangentSmoothing, Context.backwards);
            transform.SetPositionAndRotation(t.position + t.up * launchHeight + direction * spawnDistance,
                AimRotation(Owner, aim, direction));
            // Original world speeds and lifetimes (not scaled with the karts' SpeedScale): shots fly their original range.
            if (minSpeedOverOwner >= 0f && Owner.Physics != null)
                speed = Mathf.Max(speed, OwnerSpeedAlong(direction) + minSpeedOverOwner);
            launched = true;
            StartAtHeldItem();
            PlayLaunch();
            InterpolatePose();
        }

        // The held copy above the shooter's head hides for the item's cooldown while the model glides from it.
        void StartAtHeldItem()
        {
            var inv = Owner.Inventory;
            var held = inv != null ? inv.HeldVisual : null;
            if (visual == null || held == null || !held.TryGetComponent<HeldProjectileAim>(out _)) return;
            visualRest = visual.localPosition;
            joinOffset = held.position - transform.position;
            joining = joinDistance > 0f;
            UpdateJoin();
            inv.HideHeldVisual(Context.definition != null ? Context.definition.useCooldown : 0f);
        }

        void UpdateJoin()
        {
            var k = joining ? 1f - Mathf.SmoothStep(0f, 1f, flown / joinDistance) : 0f;
            visual.localPosition = visualRest + transform.InverseTransformVector(joinOffset * k);
            if (k <= 0f) joining = false;
        }

        protected override void Step(float dt)
        {
            if (!launched || TickLifetime(dt, expireVfx)) return;
            ApplyCorrection();
            Decelerate(dt);
            var from = transform.position;
            var stepLength = speed * dt;
            var wall = default(RaycastHit);
            var blocked = !passThroughWorld && ItemPhysics.SphereCast(from, collisionRadius, direction, stepLength, out wall, worldMask);
            var to = blocked ? from + direction * wall.distance : from + direction * stepLength;
            if (CheckHits(from, to)) return;
            if (blocked)
            {
                Despawn(terrainVfx, wall.point, terrainSound);
                return;
            }
            transform.position = to;
            if (!joining) return;
            flown += (to - from).magnitude;
            UpdateJoin();
        }

        bool CheckHits(Vector3 from, Vector3 to)
        {
            var r2 = hitRadius * hitRadius;
            for (var i = Kart.All.Count - 1; i >= 0; i--)
            {
                var k = Kart.All[i];
                if (!IsTangible(k) || hitKarts.Contains(k)) continue;
                if (k == Owner && (ownerImmunity < 0f || Age < ownerImmunity)) continue;
                var p = k.transform.position;
                if (GeometryUtil.DistanceSqToSegment(p, from, to, SegmentEpsilon) > r2) continue;

                hitKarts.Add(k);
                HitKart(k, hitEffect);
                PowerupFx.Spawn(hitVfx, p + k.transform.up * hitVfxHeight, hitSound, soundVolume);
                if (!piercing)
                {
                    Remove();
                    return true;
                }
            }
            return false;
        }
    }
}
