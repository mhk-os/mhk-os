using MHK.Audio;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Viper's phase dash. The user's kart moves along the racing line it is on for <see cref="duration"/> seconds
    /// at a constant speed, turned to the line's direction and kept at its side offset well inside the road. On an
    /// alternative route it follows the route to its join and goes on along the main line, and on the main line it never
    /// takes a shortcut. Afterwards the driver has the kart back and it rolls on along the line. The dash effect makes
    /// the kart a ghost or zaps karts it passes as its data says. Only a kart simulated here is moved. Elsewhere the
    /// use only shows the effect and ends after the duration.
    /// </summary>
    [DefaultExecutionOrder(100)] // after Kart and KartEffects, so it has the last word on the body's velocity before the physics step
    public class RouteDashPowerup : PowerupBehaviour
    {
        // Metres of line over which the dash direction and the side vector are smoothed.
        const float TangentSmoothing = 3f;
        // Ground is searched from this high above the line point down to this far below it (m).
        const float ProbeAbove = 2.5f;
        const float ProbeBelow = 4f;
        // Surfaces in the probed column (roofs, bridges, the road) and the restart just under each one.
        const int ProbeLayers = 3;
        const float ProbeStepBelow = 0.05f;
        const float MinGroundNormalY = 0.6f;
        // The kart rides 1 cm above its rest height, as on the start grid (KartPhysics.PlaceOnGround).
        const float RideLift = 0.01f;
        // Smoothing rate × alignTime. After alignTime about 5 % of a turn or side move is left.
        const float AlignSettle = 3f;
        // Along-line allowance (m) when locating the kart on its line at the launch.
        const float LocateAllowance = 3f;
        // Line metres per metre driven at the kept side offset (curves) stay within these.
        const float MinStretch = 0.5f;
        const float MaxStretch = 2f;
        const float MinMove = 1e-4f;
        const float MinTime = 0.01f;

        [Header("Dash")]
        [Tooltip("Seconds the dash moves the kart along the racing line.")]
        [SerializeField, Min(0f)] float duration = 1f;
        [Tooltip("Speed along the line in world m/s. The HUD shows m/s × 3.6 as km/h, so 41.7 reads 150 km/h.")]
        [SerializeField, Min(0f)] float speed = 41.7f;
        [Tooltip("Share of the dash speed the kart keeps when the driver has it back.")]
        [SerializeField, Range(0f, 1f)] float exitSpeedShare = 0.75f;
        [Tooltip("Seconds in which the kart turns to the line's direction and settles on the ground and its side offset.")]
        [SerializeField, Min(0f)] float alignTime = 0.1f;
        [Tooltip("Distance (m) the kart keeps from the road edge. Its side offset from the line is clamped to the road width less this on either side.")]
        [SerializeField, Min(0f)] float edgeMargin = 1.5f;
        [Tooltip("Effect on the user for the dash (ghost, an aura that zaps karts it passes). Removed when the dash ends. Empty = none.")]
        [SerializeField] StatusEffectDefinition dashEffect;

        [Header("Sounds")]
        [Tooltip("One-shot at the kart when the dash starts.")]
        [SerializeField] AudioClip startSound;
        [Tooltip("Loop that follows the kart during the dash.")]
        [SerializeField] LoopSound dashLoop = new();

        float elapsed;
        bool carrying, ended, wasDetecting;
        TrackRuntime track;
        TrackPath line;
        // Index of the alternative route the dash follows, -1 on the main line.
        int routeIndex = -1;
        float lineDistance;
        // 1 along the line's nodes, -1 against them (a battle kart facing the other way).
        int direction = 1;
        // Side offset (m, + = right of the line) the kart had and the one it is moved to now.
        float keptLateral, lateral;
        float lift, stretch = 1f, restHeight;
        Vector3 groundNormal = Vector3.up;
        Vector3 lastRail;
        bool rejoined;
        Effects.EffectLoopSound loop;

        // Moves its owner through the physics step, so a remote use cannot be caught up step by step.
        protected internal override bool FastForwards => false;

        protected override void OnLaunch()
        {
            if (Owner == null) { Remove(); return; }
            Owner.Respawned += OnOwnerRespawned;
            if (dashEffect != null) Owner.Effects.Apply(dashEffect, Owner, duration, Usage);
            PlaySfx(startSound, Owner.transform);
            loop = Effects.EffectLoopSound.Play(Owner.gameObject, dashLoop);
            if (MovesHere(Owner) && !Owner.Physics.Suspended && TryTakeLine()) TakeOver();
        }

        void OnDestroy()
        {
            if (Owner != null) Owner.Respawned -= OnOwnerRespawned;
            Release(false);
        }

        // A respawn has put the kart back on the track already, so the dash lets go of it where it is.
        void OnOwnerRespawned(Kart k)
        {
            if (ended) return;
            Release(false);
            Remove();
        }

        protected override void Step(float dt)
        {
            if (Owner == null) { Remove(); return; }
            if (carrying && (Owner.Eliminated || Owner.Effects.Frozen)) { End(false); return; }
            if (carrying && RaceOver()) { End(true); return; }
            if (elapsed >= duration) { End(true); return; }
            elapsed += dt;
            if (carrying) Carry(dt);
        }

        bool RaceOver()
        {
            var race = RaceManager.Current;
            if (race != null && (race.Phase == RacePhase.Finished || race.Phase == RacePhase.Results)) return true;
            return Owner.Progress != null && Owner.Progress.Finished;
        }

        // The line the racer is on (an alternative route or the main line) and where on it the kart is.
        bool TryTakeLine()
        {
            track = TrackRuntime.Current;
            var progress = Owner.Progress;
            if (track == null || !track.HasMainPath || progress == null) return false;
            var branch = progress.CurrentBranch;
            routeIndex = track.RouteIndex(branch);
            line = routeIndex >= 0 ? branch : track.MainPath;
            var from = routeIndex >= 0 ? progress.BranchDistance : progress.PathDistance;
            var hint = line.SegmentAt(from);
            var position = Owner.Physics.Body.position;
            lineDistance = line.Track(position, ref hint, from, LocateAllowance, out _, out _);
            direction = DashDirection();
            keptLateral = lateral = LateralOf(position);
            return true;
        }

        // Races always run along the line's nodes (a reverse variant turns the nodes round). In a battle the kart dashes
        // the way it faces.
        int DashDirection()
        {
            var race = RaceManager.Current;
            if (race == null || !race.IsBattle) return 1;
            return Vector3.Dot(Owner.transform.forward, line.GetTangent(lineDistance, TangentSmoothing)) < 0f ? -1 : 1;
        }

        float LateralOf(Vector3 position) =>
            Vector3.Dot(position - line.GetPoint(lineDistance), line.GetRight(lineDistance, TangentSmoothing));

        void TakeOver()
        {
            var physics = Owner.Physics;
            var body = physics.Body;
            restHeight = physics.RestHeight;
            groundNormal = body.rotation * Vector3.up;
            lastRail = Rail();
            if (!FindGround(lastRail, out var ground, out _)) ground = lastRail;
            // The kart may start in the air or a little off the ground. That height fades out over the align time.
            lift = body.position.y - (ground + groundNormal * (restHeight + RideLift)).y;
            wasDetecting = body.detectCollisions;
            physics.Suspended = true;
            body.detectCollisions = false;
            carrying = true;
        }

        Vector3 Rail() => line.GetPoint(lineDistance) + line.GetRight(lineDistance, TangentSmoothing) * lateral;

        void Carry(float dt)
        {
            var body = Owner.Physics.Body;
            var settle = Core.Smoothing.Factor(AlignSettle / Mathf.Max(MinTime, alignTime), dt);
            Advance(speed * dt * stretch);
            var maxLateral = Mathf.Max(0f, 0.5f * line.GetWidth(lineDistance) - edgeMargin);
            lateral = Mathf.Lerp(lateral, Mathf.Clamp(keptLateral, -maxLateral, maxLateral), settle);
            var rail = Rail();
            MeasureStretch(rail, dt);

            if (!FindGround(rail, out var ground, out var normal))
            {
                ground = rail;
                normal = Vector3.up;
            }
            groundNormal = Vector3.Slerp(groundNormal, normal, settle);
            lift *= 1f - settle;
            var target = ground + groundNormal * (restHeight + RideLift) + Vector3.up * lift;
            body.linearVelocity = (target - body.position) / dt;
            body.angularVelocity = Vector3.zero;
            var forward = Vector3.ProjectOnPlane(line.GetTangent(lineDistance, TangentSmoothing) * direction, groundNormal);
            if (forward.sqrMagnitude > MinMove)
                body.MoveRotation(Quaternion.Slerp(body.rotation, Quaternion.LookRotation(forward, groundNormal), settle));
        }

        // Moves along the line. At the end of an alternative route the kart goes on along the main line from the join.
        void Advance(float metres)
        {
            lineDistance += direction * metres;
            if (routeIndex < 0)
            {
                lineDistance = line.WrapDistance(lineDistance);
                return;
            }
            var beyond = direction > 0 ? lineDistance - line.Length : -lineDistance;
            if (beyond < 0f) return;
            var join = direction > 0 ? track.BranchJoinEnd(routeIndex) : track.BranchJoinStart(routeIndex);
            line = track.MainPath;
            routeIndex = -1;
            lineDistance = line.WrapDistance(join + direction * beyond);
            // The route's end lies across the main line from the join, so only the side offset changes there.
            keptLateral = lateral = LateralOf(Owner.Physics.Body.position);
            rejoined = true;
        }

        // Off the line's centre a curve is longer or shorter than the line. The line metres per step follow what the
        // last step really moved, so the kart keeps its speed.
        void MeasureStretch(Vector3 rail, float dt)
        {
            var moved = (rail - lastRail).magnitude;
            if (!rejoined && moved > MinMove) stretch = Mathf.Clamp(stretch * speed * dt / moved, MinStretch, MaxStretch);
            rejoined = false;
            lastRail = rail;
        }

        // The drivable surface in a column through the line point closest to its height, so a roof or a bridge never
        // wins against the road the line is on. None over gaps and drops, where the kart follows the line.
        bool FindGround(Vector3 point, out Vector3 ground, out Vector3 normal)
        {
            ground = point;
            normal = Vector3.up;
            var mask = track.ObstacleMask;
            var origin = point + Vector3.up * ProbeAbove;
            var bottom = point.y - ProbeBelow;
            var best = float.MaxValue;
            for (var k = 0; k < ProbeLayers && origin.y > bottom; k++)
            {
                if (!GroundProbe.Down(origin, origin.y - bottom, mask, out var hit)) break;
                origin.y = hit.point.y - ProbeStepBelow;
                if (hit.normal.y < MinGroundNormalY) continue;
                var cost = Mathf.Abs(hit.point.y - point.y);
                if (!(cost < best)) continue;
                best = cost;
                ground = hit.point;
                normal = hit.normal;
            }
            return best < float.MaxValue;
        }

        void End(bool driveOn)
        {
            Release(driveOn);
            Remove();
        }

        // Ends the dash effect and the loop, and gives the kart back with the physics as the dash found them. Driving on,
        // it rolls along the line at the exit speed instead of stopping dead.
        void Release(bool driveOn)
        {
            if (ended) return;
            ended = true;
            Effects.EffectLoopSound.Stop(ref loop);
            if (Owner == null) return;
            if (dashEffect != null) Owner.Effects.Remove(dashEffect);
            if (!carrying) return;
            carrying = false;
            var physics = Owner.Physics;
            var body = physics.Body;
            physics.Suspended = false;
            body.detectCollisions = wasDetecting;
            if (!driveOn) return;
            var along = Vector3.ProjectOnPlane(line.GetTangent(lineDistance, TangentSmoothing) * direction, groundNormal);
            var heading = along.sqrMagnitude > MinMove ? along.normalized : body.rotation * Vector3.forward;
            body.linearVelocity = heading * (speed * exitSpeedShare);
            body.angularVelocity = Vector3.zero;
        }
    }
}
