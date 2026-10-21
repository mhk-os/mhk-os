using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>Where a world point lies on the followed line (see <see cref="AIRouteFollower.TryLocate"/>).</summary>
    internal readonly struct LanePosition
    {
        /// <summary>Metres ahead of the kart along the followed line.</summary>
        public readonly float Ahead;
        /// <summary>Lane relative to the racing line, as the driving's lanes.</summary>
        public readonly float Lane;
        /// <summary>Road lane (0 = left road edge, 1 = right edge, outside the road beyond).</summary>
        public readonly float RoadLane;
        /// <summary>Road width there (m).</summary>
        public readonly float Width;

        public LanePosition(float ahead, float lane, float roadLane, float width)
        {
            Ahead = ahead;
            Lane = lane;
            RoadLane = roadLane;
            Width = width;
        }
    }

    /// <summary>
    /// The line a computer driver follows: the main racing line or an alternative route it rolled for, with lanes
    /// relative to the racing line (<see cref="RoadShift"/> converts them to road lanes).
    /// </summary>
    internal sealed class AIRouteFollower
    {
        const float AdoptDistance = 8f;
        const float LostBeforeFork = 60f, ForkDecisionMargin = 5f, MinForkAhead = 0.5f;
        const float RouteEndMargin = 1f, MaxRouteLateral = 6f;
        const int LapKeyStride = 64;
        const float SmoothSampleSpacing = 0.5f, MinSmoothAhead = 0.5f, MinSmoothWindow = 0.01f;
        const float GapSampleStep = 2f;
        const float MinLocateAhead = 1f, LocateLevelHeight = 4f, MinLocateWidth = 2f;

        readonly DifficultySettings difficulty;
        readonly int rollSeed;
        TrackRuntime track;
        TrackPath main;
        float mainD;
        int mainSegment;
        TrackPath route;
        int routeIndex = -1;
        float routeD;
        int routeHint = -1;
        float[] routeMinWidth;
        float lineMargin;

        public AIRouteFollower(DifficultySettings difficulty, int rollSeed)
        {
            this.difficulty = difficulty;
            this.rollSeed = rollSeed;
        }

        /// <summary>Alternative route being followed, null on the main line.</summary>
        public TrackPath Route => route;

        /// <summary>
        /// Per step: tracks the kart on the followed line and, on the main line, rolls for each route whose fork is less
        /// than <paramref name="reach"/> metres ahead (once per lap and route).
        /// </summary>
        public void Tick(Kart kart, TrackRuntime t, float reach, bool allowRoutes, GameConfig cfg)
        {
            lineMargin = cfg.aiRacingLineMargin;
            if (t != track) ResetForTrack(t);
            main = t.path;
            var prog = kart.Progress;
            mainD = prog.PathDistance;
            mainSegment = prog.PathSegment;
            if (!allowRoutes)
            {
                Drop();
                return;
            }
            if (route != null)
            {
                Follow(kart, prog);
                return;
            }
            if (TryAdoptBranch(prog)) return;
            TryTakeFork(prog, reach, cfg.aiMinRouteWidth);
        }

        /// <summary>Lane plus this is the road lane (0 = left road edge, 1 = right edge) <paramref name="ahead"/> metres along the followed line.</summary>
        public float RoadShift(float ahead)
        {
            Resolve(ahead, out var p, out var d);
            return Shift(p, d);
        }

        /// <summary>Bend radius (m) of the racing line <paramref name="ahead"/> metres along and the lateral friction of the ground there.</summary>
        public float Radius(float ahead, out float friction)
        {
            Resolve(ahead, out var p, out var d);
            var rl = AIRacingLine.Of(track, p, lineMargin);
            friction = rl.Friction(d);
            return rl.Radius(d);
        }

        /// <summary>World point at lane <paramref name="u"/> <paramref name="ahead"/> metres along, kept on the road.</summary>
        public Vector3 Point(float ahead, float u)
        {
            Resolve(ahead, out var p, out var d);
            var road = Mathf.Clamp01(u + Shift(p, d));
            return p.GetPointAtLane(d, road, p.GetWidth(d));
        }

        /// <summary><see cref="Point"/> averaged over five samples within ±<paramref name="window"/> metres, so a steering target moves steadily.</summary>
        public Vector3 SmoothPoint(float ahead, float u, float window)
        {
            if (window <= MinSmoothWindow) return Point(ahead, u);
            var sum = Vector3.zero;
            for (var i = -2; i <= 2; i++) sum += Point(Mathf.Max(MinSmoothAhead, ahead + i * SmoothSampleSpacing * window), u);
            return sum / 5f;
        }

        /// <summary>Tangent of the followed line <paramref name="ahead"/> metres along.</summary>
        public Vector3 Tangent(float ahead, float smoothing)
        {
            Resolve(ahead, out var p, out var d);
            return p.GetTangent(d, smoothing);
        }

        /// <summary>Road width (m) of the followed line <paramref name="ahead"/> metres along.</summary>
        public float Width(float ahead)
        {
            Resolve(ahead, out var p, out var d);
            return p.GetWidth(d);
        }

        /// <summary>A gap (<see cref="TrackGaps"/>, a jump over a fall zone) lies on the followed line within <paramref name="range"/> metres.</summary>
        public bool GapWithin(float range)
        {
            if (track == null) return false;
            for (var ahead = GapSampleStep; ahead <= range; ahead += GapSampleStep)
            {
                Resolve(ahead, out var path, out var distance);
                if (TrackGaps.IsGap(track, path, distance)) return true;
            }
            return false;
        }

        /// <summary>
        /// Where <paramref name="position"/> lies on the followed line. False when it is behind, further than
        /// <paramref name="maxAhead"/> or on another level.
        /// </summary>
        public bool TryLocate(Vector3 position, float maxAhead, out LanePosition at)
        {
            var onRoute = route != null && routeD >= 0f;
            var line = onRoute ? route : main;
            var from = onRoute ? routeD : mainD;
            var hint = onRoute ? routeHint : mainSegment;
            var d = line.Project(position, ref hint, out _);
            var ahead = line.Delta(from, d);
            at = default;
            if (ahead < MinLocateAhead || ahead > maxAhead) return false;
            if (Mathf.Abs(position.y - line.GetPoint(d).y) > LocateLevelHeight) return false;
            var width = Mathf.Max(MinLocateWidth, line.GetWidth(d));
            var roadLane = line.GetRoadLane(position, d, width);
            at = new LanePosition(ahead, roadLane - Shift(line, d), roadLane, width);
            return true;
        }

        /// <summary>Lane (as the driving's lanes) of <paramref name="position"/> beside the kart's own spot on the followed line.</summary>
        public float LaneHere(Vector3 position)
        {
            Resolve(0f, out var p, out var d);
            return p.GetRoadLane(position, d, Mathf.Max(MinLocateWidth, p.GetWidth(d))) - Shift(p, d);
        }

        /// <summary>Left and right edge of the drivable road (as the driving's lanes) <paramref name="ahead"/> metres along the followed line.</summary>
        public void RoadEdges(float ahead, out float left, out float right)
        {
            Resolve(ahead, out var p, out var d);
            var rl = AIRacingLine.Of(track, p, lineMargin);
            rl.RoadEdges(d, out left, out right);
            var shift = rl.Lane(d) - AILanes.Centre;
            left -= shift;
            right -= shift;
        }

        /// <summary>
        /// Lane of kart <paramref name="other"/> and how far (m) it is ahead along the main line, from its own track
        /// progress, so karts in a bend keep their real lanes. False while either kart is on an alternative route.
        /// </summary>
        public bool TryLaneOf(Kart other, out float lane, out float ahead)
        {
            lane = ahead = 0f;
            var prog = other.Progress;
            if (route != null || prog.CurrentBranch != null || main == null) return false;
            var d = prog.PathDistance;
            lane = main.GetRoadLane(other.transform.position, d, Mathf.Max(MinLocateWidth, main.GetWidth(d))) - Shift(main, d);
            ahead = main.Delta(mainD, d);
            return true;
        }

        void ResetForTrack(TrackRuntime t)
        {
            track = t;
            Drop();
            routeMinWidth = null;
        }

        // Respawned or pushed onto a route: follow it instead of fighting back to the main line.
        bool TryAdoptBranch(RacerProgress prog)
        {
            var onRoute = prog.CurrentBranch;
            if (!(onRoute != null && prog.BranchDistance > AdoptDistance && prog.BranchDistance < onRoute.Length - AdoptDistance)) return false;
            Take(track.alternatePaths.IndexOf(onRoute));
            routeD = prog.BranchDistance;
            return true;
        }

        void TryTakeFork(RacerProgress prog, float reach, float minRouteWidth)
        {
            var chance = difficulty.aiShortcutChance;
            if (chance <= 0f) return;
            reach = Mathf.Min(reach, LostBeforeFork - ForkDecisionMargin);
            var alts = track.alternatePaths;
            for (var i = 0; i < alts.Count; i++)
            {
                var toFork = main.Delta(mainD, track.BranchJoinStart(i));
                if (toFork <= MinForkAhead || toFork > reach) continue;
                if (AIRolls.Roll01(rollSeed, prog.Lap * LapKeyStride + i + 1) >= chance) continue;
                if (MinWidth(i) < minRouteWidth) continue;
                Take(i);
                routeD = -toFork;
                return;
            }
        }

        void Follow(Kart kart, RacerProgress prog)
        {
            var toFork = main.Delta(mainD, track.BranchJoinStart(routeIndex));
            if (prog.CurrentBranch == route)
                routeD = prog.BranchDistance;
            else if (toFork > 0f)
            {
                routeD = -toFork;
                if (toFork > LostBeforeFork) Drop();
                return;
            }
            else
            {
                // Past the fork, before the progress switches over (both lines still close together).
                routeD = route.Project(kart.transform.position, ref routeHint, out var lateral);
                if (lateral > Mathf.Max(MaxRouteLateral, route.GetWidth(routeD)))
                {
                    Drop();
                    return;
                }
            }
            if (routeD >= route.Length - RouteEndMargin) Drop();
        }

        void Take(int index)
        {
            if (index < 0) return;
            route = track.alternatePaths[index];
            routeIndex = index;
            routeHint = -1;
        }

        void Drop()
        {
            route = null;
            routeIndex = -1;
            routeHint = -1;
        }

        float MinWidth(int index)
        {
            var alts = track.alternatePaths;
            if (routeMinWidth == null || routeMinWidth.Length != alts.Count)
            {
                routeMinWidth = new float[alts.Count];
                for (var i = 0; i < alts.Count; i++)
                {
                    var p = alts[i];
                    var w = float.MaxValue;
                    for (var k = 0; k < p.Count; k++) w = Mathf.Min(w, p.GetNodeWidth(k));
                    routeMinWidth[i] = p.Count > 0 ? w : 0f;
                }
            }
            return routeMinWidth[index];
        }

        // Line and distance on it ahead metres further along the followed line.
        void Resolve(float ahead, out TrackPath path, out float distance)
        {
            if (route != null)
            {
                var x = routeD + ahead;
                var length = route.Length;
                if (x >= 0f && x <= length)
                {
                    path = route;
                    distance = x;
                    return;
                }
                path = main;
                distance = x < 0f ? track.BranchJoinStart(routeIndex) + x : track.BranchJoinEnd(routeIndex) + (x - length);
                return;
            }
            path = main;
            distance = mainD + ahead;
        }

        float Shift(TrackPath path, float distance) => AIRacingLine.Of(track, path, lineMargin).Lane(distance) - AILanes.Centre;
    }
}
