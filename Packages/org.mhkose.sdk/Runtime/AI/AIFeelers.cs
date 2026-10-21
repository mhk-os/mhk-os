using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Wall and solid-prop perception of a computer driver: sphere sweeps along candidate lanes of the followed line report
    /// how far each lane is free of walls, solid bodies and loose props. A chord sweep shortens the steering look-ahead when
    /// it cuts through a wall.
    /// </summary>
    internal sealed class AIFeelers
    {
        const int LaneCount = 7;
        const float GroundClearance = 0.25f, KerbHeight = 0.15f;
        const float GroundSearch = 1.5f, VoidDepth = 4f;
        const float MaxLeg = 6f;
        // Walls met at a smaller angle (degrees) still run along a lane. A passage narrowing ahead counts (MHKE Canyon, about 13°).
        const float GrazeAngle = 10f;
        const float KartProbeAbove = 1f, KartProbeDepth = 2f, MaxLift = 2f;
        const float LaneBlendRate = 1.5f, MinLegLength = 0.01f, SideRayShare = 0.8f;
        const float MinAheadScale = 0.3f, AheadScaleStep = 0.35f, MinChord = 3f, MinChordLength = 0.5f;
        const float ChordRadiusShare = 0.8f, ChordTolerance = 0.3f;
        const float SwerveCost = 0.5f, SwitchMargin = 0.5f, LoosePenaltyShare = 0.3f, LoosePenalty = 2f, FreeLaneBonus = 4f;
        static readonly float GrazeSin = Mathf.Sin(GrazeAngle * Mathf.Deg2Rad);

        [NoAutoStaticsCleanup] static readonly RaycastHit[] Hits = new RaycastHit[32];

        readonly AIRouteFollower line;
        readonly float[] clearance = new float[LaneCount];
        readonly float[] looseAhead = new float[LaneCount];
        readonly float[] wallDistance = new float[LaneCount];
        readonly float[] roadEdgeDistance = new float[LaneCount];
        bool valid;
        float laneShift;

        public AIFeelers(AIRouteFollower line) => this.line = line;

        /// <summary>Look-ahead (m) of the last scan. A lane with this clearance is free.</summary>
        public float Range { get; private set; }

        /// <summary>Factor (0.3 to 1) for the steering look-ahead of the last scan: below 1 the chord cut through a wall.</summary>
        public float AheadScale { get; private set; } = 1f;

        /// <summary>
        /// Scans the road lanes 0.1 to 0.9 at the kart, each parallel to the racing line, <paramref name="range"/> metres
        /// ahead from the kart's lane <paramref name="laneU"/>, and the chord to the steering target
        /// <paramref name="steerAhead"/> metres ahead. Dynamic bodies up to <paramref name="looseMass"/> kg are loose props.
        /// </summary>
        public void Scan(Transform kart, float range, float steerAhead, float laneU, float radius, int mask, float looseMass)
        {
            AIObstacleClassifier.Prepare(looseMass);
            if (!GroundProbe.Down(kart.position, KartProbeAbove, KartProbeDepth, mask, out var ground))
            {
                valid = false;
                AheadScale = 1f;
                return;
            }
            var height = radius + GroundClearance;
            var lift = Vector3.up * (Mathf.Clamp(ground.point.y - line.Point(0f, laneU).y, -MaxLift, MaxLift) + height);
            var origin = ground.point + Vector3.up * height;
            Range = range;
            laneShift = line.RoadShift(0f);
            var scan = new ScanContext(origin, lift, height, radius, mask, range, laneU, Mathf.Max(3, Mathf.CeilToInt(range / MaxLeg)),
                line.GapWithin(range + MaxLeg));
            for (var i = 0; i < LaneCount; i++) ScanLane(i, scan);
            AheadScale = ChordScale(scan, steerAhead);
            valid = true;
        }

        /// <summary>
        /// <paramref name="desired"/> when its lane is free, else the free (or freest) lane closest to it. <paramref name="clear"/>
        /// is the distance to the first wall, solid body or road edge in the returned lane (<see cref="Range"/> when none),
        /// <paramref name="obstructed"/> tells that the desired lane had a wall, solid body, edge or loose prop ahead.
        /// </summary>
        public float ChooseLane(float desired, float width, out float clear, out bool obstructed)
        {
            clear = Range;
            obstructed = false;
            if (!valid) return desired;
            var own = Mathf.Min(ClearanceAt(wallDistance, desired), roadEdgeDistance[NearestLane(desired)]);
            var ownLoose = Mathf.Min(ClearanceAt(looseAhead, desired), own);
            clear = own;
            obstructed = ownLoose < Range - 0.01f;
            if (!obstructed) return desired;
            var best = desired;
            var bestScore = Score(own, ownLoose, 0f);
            for (var i = 0; i < LaneCount; i++)
            {
                var u = LaneU(i);
                var score = Score(clearance[i], Mathf.Min(looseAhead[i], clearance[i]), Mathf.Abs(u - desired) * width);
                if (score <= bestScore + SwitchMargin) continue;
                best = u;
                bestScore = score;
                clear = clearance[i];
            }
            return best;
        }

        /// <summary>Distance (m) lane <paramref name="u"/> is free of walls, solid bodies and road edges, <see cref="Range"/> when free or unknown.</summary>
        public float FreeAhead(float u) => valid ? Mathf.Min(ClearanceAt(wallDistance, u), roadEdgeDistance[NearestLane(u)]) : Range;

        void ScanLane(int i, in ScanContext s)
        {
            var u = LaneU(i);
            var from = s.Origin;
            var travelled = 0f;
            float free = s.Range, looseFree = s.Range, onRoad = s.Range;
            for (var k = 1; k <= s.Legs; k++)
            {
                var f = k / (float)s.Legs;
                var to = FollowGround(line.Point(s.Range * f, Mathf.Lerp(s.LaneU, u, Mathf.Min(1f, f * LaneBlendRate))) + s.Lift, s.Height, s.Mask,
                    out var solid);
                var leg = to - from;
                var length = leg.magnitude;
                if (length < MinLegLength) continue;
                var dir = leg / length;
                var hit = Mathf.Min(Sweep(from, dir, length, s.Radius, s.Mask, out var looseHit, true),
                    LowWall(from, dir, length, s.Height, s.Radius, s.Mask));
                if (looseHit < length) looseFree = Mathf.Min(looseFree, travelled + looseHit);
                if (hit < length)
                {
                    free = Mathf.Min(s.Range, travelled + hit);
                    break;
                }
                // Lanes end at the road edge unless the line jumps a gap in the scan.
                if (!solid && !s.GapNear)
                {
                    onRoad = Mathf.Min(s.Range, travelled + length);
                    break;
                }
                travelled += length;
                from = to;
            }
            wallDistance[i] = free;
            roadEdgeDistance[i] = onRoad;
            clearance[i] = Mathf.Min(free, onRoad);
            looseAhead[i] = Mathf.Min(looseFree, free);
        }

        float ChordScale(in ScanContext s, float steerAhead)
        {
            for (var scale = 1f; scale >= MinAheadScale; scale -= AheadScaleStep)
            {
                var target = FollowGround(line.Point(Mathf.Max(MinChord, steerAhead * scale), s.LaneU) + s.Lift, s.Height, s.Mask, out _);
                var chord = target - s.Origin;
                var length = chord.magnitude;
                if (length < MinChordLength || Sweep(s.Origin, chord / length, length, s.Radius * ChordRadiusShare, s.Mask, out _) >= length - ChordTolerance)
                    return scale;
            }
            return MinAheadScale;
        }

        // Kerbs and low walls under the sweep stop a kart dead although the sphere slides over them (MHK3 Rome).
        internal static float LowWall(Vector3 from, Vector3 direction, float length, float height, float radius, int mask)
        {
            var best = length;
            var across = Vector3.Cross(Vector3.up, direction).normalized * (SideRayShare * radius);
            var low = from + Vector3.up * (KerbHeight - height);
            for (var s = -1; s <= 1; s += 2)
            {
                if (!Physics.Raycast(low + across * s, direction, out var hit, best, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Mathf.Abs(hit.normal.y) >= AIObstacleClassifier.SteepNormalY || Vector3.Dot(direction, hit.normal) >= -GrazeSin) continue;
                if (AIObstacleClassifier.IsWall(hit.collider)) best = hit.distance;
            }
            return best;
        }

        // Puts a lifted line point on the ground found near its height, so sweeps don't dive into crests (MHK2 Winter).
        // solid: there is ground down to VoidDepth below and it is no fall zone.
        static Vector3 FollowGround(Vector3 p, float height, int mask, out bool solid)
        {
            solid = false;
            if (!GroundProbe.Down(p + Vector3.up * (GroundSearch - height), GroundSearch + VoidDepth, mask, out var floor)) return p;
            var surface = TrackSurface.Of(floor.collider);
            solid = surface == null || !surface.kind.IsFall();
            if (floor.distance <= 2f * GroundSearch && floor.normal.y >= AIObstacleClassifier.SteepNormalY) p.y = floor.point.y + height;
            return p;
        }

        float Score(float solidFree, float looseFree, float swerve)
        {
            var free = Range - 0.01f;
            return solidFree - swerve * SwerveCost - (looseFree < free ? Range * LoosePenaltyShare + LoosePenalty : 0f) + (looseFree >= free ? FreeLaneBonus : 0f);
        }

        float LaneU(int i) => Mathf.Lerp(AILanes.DriveMin, AILanes.DriveMax, i / (float)(LaneCount - 1)) - laneShift;

        int NearestLane(float u) =>
            Mathf.Clamp(Mathf.RoundToInt((u + laneShift - AILanes.DriveMin) / (AILanes.DriveMax - AILanes.DriveMin) * (LaneCount - 1)), 0, LaneCount - 1);

        float ClearanceAt(float[] lanes, float u)
        {
            var x = (u + laneShift - AILanes.DriveMin) / (AILanes.DriveMax - AILanes.DriveMin) * (LaneCount - 1);
            if (x <= 0f) return lanes[0];
            if (x >= LaneCount - 1) return lanes[LaneCount - 1];
            var i = (int)x;
            return Mathf.Min(lanes[i], lanes[i + 1]);
        }

        // Distance to the first wall or solid body, length when free. loose: distance to the first loose prop before it.
        // ignoreGrazing: walls and bodies met at less than GrazeAngle run along the lane and are not in the way.
        static float Sweep(Vector3 from, Vector3 direction, float length, float radius, int mask, out float loose, bool ignoreGrazing = false)
        {
            var n = Physics.SphereCastNonAlloc(from, radius, direction, Hits, length, mask, QueryTriggerInteraction.Ignore);
            var best = length;
            loose = length;
            for (var i = 0; i < n; i++)
            {
                ref readonly var hit = ref Hits[i];
                if (hit.distance <= 0f) continue;
                var kind = AIObstacleClassifier.Classify(hit);
                if (kind == ObstacleKind.Plain) continue;
                if (kind == ObstacleKind.Loose) loose = Mathf.Min(loose, hit.distance);
                else if (!ignoreGrazing || Vector3.Dot(direction, hit.normal) < -GrazeSin) best = Mathf.Min(best, hit.distance);
            }
            loose = Mathf.Min(loose, best);
            return best;
        }

        readonly struct ScanContext
        {
            public readonly Vector3 Origin, Lift;
            public readonly float Height, Radius, Range, LaneU;
            public readonly int Mask, Legs;
            public readonly bool GapNear;

            public ScanContext(Vector3 origin, Vector3 lift, float height, float radius, int mask, float range, float laneU, int legs, bool gapNear)
            {
                Origin = origin;
                Lift = lift;
                Height = height;
                Radius = radius;
                Mask = mask;
                Range = range;
                LaneU = laneU;
                Legs = legs;
                GapNear = gapNear;
            }
        }
    }
}
