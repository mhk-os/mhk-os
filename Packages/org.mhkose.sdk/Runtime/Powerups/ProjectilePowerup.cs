using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Ground-following projectile (missiles, rockets, thrown balls). Optionally follows the racing line and homes in on
    /// a target once it is close enough (MHK2 homing missile model: activation and lock distances). A homing shot waits
    /// for a respawning target (falling or respawn-protected) behind it on the racing line and strikes when the protection
    /// ends, so a respawn never dodges it (like <see cref="TrackHomingMissile"/>).
    /// </summary>
    public class ProjectilePowerup : ProjectileBehaviour, INetProjectile
    {
        /// <summary>A shot inheriting the owner's speed flies at least this much faster than the owner (m/s).</summary>
        public const float OwnerSpeedMargin = 5f;
        const float SweepRadius = 0.25f;
        const float SweepSkin = 0.3f;
        const float MaxDrivableSlope = 50f;
        const float GroundProbeUp = 2f;
        const float GroundProbeDown = 6f;
        const float HoverSharpness = 20f;
        const float SegmentEpsilon = 1e-8f;
        const float MinCorrectSpeedSqr = 0.01f;
        const float MinTravelSqr = 1e-4f;
        const float WaitArriveDistance = 0.5f;
        const float PathTangentSmoothing = 3f;

        [Header("Flight")]
        [Tooltip("Do not add the shooter's speed to the launch speed.")]
        public bool ignoreOwnerSpeed;
        [Tooltip("Allow the look-back input to launch this projectile backwards.")]
        public bool allowBackwards = true;
        [Tooltip("Seconds during which the owner cannot be hit by its own projectile.")]
        [SerializeField] float ownerImmunity = 0.6f;
        [SerializeField] bool followGround = true;
        [SerializeField] float hoverHeight = 0.45f;
        [Tooltip("Wall bounces before exploding (0 = explode on first wall).")]
        [SerializeField] int wallBounces;
        [Tooltip("Explode on any movable physics prop it touches (dynamic rigidbodies such as crates), whatever the slope " +
                 "of the touched face. Walls and steep terrain always stop it.")]
        [SerializeField] bool explodeOnProps = true;
        [Tooltip("Steer along the track's racing line while no target is acquired, in the direction it was launched " +
                 "(against the track's direction when fired backwards or from a kart facing the wrong way).")]
        public bool followTrackPath;
        [Tooltip("With followTrackPath: aim at the racing line this far (m) ahead of the projectile.")]
        [SerializeField] float pathLookAhead = 8f;

        [Header("Homing")]
        public bool homing;
        public TargetSelection targetSelection = TargetSelection.NextAhead;
        [Tooltip("Seconds before the projectile starts seeking.")]
        [SerializeField] float homingArmDelay;
        [Tooltip("Start steering towards the target within this distance.")]
        public float activationDistance = 100f;
        [Tooltip("Within this distance the projectile locks on and switches to lockedSpeed.")]
        [SerializeField] float lockDistance = 20f;
        [SerializeField] float lockedSpeed = 50f;
        [SerializeField] float turnRateDegrees = 240f;
        [Tooltip("Distance (m) behind a respawning target (falling or respawn-protected) on the racing line where the " +
                 "projectile waits until the target can be hit. It does not hit anything and its lifetime does not run while " +
                 "it waits.")]
        [SerializeField] float respawnWaitDistance = 6f;

        [Header("Impact")]
        [Tooltip("Spawned wherever the projectile ends (kart hit, wall, lifetime).")]
        [SerializeField] GameObject impactVfx;
        [Tooltip("One-shot where it bounces off a wall and keeps flying (wallBounces).")]
        [SerializeField] AudioClip bounceSound;

        Vector3 direction;
        Kart target;
        bool locked;
        // Parked behind a respawning target (no motion this step).
        bool holding;
        int bounces;
        int pathSegment = -1;
        float pathDirection = 1f;

        public bool Flying => true;
        public Vector3 NetPosition => SimulatedPosition;
        public Vector3 NetVelocity => holding ? Vector3.zero : direction * (locked ? lockedSpeed : speed);

        public void Correct(Vector3 position, Vector3 velocity)
        {
            BeginCorrection(position);
            velocity.y = 0f;
            if (velocity.sqrMagnitude > MinCorrectSpeedSqr) direction = velocity.normalized;
        }

        protected internal override void OnNetHit(Kart victim) => Despawn(impactVfx, SimulatedPosition, hitSound);

        protected internal override float NetHitReach => hitRadius;

        protected override void OnLaunch()
        {
            var t = Owner.transform;
            var backwards = allowBackwards && Context.backwards;
            direction = backwards ? -t.forward : t.forward;
            direction.y = 0;
            direction.Normalize();
            transform.SetPositionAndRotation(t.position + direction * spawnDistance + Vector3.up * hoverHeight,
                Quaternion.LookRotation(direction, Vector3.up));
            if (homing && !backwards) target = Context.targetGiven ? Context.target : PowerupTargeting.First(Owner, targetSelection, includeGhosts: true);
            if (followTrackPath && TrackRuntime.Current != null) pathDirection = PathDirectionAlong(direction);
            ChosenTarget = target;
            // Original world speeds (not scaled with the karts), plus the owner's speed so its own shot never falls behind.
            if (!ignoreOwnerSpeed) speed = Mathf.Max(speed, OwnerSpeedAlong(direction) + OwnerSpeedMargin);
            PlayLaunch();
            InterpolatePose();
        }

        protected override void Step(float dt)
        {
            if (TargetRespawning)
            {
                ApplyCorrection();
                WaitForTarget(dt);
                return;
            }
            holding = false;
            if (TickLifetime(dt, impactVfx)) return;
            ApplyCorrection();
            Steer(dt);
            Decelerate(dt);
            var currentSpeed = locked ? lockedSpeed : speed;
            if (TryHitWall((direction * (currentSpeed * dt)).magnitude)) return;

            var from = transform.position;
            var pos = from + direction * (currentSpeed * dt);
            if (followGround) pos.y = HoverAbove(pos, dt);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(direction, Vector3.up));
            CheckHits(from, pos);
        }

        // The homing target is falling back or respawn-protected: a hit now would be wasted on it.
        bool TargetRespawning => homing && target != null && target.isActiveAndEnabled && !target.Eliminated
                                 && (target.Effects.HasRespawnPending || target.Effects.RespawnProtected);

        // Holds still while the target falls back; once it is on the track again the projectile moves along the racing
        // line to respawnWaitDistance behind it and stays there until the protection ends.
        void WaitForTarget(float dt)
        {
            holding = true;
            if (target.Effects.HasRespawnPending || !TrackRuntime.TryGetMainPath(out var path)) return;
            var myD = path.Project(transform.position, ref pathSegment, out _);
            var delta = path.Delta(myD, target.Progress.PathDistance - respawnWaitDistance);
            if (Mathf.Abs(delta) < WaitArriveDistance) return;
            var aim = path.GetPoint(myD + Mathf.Sign(delta) * Mathf.Min(Mathf.Abs(delta), pathLookAhead));
            var to = aim - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < MinTravelSqr) return;
            holding = false;
            direction = to.normalized;
            var pos = transform.position + direction * Mathf.Min(speed * dt, to.magnitude);
            if (followGround) pos.y = HoverAbove(pos, dt);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(direction, Vector3.up));
        }

        bool TryHitWall(float stepLength)
        {
            if (!Physics.SphereCast(transform.position, SweepRadius, direction, out var wall, stepLength + SweepSkin, worldMask, QueryTriggerInteraction.Ignore)
                || Kart.FromCollider(wall.collider) != null
                || !(Vector3.Angle(wall.normal, Vector3.up) > MaxDrivableSlope || (explodeOnProps && wall.rigidbody != null && !wall.rigidbody.isKinematic)))
                return false;
            if (bounces >= wallBounces)
            {
                Despawn(impactVfx, transform.position, terrainSound);
                return true;
            }
            bounces++;
            direction = Vector3.Reflect(direction, wall.normal);
            direction.y = 0;
            direction.Normalize();
            PlaySfx(bounceSound, wall.point);
            return false;
        }

        float HoverAbove(Vector3 pos, float dt) =>
            GroundProbe.Down(pos, GroundProbeUp, GroundProbeDown, worldMask, out var ground)
                ? Mathf.Lerp(pos.y, ground.point.y + hoverHeight, Smoothing.Factor(HoverSharpness, dt))
                : pos.y;

        void Steer(float dt)
        {
            if (!TryGetAim(out var aim)) return;
            var desired = aim - transform.position;
            desired.y = 0;
            if (desired.sqrMagnitude < 0.01f) return;
            direction = Vector3.RotateTowards(direction, desired.normalized, turnRateDegrees * Mathf.Deg2Rad * dt, 0f);
        }

        bool TryGetAim(out Vector3 aim)
        {
            aim = default;
            if (homing && Age >= homingArmDelay && target != null && target.isActiveAndEnabled)
            {
                var dist = (target.transform.position - transform.position).magnitude;
                if (dist < lockDistance) locked = true;
                if (dist < activationDistance || locked)
                {
                    aim = target.transform.position;
                    return true;
                }
            }
            if (!followTrackPath || TrackRuntime.Current == null) return false;
            var path = TrackRuntime.Current.MainPath;
            var d = path.Project(transform.position, ref pathSegment, out _);
            aim = path.GetPoint(d + pathDirection * pathLookAhead);
            return true;
        }

        // +1 to follow the racing line in the track's direction, -1 against it: the way the shot leaves.
        float PathDirectionAlong(Vector3 launch)
        {
            var path = TrackRuntime.Current.MainPath;
            var d = path.Project(transform.position, ref pathSegment, out _);
            return Vector3.Dot(path.GetTangent(d, PathTangentSmoothing), launch) < 0f ? -1f : 1f;
        }

        // Along the path travelled this step, so fast shots cannot skip past a kart between steps.
        void CheckHits(Vector3 from, Vector3 to)
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                // Homing rockets contact ghosts/diggers logically even while their colliders pass through things.
                // HitKart keeps their protection intact; the rocket is still consumed, just as against a shield.
                if ((k == Owner && Age < ownerImmunity) || (!homing && k.Effects.Ghost)) continue;
                if (GeometryUtil.DistanceSqToSegment(k.transform.position, from, to, SegmentEpsilon, 1f) >= hitRadius * hitRadius) continue;
                HitKart(k, hitEffect);
                Despawn(impactVfx, transform.position, hitSound);
                return;
            }
        }
    }
}
