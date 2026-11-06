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
        const float InterceptConeDot = -0.70710678f; // Original seeking-first cone: +/-135 degrees.
        const float GroundProbeAbove = 2f;
        // Deep enough for the drops between Castle's levels; gaps are kept level by MaxGapRun anyway.
        const float GroundProbeBelow = 30f;
        // Ground this far below the shot's hover level is a gap the karts jump, a hole in the road or a drop.
        const float GapDepth = 1f;
        const float MaxGapRun = 12f;
        const float AvoidStep = 0.6f;
        const int AvoidSteps = 4;
        const float WalkableNormalY = 0.5f;

        [Header("Track flight")]
        [SerializeField] float hoverHeight = 1.2f;
        [SerializeField] float collisionRadius = 0.2f;
        [SerializeField] float lookAhead = 3f;
        [SerializeField] float turnRateDegrees = 900f;
        [Tooltip("Release keeps the kart's forward-facing angle briefly before normal road steering takes over.")]
        [SerializeField, Min(0f)] float launchSteeringTime = 0.3f;
        [SerializeField, Min(0f)] float launchTurnRateDegrees = 180f;
        [Tooltip("A real wall impact hesitates, then makes a short swept escape before resuming pursuit.")]
        [SerializeField, Min(0f)] float wallPauseTime = 0.14f;
        [SerializeField, Min(0f)] float wallRecoveryTime = 0.35f;
        [Tooltip("A centered visual pivot. Only this child spins; navigation rotates the root around world up.")]
        [SerializeField] Transform visual;
        [SerializeField] float spinDegreesPerSecond = 720f;

        [Header("Leader pursuit")]
        [Tooltip("Slow from full flight speed to the leader's normal cruising pace inside this distance from its rear chassis.")]
        [SerializeField, Min(0.1f)] float leaderSlowDistance = 4f;
        [Tooltip("Close pursuit uses this share of the leader's unboosted top speed, not its instantaneous speed: braking lets the fireball catch up.")]
        [SerializeField, Range(0.1f, 1f)] float leaderSpeedShare = 0.95f;
        [Tooltip("Drive forwards without targeting the launcher during these first seconds. Other leaders can be targeted immediately.")]
        [SerializeField, Min(0f)] float selfTargetDelay = 1f;
        [Tooltip("Side-to-side aim drift in metres during close pursuit. Fades out at contact and stays inside the drivable road.")]
        [SerializeField, Min(0f)] float pursuitDriftWidth = 0.65f;
        [SerializeField, Min(0.1f)] float pursuitDriftPeriod = 2.4f;
        [Tooltip("Directly steer into a nearby leader inside the original side/front interception cone, on the same route with clear sight.")]
        [SerializeField, Min(0.1f)] float leaderInterceptDistance = 10.625f;
        [SerializeField, Min(0f)] float leaderInterceptTurnRate = 2700f;

        [Header("Ahead waiting")]
        [SerializeField, Min(0f)] float waitingCreepSpeed = 3f;
        [SerializeField, Range(0.1f, 1f)] float waitingNearSpeedShare = 0.85f;
        [Tooltip("Inside this ahead-of-leader gap, maintain pressure speed without the far-ahead coast pauses.")]
        [SerializeField, Min(0.1f)] float waitingPressDistance = 12.75f;
        [SerializeField, Min(0.01f)] float waitingPulsePeriod = 0.5f;
        [SerializeField, Min(0.01f)] float waitingPulseDuration = 0.2f;

        [Header("End effect")]
        [SerializeField] GameObject endVfx;
        [SerializeField] GameObject impactVfx;

        readonly RaycastHit[] sweepHits = new RaycastHit[16];
        readonly Collider[] overlaps = new Collider[16];
        TrackRuntime track;
        TrackPath main;
        int mainHint = -1, routeHint = -1, routeIndex = -1;
        float mainDistance, routeDistance, raceDistance;
        bool enteredRoute, holding, ownerCleared, spent, angledRelease;
        Vector3 direction, flightCorrection, velocity, recoveryDirection;
        float wallPauseRemaining, wallRecoveryRemaining, gapRun;
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
            flightCorrection = position - SimulatedPosition;
            newVelocity.y = 0f;
            if (newVelocity.sqrMagnitude > 0.01f) direction = newVelocity.normalized;
        }

        protected internal override float NetHitReach
        {
            get
            {
                if (hitRadius <= 0f) return 0f;
                // A confirmed victim may no longer be the leader. Cover any active chassis, not just the current target.
                var reach = 0f;
                var karts = Kart.All;
                for (var i = 0; i < karts.Count; i++)
                {
                    var kart = karts[i];
                    var chassis = kart.Physics.Chassis;
                    if (chassis == null) continue;
                    var bounds = chassis.bounds;
                    reach = Mathf.Max(reach, (bounds.center - kart.transform.position).magnitude + bounds.extents.magnitude);
                }
                return hitRadius + reach;
            }
        }
        // Race order may change while a confirmed hit is in flight; it still spends this shot.
        protected internal override void OnNetHit(Kart victim) => FinishImpact(SimulatedPosition);

        protected override void OnLaunch()
        {
            track = TrackRuntime.Current;
            main = track != null && track.HasMainPath ? track.MainPath : null;
            ChosenTarget = LeaderTarget();
            direction = Vector3.ProjectOnPlane(Owner.transform.forward, Vector3.up).normalized;
            transform.position = Owner.transform.position + Vector3.up * hoverHeight;
            if (main != null)
            {
                AdoptLaunchRoute();
                raceDistance = Owner.Progress.RaceDistance + main.Delta(Owner.Progress.PathDistance, mainDistance);
                var course = FollowedPath.GetFlatTangent(Mathf.Max(0f, FollowedDistance));
                // Look-back is a camera/input choice, not a reversed shot. A backwards/wrong-way chassis must
                // still release forward-course; ordinary angled forward releases retain their real heading.
                if (Vector3.Dot(direction, course) <= 0.1f) direction = course;
                angledRelease = Vector3.Angle(direction, course) > 5f;
            }
            if (direction.sqrMagnitude < 0.01f) direction = Vector3.forward;
            var liftFrom = Owner.transform.position + Vector3.up * (collisionRadius + Skin);
            SweepMove(liftFrom, transform.position, out var lifted, out _);
            var from = lifted;
            var to = Hover(from + direction * spawnDistance, out _);
            var clear = FlyTo(from, to, out to, out var wallNormal);
            transform.SetPositionAndRotation(to, Quaternion.LookRotation(direction, Vector3.up));
            if (!clear && main != null) BeginWallRecovery(wallNormal);
            if (visual != null) visualRotation = visual.localRotation;
            PlayLaunch();
            InterpolatePose();
            // Spare the launcher only while leaving its body, never for a timed grace period after locking on.
            ownerCleared = !LeaderContact(Owner, transform.position, transform.position, out _);
        }

        protected override void Step(float dt)
        {
            if (spent) return;
            if (TickLifetime(dt, endVfx)) return;
            ChosenTarget = LeaderTarget();
            SpinVisual(dt);
            holding = false;
            velocity = Vector3.zero;
            var target = ChosenTarget;
            var targetReady = target != null && !target.Effects.HasRespawnPending && !target.Effects.RespawnProtected;
            if (flightCorrection.sqrMagnitude > 0.000001f)
            {
                var from = transform.position;
                var move = flightCorrection * 0.2f;
                var clear = SweepMove(from, from + move, out var corrected, out _);
                transform.position = corrected;
                flightCorrection = clear ? flightCorrection - move : Vector3.zero;
                if (targetReady && TryHitLeader(target, from, corrected)) return;
            }
            if (!ownerCleared && !LeaderContact(Owner, transform.position, transform.position, out _)) ownerCleared = true;
            // Contact is decisive even while stationary, waiting, or already exactly on the steering aim point.
            if (targetReady && TryHitLeader(target, transform.position, transform.position)) return;
            if (main == null)
            {
                holding = true;
                return;
            }
            var rearReach = targetReady ? RearReach(target) : 0f;
            if (wallPauseRemaining > 0f)
            {
                wallPauseRemaining = Mathf.Max(0f, wallPauseRemaining - dt);
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
                var waiting = gap < 0f;
                var travelSpeed = waiting ? WaitingSpeed(target, -gap) : PursuitSpeed(targetReady ? target : null, gap - rearReach);
                if (waiting)
                {
                    if (travelSpeed <= 0f)
                    {
                        holding = true;
                        return;
                    }
                }
                var aim = AimPoint(targetReady ? target : null, rearReach, out var intercepting);
                var desired = Vector3.ProjectOnPlane(aim - transform.position, Vector3.up);
                if (desired.sqrMagnitude < 0.0001f && wallRecoveryRemaining <= 0f) { holding = true; break; }
                var recovering = wallRecoveryRemaining > 0f;
                if (recovering)
                {
                    wallRecoveryRemaining = Mathf.Max(0f, wallRecoveryRemaining - subDt);
                    direction = recoveryDirection;
                    travelSpeed = speed * 0.5f;
                }
                else
                {
                    var turn = Vector3.Angle(direction, desired);
                    var releasing = angledRelease && Age < launchSteeringTime;
                    var steering = releasing ? launchTurnRateDegrees : turnRateDegrees;
                    // Keep close-pursuit interception responsive outside an angled release, without changing corners.
                    if (intercepting && !releasing) steering = Mathf.Max(steering, leaderInterceptTurnRate);
                    var turnStep = steering * subDt;
                    direction = Vector3.RotateTowards(direction, desired.normalized, turnStep * Mathf.Deg2Rad, 0f);
                    if (intercepting) turn = Mathf.Max(0f, turn - turnStep);
                    travelSpeed *= Mathf.Clamp01(1f - turn / 100f);
                }
                var from = transform.position;
                var step = travelSpeed * subDt;
                var to = Hover(from + direction * step, out var overGap);
                // Follow a drop down no steeper than it advances, like a falling kart, rather than in one step.
                to.y = Mathf.Max(to.y, from.y - step);
                var clear = FlyTo(from, to, out var reached, out var wallNormal);
                transform.SetPositionAndRotation(reached, Quaternion.LookRotation(direction, Vector3.up));
                velocity = (reached - from) / subDt;
                gapRun = overGap ? gapRun + Vector3.ProjectOnPlane(reached - from, Vector3.up).magnitude : 0f;
                if (targetReady && TryHitLeader(target, from, reached)) return;
                if (clear) continue;
                // Native opponent driving records repeated collision flags before entering timed stuck recovery.
                // Keep that visible hesitation, but bound it and sweep the escape instead of reproducing a pin.
                BeginWallRecovery(wallNormal);
                holding = true;
                velocity = Vector3.zero;
                break;
            }
        }

        void BeginWallRecovery(Vector3 normal)
        {
            normal = Vector3.ProjectOnPlane(normal, Vector3.up).normalized;
            var course = FollowedPath.GetFlatTangent(Mathf.Max(0f, FollowedDistance));
            if (normal.sqrMagnitude < 0.01f) normal = -direction;
            var tangent = Vector3.ProjectOnPlane(course, normal).normalized;
            if (tangent.sqrMagnitude < 0.01f)
            {
                tangent = Vector3.Cross(Vector3.up, normal).normalized;
                if (Vector3.Dot(tangent, direction) < 0f) tangent = -tangent;
            }
            recoveryDirection = (tangent + normal * 0.35f).normalized;
            // A second face encountered during the escape changes direction without repeatedly restarting the pause.
            if (wallRecoveryRemaining <= 0f) wallPauseRemaining = wallPauseTime;
            wallRecoveryRemaining = wallRecoveryTime;
        }

        Kart LeaderTarget()
        {
            var leader = PowerupTargeting.RaceLeader();
            return leader == Owner && Age < selfTargetDelay ? null : leader;
        }

        float RearReach(Kart target)
        {
            var chassis = target.Physics.Chassis;
            if (chassis == null) return 0f;
            var t = chassis.transform;
            var scale = t.lossyScale;
            var half = Vector3.Scale(chassis.size * 0.5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var back = -direction;
            return Mathf.Max(0f, Vector3.Dot(t.TransformPoint(chassis.center) - target.transform.position, back)
                + half.x * Mathf.Abs(Vector3.Dot(t.right, back))
                + half.y * Mathf.Abs(Vector3.Dot(t.up, back))
                + half.z * Mathf.Abs(Vector3.Dot(t.forward, back)));
        }

        float PursuitSpeed(Kart target, float gap)
        {
            if (target == null || gap >= leaderSlowDistance) return speed;
            var closeSpeed = Mathf.Min(speed, target.NormalTopSpeed * leaderSpeedShare);
            // A close-speed plateau keeps a cruising leader outside contact range instead of guaranteeing a hit.
            var approach = Mathf.InverseLerp(leaderSlowDistance * 0.45f, leaderSlowDistance, gap);
            return Mathf.Lerp(closeSpeed, speed, approach);
        }

        float WaitingSpeed(Kart target, float distance)
        {
            var creep = Mathf.Min(speed, waitingCreepSpeed);
            var near = Mathf.Clamp(target.NormalTopSpeed * waitingNearSpeedShare, creep, speed);
            // Original seeking-first switches to the player's pressure speed throughout its nearby band.
            if (distance <= waitingPressDistance) return near;
            // Fixed-step approximation of its far-ahead throttle/coast creep, also fast-forwarded online.
            return Mathf.Repeat(Age, waitingPulsePeriod) < waitingPulseDuration ? creep : 0f;
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

        Vector3 AimPoint(Kart target, float rearReach, out bool intercepting)
        {
            var path = FollowedPath;
            var d = FollowedDistance;
            var samePath = target != null && target.Progress.CurrentBranch == (routeIndex >= 0 ? path : null);
            var td = samePath ? (routeIndex >= 0 ? target.Progress.BranchDistance : target.Progress.PathDistance) : 0f;
            var rootGap = samePath ? path.Delta(d, td) : float.PositiveInfinity;
            var gap = rootGap - rearReach;
            intercepting = false;
            if (samePath && Mathf.Abs(rootGap) <= leaderInterceptDistance)
            {
                var delta = Vector3.ProjectOnPlane(target.transform.position - transform.position, Vector3.up);
                var distanceSq = delta.sqrMagnitude;
                if (distanceSq <= leaderInterceptDistance * leaderInterceptDistance
                    && Vector3.Dot(direction, delta) >= InterceptConeDot * Mathf.Sqrt(distanceSq))
                {
                    var sight = transform.position + delta;
                    intercepting = !Physics.Linecast(transform.position, sight, ObstacleMask, QueryTriggerInteraction.Ignore);
                }
            }
            var drift = 0f;
            if (gap >= 0f && gap < TargetLaneReach && pursuitDriftWidth > 0f)
            {
                // Launch age also advances during online catch-up. Fade the weave before contact so a brief
                // hesitation still gets caught instead of sending the fireball around the leader.
                var weight = Mathf.InverseLerp(hitRadius, hitRadius + 0.5f, gap)
                    * (1f - Mathf.InverseLerp(lookAhead, TargetLaneReach, gap));
                drift = Mathf.Sin(Age * (2f * Mathf.PI / pursuitDriftPeriod)) * pursuitDriftWidth * weight;
            }
            // The original directly targets a leader entering the side/front cone, even before its root passes.
            if (intercepting && drift == 0f) return target.transform.position;
            var margin = GameRoot.Instance != null && GameRoot.Instance.Config != null
                ? GameRoot.Instance.Config.aiRacingLineMargin : collisionRadius;
            var road = AIRacingLine.Of(track, path, margin);
            if (intercepting)
            {
                var targetLane = road.RoadLaneOf(target.transform.position, td);
                var driftLane = DriftLane(road, td, targetLane, drift);
                return target.transform.position + (road.PointAt(td, driftLane) - road.PointAt(td, targetLane));
            }
            var ahead = road.LookAheadDistance(transform.position, d, lookAhead, 0);
            var lane = road.Lane(ahead, 0);
            if (gap >= 0f && gap < TargetLaneReach)
            {
                var targetLane = Mathf.Clamp(road.RoadLaneOf(target.transform.position, td), 0.05f, 0.95f);
                lane = Mathf.Lerp(lane, targetLane, 1f - gap / TargetLaneReach);
                if (drift != 0f) lane = DriftLane(road, ahead, lane, drift);
            }
            return ClearAim(road, ahead, lane);
        }

        Vector3 ClearAim(AIRacingLine road, float distance, float lane)
        {
            // The solved line can run through scenery the karts steer around (the pillar inside Rome's arcade). Aim
            // beside it, nearer side first, instead of ramming it again after every wall recovery.
            var aim = road.PointAt(distance, lane);
            if (Reachable(aim)) return aim;
            var width = road.Width(distance);
            road.RoadEdges(distance, out var left, out var right);
            var inset = Mathf.Min(collisionRadius / width, (right - left) * 0.5f);
            var side = road.RoadLaneOf(transform.position, distance) < lane ? -1f : 1f;
            for (var i = 1; i <= AvoidSteps; i++)
                for (var s = 0; s < 2; s++, side = -side)
                {
                    var candidate = lane + side * i * AvoidStep / width;
                    if (candidate < left + inset || candidate > right - inset) continue;
                    var point = road.PointAt(distance, candidate);
                    if (Reachable(point)) return point;
                }
            return aim;
        }

        bool Reachable(Vector3 aim)
        {
            var from = transform.position;
            var delta = Vector3.ProjectOnPlane(aim - from, Vector3.up);
            var length = delta.magnitude;
            if (length < 0.01f) return true;
            var count = Physics.SphereCastNonAlloc(from, collisionRadius, delta / length, sweepHits, length,
                ObstacleMask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var hit = sweepHits[i];
                // Slopes the shot hovers up and initial overlaps do not block the way.
                if (hit.distance <= 0f || hit.normal.y >= WalkableNormalY || Kart.FromCollider(hit.collider) != null
                    || hit.collider.transform.IsChildOf(transform)) continue;
                return false;
            }
            return true;
        }

        float DriftLane(AIRacingLine road, float distance, float lane, float drift)
        {
            var width = road.Width(distance);
            road.RoadEdges(distance, out var left, out var right);
            var inset = Mathf.Min(collisionRadius / width, (right - left) * 0.5f);
            // If the kart is already outside the solved edges, only limit outward drift; don't snap the aim back in.
            return Mathf.Clamp(lane + drift / width, Mathf.Min(lane, left + inset), Mathf.Max(lane, right - inset));
        }

        Vector3 Hover(Vector3 position, out bool overGap)
        {
            // Probe from under any roof. Started above a low ceiling (Rome's arcades), the ray lands on the roof's
            // top and every following sweep presses the shot into its underside.
            var above = GroundProbeAbove;
            if (Physics.Raycast(position, Vector3.up, out var roof, above, ObstacleMask, QueryTriggerInteraction.Ignore))
                above = Mathf.Max(0f, roof.distance - Skin);
            // Fall zones only catch karts (ItemPhysics): over them the shot has no ground, like over a gap.
            var groundY = GroundProbe.Down(position, above, GroundProbeBelow, ObstacleMask, out var ground)
                && !ItemPhysics.IsPassThrough(ground.collider) ? ground.point.y : float.NegativeInfinity;
            // Cross gaps the karts jump and holes in road colliders level, like a jumping kart: descending into them
            // pins the shot against their far edge (Rome's crusher pits, Island's broken bridge) or traps it in a pit
            // beside the road. Ground still far below after MaxGapRun is a real drop, which the shot follows down.
            var level = position.y - hoverHeight;
            overGap = groundY < level - GapDepth;
            if (overGap && gapRun < MaxGapRun) groundY = level;
            if (float.IsNegativeInfinity(groundY)) return position;
            // Under a roof lower than the hover height, fly as high as it leaves room for instead of pinning to it.
            var clearance = collisionRadius + Skin;
            var height = hoverHeight > clearance
                ? clearance + Headroom(new Vector3(position.x, groundY + clearance, position.z), hoverHeight - clearance)
                : hoverHeight;
            position.y = groundY + height;
            return position;
        }

        float Headroom(Vector3 from, float distance)
        {
            var count = Physics.SphereCastNonAlloc(from, collisionRadius, Vector3.up, sweepHits, distance + Skin,
                ObstacleMask, QueryTriggerInteraction.Ignore);
            var free = distance;
            for (var i = 0; i < count; i++)
            {
                var hit = sweepHits[i];
                // Initial overlaps (the sloped road under the probe) report zero distance; they are no roof.
                if (hit.distance <= 0f || Kart.FromCollider(hit.collider) != null || hit.collider.transform.IsChildOf(transform)) continue;
                free = Mathf.Min(free, Mathf.Max(0f, hit.distance - Skin));
            }
            return free;
        }

        int ObstacleMask => worldMask & ~GameLayers.KartAndItemMask;

        bool FlyTo(Vector3 from, Vector3 to, out Vector3 reached, out Vector3 normal)
        {
            if (to.y < from.y)
            {
                // Leaving a ramp or a barrier top: travel level, then descend. A straight diagonal sweep clips the edge
                // being left and pins the shot on it.
                if (SweepMove(from, new Vector3(to.x, from.y, to.z), out var level, out _))
                {
                    SweepMove(level, new Vector3(level.x, to.y, level.z), out reached, out _);
                    normal = Vector3.zero;
                    return true;
                }
                // Ducking under a lintel: descend first, then travel level.
                if (SweepMove(from, new Vector3(from.x, to.y, from.z), out var low, out _)
                    && SweepMove(low, to, out reached, out normal)) return true;
            }
            // Climbing stays one sweep, so walls still stop the shot.
            return SweepMove(from, to, out reached, out normal);
        }

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

        bool LeaderContact(Kart target, Vector3 from, Vector3 to, out Vector3 contact)
        {
            contact = to;
            if (target == null || hitRadius <= 0f) return false;
            var chassis = target.Physics.Chassis;
            if (chassis == null || !chassis.enabled || !chassis.gameObject.activeInHierarchy) return false;
            var count = Physics.OverlapCapsuleNonAlloc(from, to, hitRadius, overlaps,
                1 << chassis.gameObject.layer, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                if (overlaps[i] != chassis) continue;
                var nearest = GeometryUtil.ClosestOnSegment(chassis.bounds.center, from, to, 1e-8f);
                contact = chassis.ClosestPoint(nearest);
                // Reaching the body through a wall is still not contact.
                return !Physics.Linecast(from, contact, ObstacleMask, QueryTriggerInteraction.Ignore);
            }
            return false;
        }

        bool TryHitLeader(Kart target, Vector3 from, Vector3 to)
        {
            if (target == Owner && !ownerCleared) return false;
            if (!LeaderContact(target, from, to, out var contact)) return false;
            if (spent) return true;
            spent = true; // Reserve the hit before applying an effect, which can synchronously echo online.
            HitKart(target, hitEffect);
            SpawnImpact(contact);
            return true;
        }

        void FinishImpact(Vector3 contact)
        {
            if (spent || !gameObject.activeSelf) return;
            spent = true;
            SpawnImpact(contact);
        }

        void SpawnImpact(Vector3 contact)
        {
            PowerupFx.Spawn(impactVfx, contact);
            Despawn(endVfx, contact, hitSound);
        }
    }
}
