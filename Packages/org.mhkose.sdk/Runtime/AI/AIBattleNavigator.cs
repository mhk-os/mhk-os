using MHK.Core;
using MHK.Karts;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Finds the way of a computer driver in a battle to its goal (an opponent hunted, an arena spot or item box, or the
    /// line behind it when it turns around): straight across the ground when it is near (always in arenas), else along the
    /// racing line in whichever direction is shorter and along the alternative route a goal lies on, and around walls,
    /// solid props, unsafe ground (<see cref="AIBattleArea"/>) and the course of an opponent about to ram it
    /// (<see cref="AIRamThreat"/>) by trying headings to both sides of the wanted one. Off arenas it turns around on the
    /// road: first on to a wide stretch or a fork close ahead when the road is narrow, then keeping to the road (its edges
    /// stop the kart like drops, so a narrow road gets a three-point turn). Offroad it prefers headings back towards the road.
    /// </summary>
    internal sealed class AIBattleNavigator
    {
        const float SampleStep = 3f, GroundClearance = 0.25f, GroundProbeDepth = 2f;
        const float AngleCost = 0.08f, KeepBonus = 2f, FreeBonus = 3f, MinReach = 5f;
        const float SlowScanInterval = 0.2f, FastScanInterval = 0.08f;
        const float CarrotBase = 8f, CarrotPerSpeed = 0.6f, MaxCarrot = 22f;
        const float LookPerSpeed = 0.8f, MinLook = 6f;
        const float BlockedSeconds = 1.5f, LineSeconds = 3f, BlockedAngle = 60f, NearGoal = 15f;
        const float DirectionMargin = 30f, GapCheckStep = 2f, GoalJump = 20f;
        const float HazardMargin = 6f, KartClearance = 2f, KartGap = 1.5f;
        // Turning around off arenas: a wanted direction more than TurnEnterAngle off the nose starts a turn, less than
        // TurnLeaveAngle ends it. While turning the probes keep within TurnLeash of the road edges, or no farther out than
        // the kart already is plus TurnSlack (paved ground within the leash too). A road narrower than TurnWidth: drive on first to a wider stretch or a fork
        // (JunctionRoom) up to TurnSearch metres ahead, for at most TurnDelaySeconds, unless the goal is nearer than
        // TurnDelayGoal (and is no turn around). TurnArrive: close enough to the turning spot.
        const float TurnEnterAngle = 110f, TurnLeaveAngle = 50f, TurnLeash = 1f, TurnSlack = 1f;
        const float TurnWidth = 14f, TurnSearch = 30f, TurnSearchStep = 5f, JunctionRoom = 8f, TurnDelaySeconds = 4f;
        const float TurnDelayGoal = 60f, TurnArrive = 3f;
        // Offroad, a heading scores this per metre its reach gets closer to the road.
        const float RoadPull = 1f;

        [NoAutoStaticsCleanup] static readonly float[] Headings = { 0f, -20f, 20f, -40f, 40f, -65f, 65f, -95f, 95f, -130f, 130f };
        [NoAutoStaticsCleanup] static readonly RaycastHit[] Hits = new RaycastHit[32];

        readonly float scanInterval;
        readonly AIRamThreat rams;
        readonly AIBattleArea.Hints hints = new();
        readonly AIBattleArea.Hints goalHints = new();
        int goalRoute = -1;
        float goalAt;
        Vector3 lastGoal;
        int lineDirection;
        float scanTimer, lineTimer, blockedTimer;
        float bestAngle, bestReach, minNormalY;
        float roadBeyond, turnLeash, turnSpot, turnDelayTimer;
        int turnSign;
        bool alongLine, turning, turnDelayed;

        public AIBattleNavigator(DifficultySettings difficulty, AIRamThreat rams)
        {
            scanInterval = Mathf.Lerp(SlowScanInterval, FastScanInterval, difficulty.aiPrecision);
            this.rams = rams;
        }

        /// <summary>World point the kart steers at this step.</summary>
        public Vector3 SteerPoint { get; private set; }
        /// <summary>Distance (m) the kart's direction of travel has safe ground, checked up to its stopping distance.</summary>
        public float GroundAhead { get; private set; } = float.PositiveInfinity;
        /// <summary>Distance (m) the kart's direction of travel is clear of walls and solid bodies, checked as far.</summary>
        public float WallAhead { get; private set; } = float.PositiveInfinity;
        /// <summary>
        /// The kart follows the racing line or an alternative route towards its goal (the goal is far, lies on a route the
        /// kart is not on, or the direct way is blocked).
        /// </summary>
        public bool AlongLine => alongLine;
        /// <summary>Distance (m) left to the goal: along the lines while following them, else straight across.</summary>
        public float Way { get; private set; }
        /// <summary>A jump over a gap lies ahead on the racing line being followed forwards: full throttle along the line.</summary>
        public bool Jumping { get; private set; }
        /// <summary>The kart turns around (or drives on to a spot to turn at): the way to the goal may grow meanwhile.</summary>
        public bool Turning => turning;

        /// <summary>
        /// Forgets the state of an earlier stretch off the racing line (turns, blocked goals, line direction): the kart
        /// at <paramref name="position"/> starts a new one.
        /// </summary>
        public void Reset(Vector3 position)
        {
            turning = turnDelayed = false;
            lineTimer = blockedTimer = scanTimer = 0f;
            lineDirection = 0;
            bestAngle = 0f;
            bestReach = 0f;
            SteerPoint = position;
            GroundAhead = WallAhead = float.PositiveInfinity;
        }

        /// <summary>
        /// Plans this physics step's steering point towards <paramref name="goal"/>, around every kart but <paramref name="target"/>.
        /// <paramref name="turnAround"/>: the goal is the way back along the road, so a narrow road is driven on to a wide
        /// spot first however near the goal is.
        /// </summary>
        public void Tick(Kart kart, Kart target, TrackRuntime track, Vector3 goal, bool arena, float leash, float radius, bool turnAround,
            GameConfig cfg, float dt)
        {
            var pos = kart.transform.position;
            minNormalY = AIBattleArea.MinNormalY(cfg);
            var speed = Mathf.Abs(kart.Physics.ForwardSpeed);
            lineTimer -= dt;
            if ((goal - lastGoal).sqrMagnitude > GoalJump * GoalJump) goalHints.Reset();
            lastGoal = goal;
            goalRoute = AIBattleArea.RouteAt(track, goal, goalHints, out goalAt);
            var offRoute = goalRoute >= 0 && AIBattleArea.BeyondRoute(track, goalRoute, pos, hints, out _) > 0f;
            alongLine = lineTimer > 0f || offRoute || (!arena && Flat(goal - pos).magnitude > cfg.aiBattleDirectRange);
            Way = Flat(goal - pos).magnitude;
            var wanted = alongLine ? Carrot(kart, track, goal, speed) : goal;
            Jumping = alongLine && lineDirection > 0 && GapAhead(track, kart.Progress.PathDistance, cfg.gapRunUp);
            if (Jumping)
            {
                SteerPoint = wanted;
                GroundAhead = WallAhead = float.PositiveInfinity;
                return;
            }
            var toWanted = Flat(wanted - pos);
            var wantedDir = toWanted.sqrMagnitude > 0.01f ? toWanted.normalized : Flat(kart.transform.forward).normalized;
            scanTimer -= dt;
            turnDelayTimer -= dt;
            var scan = scanTimer <= 0f;
            if (scan)
            {
                scanTimer = scanInterval;
                roadBeyond = AIBattleArea.BeyondRoad(track, pos, hints);
                WatchTurn(kart, track, arena, wantedDir, turnAround);
            }
            if (turnDelayed && TurnSpotReached(kart, track)) turnDelayed = false;
            if (turnDelayed)
            {
                wanted = track.path.GetPoint(turnSpot);
                toWanted = Flat(wanted - pos);
                if (toWanted.sqrMagnitude > 0.01f) wantedDir = toWanted.normalized;
            }
            if (scan)
            {
                Scan(kart, target, track, wantedDir, toWanted.magnitude, speed, arena, leash, radius, cfg);
                Hazard(kart, track, leash, radius, cfg);
            }
            WatchBlocked(toWanted.magnitude, dt);
            var dir = Quaternion.AngleAxis(bestAngle, Vector3.up) * wantedDir;
            var look = Mathf.Max(MinLook, speed * LookPerSpeed);
            SteerPoint = bestAngle == 0f && toWanted.magnitude <= Mathf.Max(bestReach, look) ? wanted : pos + dir * Mathf.Clamp(bestReach, MinLook, look);
        }

        // A goal the kart keeps turning away from or cannot get closer to: follow the racing line for a while.
        void WatchBlocked(float toWanted, float dt)
        {
            var blocked = !alongLine && toWanted > NearGoal && (Mathf.Abs(bestAngle) >= BlockedAngle || bestReach < MinReach);
            blockedTimer = blocked ? blockedTimer + dt : 0f;
            if (blockedTimer < BlockedSeconds) return;
            blockedTimer = 0f;
            lineTimer = LineSeconds;
        }

        // A turn of the wanted direction behind the kart starts (off arenas): on a narrow road maybe after driving on to a
        // better spot. It ends once the wanted direction is roughly ahead.
        void WatchTurn(Kart kart, TrackRuntime track, bool arena, Vector3 wantedDir, bool turnAround)
        {
            if (arena)
            {
                turning = turnDelayed = false;
                return;
            }
            var forward = Flat(kart.transform.forward);
            var angle = Vector3.Angle(forward, wantedDir);
            if (turning)
            {
                if (angle < TurnLeaveAngle) turning = turnDelayed = false;
                return;
            }
            if (angle <= TurnEnterAngle) return;
            turning = true;
            turnLeash = Mathf.Max(TurnLeash, roadBeyond + TurnSlack);
            turnDelayed = (turnAround || Way > TurnDelayGoal) && roadBeyond <= 0f && FindTurnSpot(kart, track, forward);
            turnDelayTimer = TurnDelaySeconds;
        }

        // A stretch of drivable road at least TurnWidth wide or a fork up to TurnSearch metres ahead on the main line,
        // short of a jump or the end of an open line. None when the road here is wide enough or the kart is on an
        // alternative route.
        bool FindTurnSpot(Kart kart, TrackRuntime track, Vector3 forward)
        {
            if (AIBattleArea.RouteAt(track, kart.transform.position, hints, out _) >= 0) return false;
            var path = track.path;
            var road = AIRacingLine.Of(track, path, GameRoot.Instance.Config.aiRacingLineMargin);
            var d = kart.Progress.PathDistance;
            if (road.Width(d) >= TurnWidth) return false;
            turnSign = Vector3.Dot(forward, path.GetFlatTangent(d, 1f)) >= 0f ? 1 : -1;
            for (var x = TurnSearchStep; x <= TurnSearch; x += TurnSearchStep)
            {
                var at = d + turnSign * x;
                if (!path.closed && (at < 0f || at > path.Length)) return false;
                if (TrackGaps.IsGap(track, path, at)) return false;
                if (road.Width(at) < TurnWidth && !NearFork(track, at)) continue;
                turnSpot = path.WrapDistance(at);
                return true;
            }
            return false;
        }

        static bool NearFork(TrackRuntime track, float at)
        {
            var path = track.path;
            var routes = track.alternatePaths;
            for (var i = 0; i < routes.Count; i++)
                if (routes[i] != null && (Mathf.Abs(path.Delta(at, track.BranchJoinStart(i))) < JunctionRoom
                        || Mathf.Abs(path.Delta(at, track.BranchJoinEnd(i))) < JunctionRoom))
                    return true;
            return false;
        }

        bool TurnSpotReached(Kart kart, TrackRuntime track) =>
            turnDelayTimer <= 0f || track.path.Delta(kart.Progress.PathDistance, turnSpot) * turnSign < TurnArrive;

        // A kart pushed beyond the leash may still drive back, but no further out. While turning around (not while driving
        // on to the turning spot) the probes keep to the road and paved ground (pavedLeash) within the leash.
        float ProbeLeash(float leash, out float pavedLeash)
        {
            pavedLeash = Mathf.Max(leash, roadBeyond);
            return turning && !turnDelayed ? Mathf.Min(pavedLeash, turnLeash) : pavedLeash;
        }

        // Headings are cut short at walls, unsafe ground, karts in the way and where a rammer would meet the kart.
        void Scan(Kart kart, Kart target, TrackRuntime track, Vector3 wantedDir, float toWanted, float speed, bool arena, float leash, float radius,
            GameConfig cfg)
        {
            AIObstacleClassifier.Prepare(cfg.aiPushableMass);
            var range = Mathf.Min(cfg.aiBattleLookRange, toWanted + SampleStep);
            Origin(kart, track, radius, out var origin, out var groundY);
            leash = ProbeLeash(leash, out var paved);
            var offroad = !arena && roadBeyond > 0f;
            var previous = bestAngle;
            var bestScore = float.NegativeInfinity;
            for (var i = 0; i < Headings.Length; i++)
            {
                var angle = Headings[i];
                var dir = Quaternion.AngleAxis(angle, Vector3.up) * wantedDir;
                var reach = rams.Along(origin, dir, speed,
                    KartsAlong(kart, target, origin, dir, Probe(track, origin, groundY, dir, range, leash, paved, radius, out _, out var endBeyond)));
                var free = reach >= range - 0.01f;
                var score = reach - Mathf.Abs(angle) * AngleCost + (angle == previous ? KeepBonus : 0f) + (free ? FreeBonus : 0f)
                    + (offroad ? RoadPull * (roadBeyond - endBeyond) : 0f);
                if (score <= bestScore) continue;
                bestScore = score;
                bestAngle = angle;
                bestReach = reach;
                if (i == 0 && free) break;
            }
        }

        // Other karts in the way are obstacles too (piles of karts pushing each other), except the one being hunted.
        static float KartsAlong(Kart kart, Kart target, Vector3 origin, Vector3 dir, float reach)
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (o == kart || o == target || !o.isActiveAndEnabled) continue;
                var to = Flat(o.transform.position - origin);
                var along = Vector3.Dot(to, dir);
                if (along <= 0f || along >= reach || (to - dir * along).magnitude > KartClearance) continue;
                reach = Mathf.Max(0f, along - KartGap);
            }
            return reach;
        }

        void Hazard(Kart kart, TrackRuntime track, float leash, float radius, GameConfig cfg)
        {
            GroundAhead = WallAhead = float.PositiveInfinity;
            var v = Flat(kart.Physics.Body.linearVelocity);
            var speed = v.magnitude;
            var dir = speed >= 1f ? v / speed : Flat(kart.transform.forward).normalized;
            Origin(kart, track, radius, out var origin, out var groundY);
            var range = speed * speed / (2f * Mathf.Max(1f, cfg.aiPlanBraking)) + HazardMargin;
            var reach = Probe(track, origin, groundY, dir, range, ProbeLeash(leash, out var paved), paved, radius, out var wall, out _);
            if (wall < range - 0.01f) WallAhead = wall;
            if (reach < wall) GroundAhead = reach;
        }

        static void Origin(Kart kart, TrackRuntime track, float radius, out Vector3 origin, out float groundY)
        {
            var pos = kart.transform.position;
            var lift = Vector3.up * (radius + GroundClearance);
            groundY = pos.y;
            origin = pos + lift;
            if (!GroundProbe.Down(pos, 1f, GroundProbeDepth, track.ObstacleMask, out var ground)) return;
            groundY = ground.point.y;
            origin = ground.point + lift;
        }

        // Safe reach along dir; wall: distance to the first wall or solid body; endBeyond: how far the last safe sample lies
        // beyond the road edges (where the kart is when there is none).
        float Probe(TrackRuntime track, Vector3 origin, float groundY, Vector3 dir, float range, float leash, float pavedLeash, float radius,
            out float wall, out float endBeyond)
        {
            wall = Mathf.Min(Sweep(origin, dir, range, radius, track.ObstacleMask),
                AIFeelers.LowWall(origin, dir, range, radius + GroundClearance, radius, track.ObstacleMask));
            endBeyond = roadBeyond;
            var y = groundY;
            for (var s = SampleStep; s <= wall; s += SampleStep)
            {
                if (!AIBattleArea.Safe(track, origin + dir * s, y, leash, pavedLeash, minNormalY, hints, out var next, out var beyond)) return s - SampleStep;
                y = next;
                endBeyond = beyond;
            }
            return wall;
        }

        static float Sweep(Vector3 from, Vector3 dir, float length, float radius, int mask)
        {
            var n = Physics.SphereCastNonAlloc(from, radius, dir, Hits, length, mask, QueryTriggerInteraction.Ignore);
            var best = length;
            for (var i = 0; i < n; i++)
            {
                ref readonly var hit = ref Hits[i];
                if (hit.distance <= 0f) continue;
                var kind = AIObstacleClassifier.Classify(hit);
                if (kind is ObstacleKind.Wall or ObstacleKind.Body) best = Mathf.Min(best, hit.distance);
            }
            return best;
        }

        // Along the lines to the goal: on the alternative route the goal lies on, along it. Else along the kart's own route
        // to its end nearer the target, then the main line in the shorter direction (forwards, or backwards when no gap is
        // in the way) to the goal or to the nearer end of the goal's route. Gaps are known on the main line only, so no
        // jumps are planned along a route.
        Vector3 Carrot(Kart kart, TrackRuntime track, Vector3 goal, float speed)
        {
            var carrot = Mathf.Clamp(CarrotBase + speed * CarrotPerSpeed, CarrotBase, MaxCarrot);
            var pos = kart.transform.position;
            var target = goal;
            var tail = 0f;
            float to;
            if (goalRoute >= 0)
            {
                var route = track.alternatePaths[goalRoute];
                if (AIBattleArea.BeyondRoute(track, goalRoute, pos, hints, out var at) <= 0f) return AlongRoute(route, at, goalAt, goal, carrot, 0f);
                var start = StartNearer(track, goalRoute, kart.Progress.PathDistance, goalAt);
                to = start ? track.BranchJoinStart(goalRoute) : track.BranchJoinEnd(goalRoute);
                target = route.GetPoint(start ? 0f : route.Length);
                tail = start ? goalAt : route.Length - goalAt;
            }
            else to = track.path.Project(goal, ref goalHints.main, out _);
            var from = kart.Progress.PathDistance;
            var own = AIBattleArea.RouteAt(track, pos, hints, out var ownAt);
            if (own >= 0)
            {
                var route = track.alternatePaths[own];
                var exitStart = StartNearer(track, own, to, ownAt);
                from = exitStart ? track.BranchJoinStart(own) : track.BranchJoinEnd(own);
                var left = exitStart ? ownAt : route.Length - ownAt;
                if (left > carrot) return AlongRoute(route, ownAt, exitStart ? 0f : route.Length, goal, carrot, Mathf.Abs(track.path.Delta(from, to)) + tail);
            }
            return LineCarrot(track, from, to, target, carrot, tail);
        }

        Vector3 LineCarrot(TrackRuntime track, float from, float to, Vector3 target, float carrot, float tail)
        {
            var path = track.path;
            var forward = path.WrapDistance(to - from);
            var backward = path.Length - forward;
            var direction = forward <= backward ? 1 : -1;
            if (lineDirection != 0 && direction != lineDirection && Mathf.Abs(forward - backward) < DirectionMargin) direction = lineDirection;
            if (direction < 0 && GapAlong(track, from, backward, -1f)) direction = 1;
            lineDirection = direction;
            var left = direction > 0 ? forward : backward;
            Way = left + tail;
            return left <= carrot ? target : path.GetPoint(path.WrapDistance(from + direction * carrot));
        }

        Vector3 AlongRoute(TrackPath route, float at, float to, Vector3 goal, float carrot, float tail)
        {
            lineDirection = 0;
            var delta = to - at;
            Way = Mathf.Abs(delta) + tail;
            return tail <= 0f && Mathf.Abs(delta) <= carrot ? goal : route.GetPoint(route.WrapDistance(at + Mathf.Sign(delta) * carrot));
        }

        static bool StartNearer(TrackRuntime track, int route, float mainPoint, float at)
        {
            var path = track.path;
            var length = track.alternatePaths[route].Length;
            return Mathf.Abs(path.Delta(mainPoint, track.BranchJoinStart(route))) + at
                <= Mathf.Abs(path.Delta(mainPoint, track.BranchJoinEnd(route))) + length - at;
        }

        // Jumps are made in the driving direction: driving the line backwards across one is a fall.
        static bool GapAlong(TrackRuntime track, float from, float length, float direction)
        {
            for (var x = 0f; x <= length; x += GapCheckStep)
                if (TrackGaps.IsGap(track, track.path, from + direction * x))
                    return true;
            return false;
        }

        static bool GapAhead(TrackRuntime track, float from, float length) => GapAlong(track, from, length, 1f);

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    }
}
