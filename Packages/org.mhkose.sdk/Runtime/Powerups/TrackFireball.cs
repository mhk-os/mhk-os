using MHK.AI;
using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Floating leader hunter. Drives the measured racing lines, including their fork/merge run-ons, rather than
    /// steering directly through scenery at a kart. Race order is reconsidered every physics step, including the owner.
    /// </summary>
    public class TrackFireball : ProjectileBehaviour, INetProjectile
    {
        const float MaxSubstepDistance = 0.5f;
        const float Skin = 0.02f;
        const float JunctionMargin = 0.5f;
        const float ForkReach = AIRacingLine.RouteRunOn + 5f;
        const float TargetLaneReach = 12f;
        const float TargetBodyHeight = 0.7f;

        [Header("Track flight")]
        [SerializeField] float hoverHeight = 1.2f;
        [SerializeField] float collisionRadius = 0.2f;
        [SerializeField] float ownerImmunity = 2f;
        [SerializeField] float lookAhead = 3f;
        [SerializeField] float turnRateDegrees = 900f;
        [Tooltip("A centered visual pivot. Only this child spins; navigation rotates the root around world up.")]
        [SerializeField] Transform visual;
        [SerializeField] float spinDegreesPerSecond = 720f;

        [Header("Leader pursuit")]
        [Tooltip("Slow from full flight speed to the leader's normal cruising pace inside this track distance.")]
        [SerializeField, Min(0.1f)] float leaderSlowDistance = 12.75f;
        [Tooltip("Close pursuit uses this share of the leader's unboosted top speed, not its instantaneous speed: braking lets the fireball catch up.")]
        [SerializeField, Range(0.1f, 1f)] float leaderSpeedShare = 0.65f;

        readonly RaycastHit[] sweepHits = new RaycastHit[16];
        readonly Collider[] overlaps = new Collider[16];
        TrackRuntime track;
        TrackPath main;
        int mainHint = -1, routeHint = -1, routeIndex = -1;
        float mainDistance, routeDistance, raceDistance;
        bool enteredRoute, holding;
        Vector3 direction, correction, velocity;
        Quaternion visualRotation;
        float spin;

        public bool Flying => isActiveAndEnabled;
        public Vector3 NetPosition => SimulatedPosition;
        public Vector3 NetVelocity => holding ? Vector3.zero : velocity;
        /// <summary>Committed alternative route (including its approach), otherwise the main path.</summary>
        public TrackPath FollowedPath => routeIndex >= 0 ? track.Routes[routeIndex] : main;
        /// <summary>Distance along FollowedPath; negative while approaching an alternative route's fork.</summary>
        public float FollowedDistance => routeIndex >= 0 ? routeDistance : mainDistance;

        public void Correct(Vector3 position, Vector3 newVelocity)
        {
            // Base ApplyCorrection moves without a collision query. Keep this correction swept too, even while waiting.
            correction = position - SimulatedPosition;
            newVelocity.y = 0f;
            if (newVelocity.sqrMagnitude > 0.01f) direction = newVelocity.normalized;
        }

        protected internal override float NetHitReach => hitRadius + TargetBodyHeight;
        // Race order may change while a confirmed hit is in flight; it still spends this shot.
        protected internal override void OnNetHit(Kart victim) => Despawn(null, SimulatedPosition, hitSound);

        protected override void OnLaunch()
        {
            track = TrackRuntime.Current;
            main = track != null && track.HasMainPath ? track.MainPath : null;
            ChosenTarget = PowerupTargeting.RaceLeader();
            transform.position = Owner.transform.position + Vector3.up * hoverHeight;
            if (main != null)
            {
                AdoptLaunchRoute();
                raceDistance = Owner.Progress.RaceDistance + main.Delta(Owner.Progress.PathDistance, mainDistance);
                direction = FollowedPath.GetFlatTangent(Mathf.Max(0f, FollowedDistance));
            }
            else direction = Vector3.ProjectOnPlane(Owner.transform.forward, Vector3.up).normalized;
            if (direction.sqrMagnitude < 0.01f) direction = Vector3.forward;
            var liftFrom = Owner.transform.position + Vector3.up * (collisionRadius + Skin);
            SweepMove(liftFrom, transform.position, out var lifted, out _);
            var from = lifted;
            var to = Hover(from + direction * spawnDistance);
            SweepMove(from, to, out to, out _);
            transform.SetPositionAndRotation(to, Quaternion.LookRotation(direction, Vector3.up));
            if (visual != null) visualRotation = visual.localRotation;
            PlayLaunch();
            InterpolatePose();
        }

        protected override void Step(float dt)
        {
            if (TickLifetime(dt, null)) return;
            ChosenTarget = PowerupTargeting.RaceLeader();
            SpinVisual(dt);
            holding = false;
            velocity = Vector3.zero;
            if (correction.sqrMagnitude > 0.000001f)
            {
                var from = transform.position;
                var move = correction * 0.2f;
                var clear = SweepMove(from, from + move, out var corrected, out _);
                transform.position = corrected;
                correction = clear ? correction - move : Vector3.zero;
            }
            var target = ChosenTarget;
            var targetReady = target != null && !target.Effects.HasRespawnPending && !target.Effects.RespawnProtected;
            if (main == null)
            {
                holding = true;
                return;
            }
            var steps = Mathf.Max(1, Mathf.CeilToInt(speed * dt / MaxSubstepDistance));
            var subDt = dt / steps;
            for (var i = 0; i < steps; i++)
            {
                UpdateNavigation();
                if (routeIndex >= 0 && enteredRoute && track.IsRouteCut(routeIndex, routeDistance))
                {
                    holding = true;
                    break;
                }
                var gap = targetReady ? target.Progress.RaceDistance - raceDistance : float.PositiveInfinity;
                if (gap < 0f)
                {
                    // An active leader behind us must drive up to the fireball. Respawning leaders never park it.
                    holding = true;
                    TryHitLeader(target, transform.position, transform.position);
                    return;
                }
                var aim = AimPoint(targetReady ? target : null);
                var desired = Vector3.ProjectOnPlane(aim - transform.position, Vector3.up);
                if (desired.sqrMagnitude < 0.0001f) { holding = true; break; }
                var turn = Vector3.Angle(direction, desired);
                direction = Vector3.RotateTowards(direction, desired.normalized, turnRateDegrees * Mathf.Deg2Rad * subDt, 0f);
                // Brake into a tight turn instead of overshooting the road at full straight-line speed.
                var travelSpeed = PursuitSpeed(targetReady ? target : null, gap) * Mathf.Clamp01(1f - turn / 100f);
                var from = transform.position;
                var to = Hover(from + direction * (travelSpeed * subDt));
                var clear = SweepMove(from, to, out var reached, out var wallNormal);
                transform.SetPositionAndRotation(reached, Quaternion.LookRotation(direction, Vector3.up));
                velocity = (reached - from) / subDt;
                if (targetReady && TryHitLeader(target, from, reached)) return;
                if (clear) continue;
                // Sweep the wall-slide separately: a chord from the original point would cut solid corners.
                var remaining = to - reached;
                var slide = Vector3.ProjectOnPlane(remaining, wallNormal);
                slide.y = 0f;
                if (slide.sqrMagnitude < 0.0001f && remaining.sqrMagnitude > 0.0001f && wallNormal.sqrMagnitude > 0.01f)
                {
                    var tangent = Vector3.Cross(Vector3.up, wallNormal).normalized;
                    if (Vector3.Dot(tangent, FollowedPath.GetFlatTangent(Mathf.Max(0f, FollowedDistance))) < 0f) tangent = -tangent;
                    slide = tangent * remaining.magnitude;
                }
                SweepMove(reached, Hover(reached + slide), out var slid, out _);
                if (slide.sqrMagnitude > 0.0001f) direction = slide.normalized;
                transform.SetPositionAndRotation(slid, Quaternion.LookRotation(direction, Vector3.up));
                velocity = (slid - from) / subDt;
                if (targetReady && TryHitLeader(target, reached, slid)) return;
                holding = (slid - from).sqrMagnitude < 0.000001f;
            }
        }

        float PursuitSpeed(Kart target, float gap)
        {
            if (target == null || gap >= leaderSlowDistance) return speed;
            var closeSpeed = Mathf.Min(speed, target.NormalTopSpeed * leaderSpeedShare);
            // A close-speed plateau keeps a cruising leader outside contact range instead of guaranteeing a hit.
            var approach = Mathf.InverseLerp(leaderSlowDistance * 0.5f, leaderSlowDistance, gap);
            return Mathf.Lerp(closeSpeed, speed, approach);
        }

        void SpinVisual(float dt)
        {
            if (visual == null) return;
            spin = Mathf.Repeat(spin + spinDegreesPerSecond * dt, 360f);
            // Explicit pivot-local X: never world-space rotation and never the navigation root's yaw.
            visual.localRotation = visualRotation * Quaternion.AngleAxis(spin, Vector3.right);
        }

        void AdoptLaunchRoute()
        {
            var point = transform.position - Vector3.up * hoverHeight;
            mainDistance = main.Project(point, ref mainHint, out _);
            var best = ProjectionScore(main, mainDistance, point);
            for (var i = 0; i < track.Routes.Count; i++)
            {
                var route = track.Routes[i];
                var hint = -1;
                var d = route.Project(point, ref hint, out _);
                var score = ProjectionScore(route, d, point);
                // Prefer the launcher's actual branch at an ambiguous fork, but project the launch pose itself:
                // remote owners' progress may refer to their older, interpolated pose.
                if (Owner.Progress.CurrentBranch == route && score <= best + JunctionMargin) score -= JunctionMargin;
                if (score >= best) continue;
                best = score;
                routeIndex = i;
                routeDistance = d;
                routeHint = hint;
                enteredRoute = true;
            }
            if (routeIndex >= 0)
            {
                mainDistance = track.MapToMain(routeIndex, routeDistance);
                mainHint = main.SegmentAt(mainDistance);
            }
        }

        static float ProjectionScore(TrackPath path, float distance, Vector3 point)
        {
            var delta = point - path.GetPoint(distance);
            delta.y *= TrackPath.HeightWeight;
            return delta.magnitude;
        }

        void UpdateNavigation()
        {
            var previous = mainDistance;
            FollowRoute();
            // Unwrapped progress distinguishes being ahead from a leader more than half a lap in front.
            raceDistance += main.Delta(previous, mainDistance);
        }

        void FollowRoute()
        {
            var point = transform.position - Vector3.up * hoverHeight;
            mainDistance = main.Track(point, ref mainHint, routeIndex < 0 ? mainDistance : float.NaN,
                MaxSubstepDistance * 2f, out _, out _);
            if (routeIndex >= 0)
            {
                var route = track.Routes[routeIndex];
                if (!enteredRoute)
                {
                    if (track.IsRouteCut(routeIndex)) { DropRoute(); return; }
                    var toFork = main.Delta(mainDistance, track.BranchJoinStart(routeIndex));
                    var entry = route.Project(point, ref routeHint, out _);
                    // The solved racing line can cut into the branch before the raw main-line fork plane. Keeping
                    // its progress negative there aims back at the same corner forever (Luna's shortcut entrance).
                    if (toFork > 0f && (entry <= JunctionMargin ||
                        ProjectionScore(route, entry, point) > ProjectionScore(main, mainDistance, point) + JunctionMargin))
                    {
                        routeDistance = -toFork;
                        return;
                    }
                    enteredRoute = true;
                    routeDistance = entry;
                }
                else routeDistance = route.Track(point, ref routeHint, routeDistance,
                    MaxSubstepDistance * 2f, out _, out _);
                // The solved exit has the same early handover: the shot may already be on the main road while its
                // raw branch projection is still short of the endpoint. Preserve that real main-line projection.
                var onMain = routeDistance >= route.Length - JunctionMargin;
                if (!onMain && routeDistance >= route.Length - AIRacingLine.RouteRunOn)
                {
                    var pastMerge = main.Delta(track.BranchJoinEnd(routeIndex), mainDistance);
                    onMain = pastMerge >= JunctionMargin && pastMerge <= AIRacingLine.RouteRunOn
                        && ProjectionScore(main, mainDistance, point) <= ProjectionScore(route, routeDistance, point) + JunctionMargin;
                }
                if (!onMain)
                {
                    mainDistance = track.MapToMain(routeIndex, routeDistance);
                    mainHint = main.SegmentAt(mainDistance);
                    return;
                }
                DropRoute();
            }
            ChooseFork();
        }

        void DropRoute()
        {
            routeIndex = -1;
            routeHint = -1;
            enteredRoute = false;
        }

        void ChooseFork()
        {
            if (ChosenTarget == null) return;
            var targetRoute = ChosenTarget.Progress.CurrentBranch;
            var targetAhead = main.WrapDistance(ChosenTarget.Progress.PathDistance - mainDistance);
            var bestSaving = 0f;
            var bestFork = float.PositiveInfinity;
            for (var i = 0; i < track.Routes.Count; i++)
            {
                if (track.IsRouteCut(i)) continue;
                var fork = main.WrapDistance(track.BranchJoinStart(i) - mainDistance);
                if (fork < JunctionMargin || fork > ForkReach) continue;
                var route = track.Routes[i];
                var huntsRoute = targetRoute == route;
                var span = track.BranchSpan(i);
                var saving = span - route.Length;
                // Do not bypass the target itself. A leader on a branch is followed through its entrance, never
                // approached across the intervening terrain. Other routes must shorten the remaining chase.
                if (!huntsRoute && (saving <= JunctionMargin || fork + span > targetAhead)) continue;
                if (huntsRoute)
                {
                    // Enter the leader's branch even if it is longer, provided its fork is still in front of us.
                    routeIndex = i;
                    routeDistance = -fork;
                    return;
                }
                if (saving < bestSaving || (Mathf.Approximately(saving, bestSaving) && fork >= bestFork)) continue;
                bestSaving = saving;
                bestFork = fork;
                routeIndex = i;
                routeDistance = -fork;
            }
        }

        Vector3 AimPoint(Kart target)
        {
            var path = FollowedPath;
            var d = FollowedDistance;
            var samePath = target != null && target.Progress.CurrentBranch == (routeIndex >= 0 ? path : null);
            var td = samePath ? (routeIndex >= 0 ? target.Progress.BranchDistance : target.Progress.PathDistance) : 0f;
            var gap = samePath ? path.Delta(d, td) : float.PositiveInfinity;
            // Finish the approach at the kart, not a look-ahead point beyond it. Skip line solving for direct contact.
            if (gap >= 0f && gap <= lookAhead) return target.transform.position;
            var margin = GameRoot.Instance != null && GameRoot.Instance.Config != null
                ? GameRoot.Instance.Config.aiRacingLineMargin : collisionRadius;
            var road = AIRacingLine.Of(track, path, margin);
            var ahead = road.LookAheadDistance(transform.position, d, lookAhead, 0);
            var lane = road.Lane(ahead, 0);
            if (gap >= 0f && gap < TargetLaneReach)
            {
                var targetLane = Mathf.Clamp(road.RoadLaneOf(target.transform.position, td), 0.05f, 0.95f);
                lane = Mathf.Lerp(lane, targetLane, 1f - gap / TargetLaneReach);
            }
            return road.PointAt(ahead, lane);
        }

        Vector3 Hover(Vector3 position)
        {
            if (GroundProbe.Down(position, 2f, 6f, ObstacleMask, out var ground))
                position.y = ground.point.y + hoverHeight;
            return position;
        }

        int ObstacleMask => worldMask & ~GameLayers.KartAndItemMask;

        bool SweepMove(Vector3 from, Vector3 requested, out Vector3 reached, out Vector3 normal)
        {
            reached = from;
            normal = Vector3.zero;
            var overlapCount = Physics.OverlapSphereNonAlloc(from, collisionRadius - Skin, overlaps,
                ObstacleMask, QueryTriggerInteraction.Ignore);
            if (overlapCount == overlaps.Length) return false;
            for (var i = 0; i < overlapCount; i++)
                if (Kart.FromCollider(overlaps[i]) == null && !overlaps[i].transform.IsChildOf(transform)) return false;
            var delta = requested - from;
            var length = delta.magnitude;
            if (length < 0.00001f) return true;
            var count = Physics.SphereCastNonAlloc(from, collisionRadius, delta / length, sweepHits,
                length + Skin, ObstacleMask, QueryTriggerInteraction.Ignore);
            var allowed = length;
            // A saturated query is not permission to tunnel through an unreported wall.
            if (count == sweepHits.Length) return false;
            for (var i = 0; i < count; i++)
            {
                var hit = sweepHits[i];
                if (Kart.FromCollider(hit.collider) != null || hit.collider.transform.IsChildOf(transform)) continue;
                var limit = Mathf.Max(0f, hit.distance - Skin);
                if (limit >= allowed) continue;
                allowed = limit;
                normal = hit.normal;
            }
            reached = from + delta * (allowed / length);
            return allowed >= length;
        }

        bool TryHitLeader(Kart target, Vector3 from, Vector3 to)
        {
            if (target == Owner && Age < ownerImmunity) return false;
            var centre = target.transform.position + Vector3.up * TargetBodyHeight;
            if (GeometryUtil.DistanceSqToSegment(centre, from, to, 1e-8f, 1f) > hitRadius * hitRadius) return false;
            // The contact radius must not boil a racer on the other side of a thin solid wall.
            if (Physics.Linecast(to, centre, ObstacleMask, QueryTriggerInteraction.Ignore)) return false;
            HitKart(target, hitEffect);
            Despawn(null, to, hitSound);
            return true;
        }
    }
}
