using MHK.Core;
using MHK.Karts;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Finds the way of a computer driver to a battle goal: straight across the ground when it is near (always in arenas),
    /// else along the racing line in whichever direction is shorter and along the alternative route a goal lies on, and
    /// around walls, solid props and unsafe ground (<see cref="AIBattleArea"/>) by trying headings to both sides of the
    /// wanted one.
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

        [NoAutoStaticsCleanup] static readonly float[] Headings = { 0f, -20f, 20f, -40f, 40f, -65f, 65f, -95f, 95f, -130f, 130f };
        [NoAutoStaticsCleanup] static readonly RaycastHit[] Hits = new RaycastHit[32];

        readonly float scanInterval;
        readonly AIBattleArea.Hints hints = new();
        readonly AIBattleArea.Hints goalHints = new();
        int goalRoute = -1;
        float goalAt;
        Vector3 lastGoal;
        int lineDirection;
        float scanTimer, lineTimer, blockedTimer;
        float bestAngle, bestReach, minNormalY;
        bool alongLine;

        public AIBattleNavigator(DifficultySettings difficulty) =>
            scanInterval = Mathf.Lerp(SlowScanInterval, FastScanInterval, difficulty.aiPrecision);

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

        /// <summary>Plans this physics step's steering point towards <paramref name="goal"/>, around every kart but <paramref name="target"/>.</summary>
        public void Tick(Kart kart, Kart target, TrackRuntime track, Vector3 goal, bool arena, float leash, float radius, GameConfig cfg, float dt)
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
            if (scanTimer <= 0f)
            {
                scanTimer = scanInterval;
                Scan(kart, target, track, wantedDir, toWanted.magnitude, leash, radius, cfg);
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

        void Scan(Kart kart, Kart target, TrackRuntime track, Vector3 wantedDir, float toWanted, float leash, float radius, GameConfig cfg)
        {
            AIObstacleClassifier.Prepare(cfg.aiPushableMass);
            var range = Mathf.Min(cfg.aiBattleLookRange, toWanted + SampleStep);
            Origin(kart, track, radius, out var origin, out var groundY);
            leash = LeashHere(track, kart.transform.position, leash);
            var previous = bestAngle;
            var bestScore = float.NegativeInfinity;
            for (var i = 0; i < Headings.Length; i++)
            {
                var angle = Headings[i];
                var dir = Quaternion.AngleAxis(angle, Vector3.up) * wantedDir;
                var reach = KartsAlong(kart, target, origin, dir, Probe(track, origin, groundY, dir, range, leash, radius, out _));
                var free = reach >= range - 0.01f;
                var score = reach - Mathf.Abs(angle) * AngleCost + (angle == previous ? KeepBonus : 0f) + (free ? FreeBonus : 0f);
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
            var reach = Probe(track, origin, groundY, dir, range, LeashHere(track, kart.transform.position, leash), radius, out var wall);
            if (wall < range - 0.01f) WallAhead = wall;
            if (reach < wall) GroundAhead = reach;
        }

        // A kart pushed beyond the leash may still drive back, but no further out.
        float LeashHere(TrackRuntime track, Vector3 pos, float leash) => Mathf.Max(leash, AIBattleArea.BeyondRoad(track, pos, hints));

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

        float Probe(TrackRuntime track, Vector3 origin, float groundY, Vector3 dir, float range, float leash, float radius, out float wall)
        {
            wall = Mathf.Min(Sweep(origin, dir, range, radius, track.ObstacleMask),
                AIFeelers.LowWall(origin, dir, range, radius + GroundClearance, radius, track.ObstacleMask));
            var y = groundY;
            for (var s = SampleStep; s <= wall; s += SampleStep)
            {
                if (!AIBattleArea.Safe(track, origin + dir * s, y, leash, minNormalY, hints, out var next)) return s - SampleStep;
                y = next;
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
