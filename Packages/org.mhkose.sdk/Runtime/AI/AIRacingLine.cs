using System.Collections.Generic;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Racing lines of one track line for computer drivers: line 0 has the evenest curvature within the measured drivable
    /// road (out, in to the apex, out again, using the width); the others (<see cref="LineCount"/>) are the evenest within
    /// a band of the road that sweeps from one side to the other along the lap, each a phase further, so drivers on
    /// different lines spread over the width and still take every bend as an arc. Each as a lane and bend curvature per
    /// distance, with the ground friction of line 0. Lanes span the road across a smoothed copy of the track line, so the
    /// sharp kinks of node polylines (MHK2, MHK3) neither fold the lanes nor keep the line from cutting a corner. The road
    /// is measured once per track and line on first use, each racing line solved on its first use.
    /// </summary>
    internal sealed partial class AIRacingLine
    {
        const float Step = 2f;
        // The line keeps to the centre this far (m) before and after a gap (jump over a fall zone).
        const float GapRunIn = 20f, GapRunOut = 15f;
        const float StraightRadius = 1e4f;
        // Bend curvature is measured on the line points this many samples before and after.
        const int CurvatureSpan = 2;
        // A solved line is swept between its samples for walls the road probes across the line missed (the corner of a
        // building or machine at an apex) and solved again clear of them, at most this often.
        const int ObstacleRounds = 3;
        // Lines 1 and up keep to BandShare of the road between the bounds, the band sweeping across it and back once per
        // BandWavelength (m, rounded to whole waves per lap). Bounds closer than MinBandRoad (m) are not narrowed, nor at a
        // bend of line 0 as tight as TightBendRadius (m): every line has the whole road for its apex there (gentler bends
        // open the band in proportion to their curvature). The narrowing grows by at most BandSlope (m per m) away from
        // such places, so the lines gather on the way into a bend or a narrow passage instead of all at once.
        const float BandShare = 0.55f, BandWavelength = 320f, MinBandRoad = 2.5f, TightBendRadius = 20f, BandSlope = 0.12f;

        /// <summary>Racing lines per track line (<see cref="LineFor"/> shares them out over the drivers).</summary>
        public const int LineCount = 8;

        [AutoStaticsCleanup] static readonly Dictionary<TrackPath, AIRacingLine> Cache = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;
        [AutoStaticsCleanup] static float cacheMargin;

        readonly TrackPath path;
        readonly bool closed;
        readonly float length;
        readonly float sampleSpacing;
        readonly Road road;
        readonly TrackRuntime track;
        readonly float margin;
        // Band phase (radians) at the first sample and per metre along the line.
        readonly float bandPhase, bandPerMetre;
        // Racing lines and measured road edges (m right of the frame centre): lanes are taken of the interpolated offsets,
        // so where the road's middle or width steps a line stays on the solved points. Lines are solved on first use.
        readonly float[][] lineOffset = new float[LineCount][];
        readonly float[][] curvature = new float[LineCount][];
        // Share of the band squeeze applied per sample (1 on straights, 0 around tight bends), from line 0.
        float[] bandSqueeze;
        readonly float[] friction;
        readonly float[] edgeLeft, edgeRight;
        readonly float[] frameShift, frameWidth;
        readonly Vector2[] frameCentre, frameSide;

        /// <summary>The racing line of <paramref name="line"/> on <paramref name="track"/> (built on first use).</summary>
        public static AIRacingLine Of(TrackRuntime track, TrackPath line, float margin)
        {
            if (cacheTrack != track || cacheMargin != margin)
            {
                Cache.Clear();
                cacheTrack = track;
                cacheMargin = margin;
            }
            if (Cache.TryGetValue(line, out var result)) return result;
            result = new AIRacingLine(track, line, margin);
            Cache[line] = result;
            return result;
        }

        /// <summary>Solves racing line <paramref name="line"/> now if it is not yet (drivers call this while the track loads).</summary>
        public void Prepare(int line) => Offsets(line);

        /// <summary>Racing line of the driver with racer index <paramref name="racer"/>: drivers next to each other in the grid get different lines.</summary>
        public static int LineFor(int racer) => (racer % LineCount + LineCount) % LineCount;

        /// <summary>
        /// Road lane (0 = left edge of the drivable road, 0.5 = its middle, 1 = right edge) of racing line
        /// <paramref name="line"/> at <paramref name="distance"/>.
        /// </summary>
        public float Lane(float distance, int line) => LaneOf(Sample(Offsets(line), distance), distance);

        /// <summary>
        /// Width (m) of the drivable road at <paramref name="distance"/>, which road lanes span: the track line's width cut
        /// short at walls, drops and offroad ground on each side (where measured), so a track line much wider or off-centre
        /// than its road does not spread the lanes beyond it.
        /// </summary>
        public float Width(float distance) => Sample(frameWidth, distance);

        /// <summary>World point at road lane <paramref name="roadLane"/> at <paramref name="distance"/>.</summary>
        public Vector3 PointAt(float distance, float roadLane)
        {
            Frame(distance, out var centre, out var side);
            var p = centre + side * (Sample(frameShift, distance) + (roadLane - AILanes.Centre) * Width(distance));
            return new Vector3(p.x, path.GetPoint(distance).y, p.y);
        }

        /// <summary>Road lane (unclamped) of <paramref name="position"/> beside <paramref name="distance"/>.</summary>
        public float RoadLaneOf(Vector3 position, float distance)
        {
            Frame(distance, out var centre, out var side);
            return AILanes.Centre + (Vector2.Dot(new Vector2(position.x, position.z) - centre, side) - Sample(frameShift, distance)) / Width(distance);
        }

        /// <summary>Bend radius (m) of racing line <paramref name="line"/> at <paramref name="distance"/> (large on straights).</summary>
        public float Radius(float distance, int line) => 1f / Mathf.Max(1f / StraightRadius, Mathf.Abs(Sample(Curvatures(line), distance)));

        /// <summary>
        /// Side racing line <paramref name="line"/> bends to at <paramref name="distance"/>: 1 = towards the right road
        /// edge, -1 = left, 0 = straight.
        /// </summary>
        public int BendSide(float distance, int line)
        {
            var k = Sample(Curvatures(line), distance);
            return k > 1f / StraightRadius ? 1 : k < -1f / StraightRadius ? -1 : 0;
        }

        /// <summary>Lateral friction of the ground under racing line 0 at <paramref name="distance"/> (1 = road).</summary>
        public float Friction(float distance) => Sample(friction, distance);

        /// <summary>
        /// Road lanes of the left and right edge of the drivable road at <paramref name="distance"/>: walls, drops and offroad
        /// ground cut the track line's width, and near gaps only the centre band counts.
        /// </summary>
        public void RoadEdges(float distance, out float left, out float right)
        {
            left = LaneOf(Sample(edgeLeft, distance), distance);
            right = LaneOf(Sample(edgeRight, distance), distance);
        }

        // Road lane of a point offset (m right of the frame centre) across the frame at distance.
        float LaneOf(float offset, float distance) => AILanes.Centre + (offset - Sample(frameShift, distance)) / Width(distance);

        AIRacingLine(TrackRuntime track, TrackPath line, float margin)
        {
            this.track = track;
            this.margin = margin;
            path = line;
            closed = line.closed;
            length = line.Length;
            var n = closed ? Mathf.Max(3, Mathf.RoundToInt(length / Step)) : Mathf.Max(2, Mathf.CeilToInt(length / Step) + 1);
            sampleSpacing = closed ? length / n : Step;
            var mask = track.ObstacleMask;
            road = new Road(n, closed);
            road.Measure(track, line, sampleSpacing, length, margin, mask);
            // Jumps are taken straight down the centre: the take-off ramp and the landing are built around the line.
            road.KeepCentreAroundGaps(Mathf.CeilToInt(GapRunIn / sampleSpacing), Mathf.CeilToInt(GapRunOut / sampleSpacing));
            if (!closed && track.path != null && track.path != line) road.pinned[0] = road.pinned[n - 1] = true;
            BandOf(track, line, out bandPhase, out bandPerMetre);
            road.Edges(margin, out edgeLeft, out edgeRight);
            frameCentre = road.refCentre;
            frameSide = road.refSide;
            frameShift = road.middle;
            frameWidth = road.roadWidth;
            friction = Frictions(road.Points(Offsets(0)), road.height, mask);
        }

        // Main line: whole waves per lap (an open line: per BandWavelength). A route continues the main line's band from
        // where it forks, so its lines leave and join the main line's on the same side.
        static void BandOf(TrackRuntime track, TrackPath line, out float phase, out float perMetre)
        {
            var main = track.path != null ? track.path : line;
            var waves = main.closed ? Mathf.Max(1f, Mathf.Round(main.Length / BandWavelength)) : main.Length / BandWavelength;
            perMetre = 2f * Mathf.PI * waves / Mathf.Max(1f, main.Length);
            var index = line == main ? -1 : track.alternatePaths.IndexOf(line);
            phase = index >= 0 ? track.BranchJoinStart(index) * perMetre : 0f;
        }

        float[] Offsets(int line)
        {
            line = LineFor(line);
            if (lineOffset[line] == null) SolveLine(line);
            return lineOffset[line];
        }

        float[] Curvatures(int line)
        {
            line = LineFor(line);
            if (curvature[line] == null) SolveLine(line);
            return curvature[line];
        }

        // Line 0 within the bounds, the others within their band, starting from line 0 (a warm start: it is solved first).
        void SolveLine(int line)
        {
            var low = road.Low;
            var high = road.High;
            var start = road.Start;
            if (line > 0)
            {
                bandSqueeze ??= BandSqueeze(Curvatures(0));
                road.Band(bandPhase + 2f * Mathf.PI * (line - 1) / (LineCount - 1), bandPerMetre * sampleSpacing, bandSqueeze, BandSlope * sampleSpacing,
                    out low, out high);
                start = Offsets(0);
            }
            var offset = new float[road.n];
            for (var i = 0; i < road.n; i++) offset[i] = Mathf.Clamp(start[i], low[i], high[i]);
            if (!closed && track.path != null && track.path != path) PinRouteEnds(offset, low, high, line);
            road.Solve(offset, low, high, Road.CoarsestStride);
            for (var round = 0; round < ObstacleRounds && road.AvoidObstacles(offset, low, high, margin, track.ObstacleMask); round++)
                road.Solve(offset, low, high, Road.RefineStride);
            lineOffset[line] = offset;
            curvature[line] = road.Curvatures(offset, CurvatureSpan);
        }

        static float[] BandSqueeze(float[] bend)
        {
            var result = new float[bend.Length];
            for (var i = 0; i < bend.Length; i++) result[i] = 1f - Mathf.Clamp01(TightBendRadius * Mathf.Abs(bend[i]));
            return result;
        }

        float Sample(float[] values, float distance)
        {
            Locate(distance, out var i, out var j, out var t);
            return Mathf.Lerp(values[i], values[j], t);
        }

        // Centre and right side direction of the smoothed frame lanes span at distance.
        void Frame(float distance, out Vector2 centre, out Vector2 side)
        {
            Locate(distance, out var i, out var j, out var t);
            centre = Vector2.Lerp(frameCentre[i], frameCentre[j], t);
            side = Vector2.Lerp(frameSide[i], frameSide[j], t).normalized;
        }

        // Samples around distance and the share of the way from i to j.
        void Locate(float distance, out int i, out int j, out float t)
        {
            var n = road.n;
            var d = closed ? path.WrapDistance(distance) : Mathf.Clamp(distance, 0f, length);
            var x = d / sampleSpacing;
            i = Mathf.Clamp((int)x, 0, n - 1);
            j = closed ? (i + 1) % n : Mathf.Min(i + 1, n - 1);
            t = x - (int)x;
        }

        Vector3 WorldPoint(float distance, int line) => PointAt(distance, Lane(distance, line));

        // Alternative routes join the same racing line of the main line at both ends.
        void PinRouteEnds(float[] offset, float[] low, float[] high, int line)
        {
            var index = track.alternatePaths.IndexOf(path);
            if (index < 0) return;
            var main = Of(track, track.path, margin);
            road.Pin(main.WorldPoint(track.BranchJoinStart(index), line), 0, offset, low, high);
            road.Pin(main.WorldPoint(track.BranchJoinEnd(index), line), road.n - 1, offset, low, high);
        }

        static float[] Frictions(Vector2[] points, float[] height, int mask)
        {
            var result = new float[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var surface = AIRoadProbe.Ground(new Vector3(p.x, height[i], p.y), height[i], mask, out var hit) ? TrackSurface.Of(hit.collider) : null;
                result[i] = surface != null ? surface.lateralFriction : 1f;
            }
            return result;
        }

        /// <summary>
        /// Road samples of a line: the track line, a smoothed copy of it (the frame), the drivable road measured across that
        /// frame, and the bounds racing lines are solved in.
        /// </summary>
        sealed class Road
        {
            /// <summary>Sample stride of the coarsest solver level, and of the first one when a solved line is refined.</summary>
            public const int CoarsestStride = 32, RefineStride = 4;
            const int MinLevelSamples = 5;
            // Sweeps per level: this times the square root of the level's stride.
            const float SweepsPerStride = 100f;
            const float Converged = 1e-4f;
            // The frame is the track line smoothed over ±SmoothSamples samples (Gaussian, SmoothSigma samples), so the
            // sharp kinks of 10 m node polylines (MHK2, MHK3) neither bend the line nor fold the frame.
            const int SmoothSamples = 6;
            const float SmoothSigma = 2f;
            // Bounds stop this far (m) short of where the side lines of neighbouring samples cross (inside bends tighter
            // than the road is wide), so lanes map onto the road without folding back.
            const float FoldMargin = 1f;
            // Coarse levels keep the spacing before times the spacing after over this (m) from the bounds.
            const float SecurityScale = 800f;
            const float NewtonStep = 1e-3f;
            // Measured roads narrower than MinMeasuredWidth (m) count as unmeasured (the probes found no ground at the
            // centre); lanes span at least MinRoadWidth.
            const float MinMeasuredWidth = 1f, MinRoadWidth = 2f;
            // The obstacle sweep: a sphere of ObstacleRadius (m) ObstacleHeight (m) above the track line's height.
            const float ObstacleRadius = 0.4f, ObstacleHeight = 0.7f;

            public readonly int n;
            public readonly bool[] pinned;
            public readonly float[] height;
            /// <summary>Smoothed centre and right side direction per sample: the frame lanes span.</summary>
            public readonly Vector2[] refCentre, refSide;
            /// <summary>Middle of the drivable road (m right of the frame centre) and its width per sample.</summary>
            public readonly float[] middle, roadWidth;
            readonly bool closed;
            readonly Vector2[] centre, side;
            // Track line (m right of the frame centre), the bounds racing lines keep and the start of the solve, across the frame.
            readonly float[] trackLine, width, lo, hi, start;
            readonly bool[] gap;

            /// <summary>Bounds (m right of the frame centre) line 0 keeps per sample.</summary>
            public float[] Low => lo;
            public float[] High => hi;
            /// <summary>Where line 0's solve starts per sample (in the bounds, the track line around gaps).</summary>
            public float[] Start => start;

            public Road(int n, bool closed)
            {
                this.n = n;
                this.closed = closed;
                centre = new Vector2[n];
                side = new Vector2[n];
                refCentre = new Vector2[n];
                refSide = new Vector2[n];
                trackLine = new float[n];
                width = new float[n];
                middle = new float[n];
                roadWidth = new float[n];
                height = new float[n];
                lo = new float[n];
                hi = new float[n];
                gap = new bool[n];
                start = new float[n];
                pinned = new bool[n];
            }

            /// <summary>
            /// Samples the track line, smooths it into the frame and probes the drivable road from each track line point
            /// along the frame's side direction: walls, drops, offroad ground and holes cut the track line's width short.
            /// The bounds keep <paramref name="margin"/> from the edges (the middle of roads narrower than that) and stop
            /// short of folds.
            /// </summary>
            public void Measure(TrackRuntime track, TrackPath line, float spacing, float length, float margin, int mask)
            {
                for (var i = 0; i < n; i++)
                {
                    var d = Mathf.Min(i * spacing, length);
                    var c = line.GetPoint(d);
                    var right = TrackPath.RightOf(line.GetTangent(d));
                    side[i] = new Vector2(right.x, right.z);
                    centre[i] = new Vector2(c.x, c.z);
                    height[i] = c.y;
                    width[i] = Mathf.Max(1f, line.GetWidth(d));
                    gap[i] = TrackGaps.IsGap(track, line, d);
                }
                SmoothCentre();
                for (var i = 0; i < n; i++)
                {
                    var tangent = refCentre[Index(i + 1)] - refCentre[Index(i - 1)];
                    var right = tangent.sqrMagnitude > 1e-8f ? new Vector2(tangent.y, -tangent.x).normalized : side[i];
                    refSide[i] = Vector2.Dot(right, side[i]) < 0f ? -right : right;
                    var s = trackLine[i] = Vector2.Dot(centre[i] - refCentre[i], refSide[i]);
                    middle[i] = lo[i] = hi[i] = s;
                    roadWidth[i] = width[i];
                    if (width[i] * 0.5f - margin <= 0f || gap[i]) continue;
                    var c = new Vector3(centre[i].x, height[i], centre[i].y);
                    var across = new Vector3(refSide[i].x, 0f, refSide[i].y);
                    var reachLeft = AIRoadProbe.Reach(c, -across, width[i] * 0.5f, mask);
                    var reachRight = AIRoadProbe.Reach(c, across, width[i] * 0.5f, mask);
                    lo[i] = s - reachLeft + margin;
                    hi[i] = s + reachRight - margin;
                    if (lo[i] > hi[i]) lo[i] = hi[i] = s + 0.5f * (reachRight - reachLeft);
                    if (reachLeft + reachRight < MinMeasuredWidth) continue;
                    middle[i] = s + 0.5f * (reachRight - reachLeft);
                    roadWidth[i] = Mathf.Max(MinRoadWidth, reachLeft + reachRight);
                }
                LimitFolds();
                for (var i = 0; i < n; i++) start[i] = Mathf.Clamp(0f, lo[i], hi[i]);
            }

            public void KeepCentreAroundGaps(int before, int after)
            {
                for (var i = 0; i < n; i++)
                {
                    if (!gap[i]) continue;
                    for (var k = -before; k <= after; k++)
                    {
                        var j = i + k;
                        if (closed) j = (j % n + n) % n;
                        else if (j < 0 || j >= n) continue;
                        lo[j] = hi[j] = start[j] = trackLine[j];
                    }
                }
            }

            /// <summary>Pins sample <paramref name="i"/> of a line to <paramref name="world"/>, within that line's bounds.</summary>
            public void Pin(Vector3 world, int i, float[] offset, float[] low, float[] high) =>
                offset[i] = Mathf.Clamp(Vector2.Dot(new Vector2(world.x, world.z) - refCentre[i], refSide[i]), low[i], high[i]);

            /// <summary>
            /// Bounds of a banded line: <see cref="BandShare"/> of line 0's bounds (narrowed by <paramref name="squeeze"/>
            /// of the rest per sample, and by at most <paramref name="slope"/> m more per sample away from where it is not
            /// narrowed), at the left end of them where sin(<paramref name="phase"/> + i · <paramref name="perSample"/>)
            /// is -1, at the right end where it is 1.
            /// </summary>
            public void Band(float phase, float perSample, float[] squeeze, float slope, out float[] low, out float[] high)
            {
                var cut = new float[n];
                for (var i = 0; i < n; i++)
                {
                    var room = hi[i] - lo[i];
                    cut[i] = room < MinBandRoad ? 0f : (1f - BandShare) * room * squeeze[i];
                }
                // A sweep each way (twice round a closed line) keeps every cut within slope per sample of its neighbours'.
                for (var pass = 0; pass < (closed ? 2 : 1); pass++)
                {
                    for (var i = 1; i < n + (closed ? 1 : 0); i++) cut[i % n] = Mathf.Min(cut[i % n], cut[i - 1] + slope);
                    for (var i = n - 2; i >= (closed ? -1 : 0); i--) cut[Index(i)] = Mathf.Min(cut[Index(i)], cut[Index(i + 1)] + slope);
                }
                low = new float[n];
                high = new float[n];
                for (var i = 0; i < n; i++)
                {
                    var shift = cut[i] * 0.5f * (1f + Mathf.Sin(phase + i * perSample));
                    low[i] = lo[i] + shift;
                    high[i] = hi[i] - (cut[i] - shift);
                }
            }

            /// <summary>
            /// Sweeps the line between neighbouring samples at kart height. Where it meets a wall or steep static scenery
            /// the bound on that side moves in at both samples to <paramref name="margin"/> from the hit (the line's own
            /// bounds only: line 0's are every line's). True when a bound moved.
            /// </summary>
            public bool AvoidObstacles(float[] offset, float[] low, float[] high, float margin, int mask)
            {
                var moved = false;
                var last = closed ? n : n - 1;
                for (var i = 0; i < last; i++)
                {
                    var j = Index(i + 1);
                    if (gap[i] || gap[j]) continue;
                    var a = Point(i, offset);
                    var b = Point(j, offset);
                    var from = new Vector3(a.x, height[i] + ObstacleHeight, a.y);
                    var chord = new Vector3(b.x, height[j] + ObstacleHeight, b.y) - from;
                    var length = chord.magnitude;
                    if (length < 1e-3f || !Physics.SphereCast(from, ObstacleRadius, chord / length, out var hit, length, mask, QueryTriggerInteraction.Ignore))
                        continue;
                    if (hit.collider.attachedRigidbody != null || (!AIObstacleClassifier.IsWall(hit.collider) && Mathf.Abs(hit.normal.y) >= AIObstacleClassifier.SteepNormalY))
                        continue;
                    var at = new Vector2(hit.point.x, hit.point.z);
                    moved |= Keep(i, at, offset, low, high, margin) | Keep(j, at, offset, low, high, margin);
                }
                return moved;
            }

            // Moves sample i's bound on the side of the obstacle at `at` to margin from it; false when it already was.
            bool Keep(int i, Vector2 at, float[] offset, float[] low, float[] high, float margin)
            {
                if (pinned[i]) return false;
                var across = Vector2.Dot(at - refCentre[i], refSide[i]);
                if (across >= offset[i])
                {
                    var bound = Mathf.Max(low[i], across - margin);
                    if (bound >= high[i]) return false;
                    high[i] = bound;
                }
                else
                {
                    var bound = Mathf.Min(high[i], across + margin);
                    if (bound <= low[i]) return false;
                    low[i] = bound;
                }
                offset[i] = Mathf.Clamp(offset[i], low[i], high[i]);
                return true;
            }

            // Curvature relaxation (K1999, R. Coulom) on successively finer sample spacings from stride coarsest: each sample
            // of the line moves across the road to the curvature interpolated from its neighbours', within the bounds. Bends
            // become arcs that use the width (out, in to the apex, out again) instead of following the track line's kinks.
            public void Solve(float[] offset, float[] low, float[] high, int coarsest)
            {
                var levels = new List<int>(n);
                for (var stride = coarsest; stride >= 1; stride >>= 1)
                {
                    LevelIndices(stride, levels);
                    if (levels.Count < MinLevelSamples) continue;
                    var sweeps = Mathf.CeilToInt(SweepsPerStride * Mathf.Sqrt(stride));
                    for (var sweep = 0; sweep < sweeps; sweep++)
                    {
                        var change = 0f;
                        for (var k = 0; k < levels.Count; k++) Adjust(levels, k, offset, low, high, ref change);
                        if (change < Converged) break;
                    }
                    if (stride > 1) FillBetween(levels, offset, low, high);
                }
            }

            public Vector2[] Points(float[] offset)
            {
                var p = new Vector2[n];
                for (var i = 0; i < n; i++) p[i] = Point(i, offset);
                return p;
            }

            /// <summary>Measured road edges (m right of the frame centre): the bounds the line keeps plus its margin.</summary>
            public void Edges(float margin, out float[] left, out float[] right)
            {
                left = new float[n];
                right = new float[n];
                for (var i = 0; i < n; i++)
                {
                    left[i] = lo[i] - margin;
                    right[i] = hi[i] + margin;
                }
            }

            /// <summary>
            /// Signed bend curvature (1/m) of the line through the points <paramref name="span"/> samples before and after
            /// each: positive when it bends towards the right road edge.
            /// </summary>
            public float[] Curvatures(float[] offset, int span)
            {
                var result = new float[n];
                for (var i = 0; i < n; i++)
                {
                    int a = i - span, b = i + span;
                    if (!closed && (a < 0 || b >= n)) continue;
                    result[i] = SignedCurvature(Point(Index(a), offset), Point(i, offset), Point(Index(b), offset), refSide[i]);
                }
                if (closed || n <= 2 * span) return result;
                for (var i = 0; i < span; i++)
                {
                    result[i] = result[span];
                    result[n - 1 - i] = result[n - 1 - span];
                }
                return result;
            }

            Vector2 Point(int i, float[] offset) => refCentre[i] + refSide[i] * offset[i];

            int Index(int i) => closed ? (i % n + n) % n : Mathf.Clamp(i, 0, n - 1);

            // In a bend tighter than the road is wide the side lines of neighbouring samples cross on its inside: lanes
            // beyond that point would map back along the road.
            void LimitFolds()
            {
                for (var i = 0; i < n; i++)
                {
                    for (var step = -1; step <= 1; step += 2)
                    {
                        var j = i + step;
                        if (closed) j = (j % n + n) % n;
                        else if (j < 0 || j >= n) continue;
                        var turn = Cross(refSide[i], refSide[j]);
                        if (Mathf.Abs(turn) < 1e-6f) continue;
                        var at = Cross(refCentre[j] - refCentre[i], refSide[j]) / turn;
                        if (at > 0f) hi[i] = Mathf.Min(hi[i], at - FoldMargin);
                        else lo[i] = Mathf.Max(lo[i], at + FoldMargin);
                    }
                    if (lo[i] > hi[i]) lo[i] = hi[i] = 0.5f * (lo[i] + hi[i]);
                }
            }

            void SmoothCentre()
            {
                var weights = 0f;
                for (var k = -SmoothSamples; k <= SmoothSamples; k++) weights += Weight(k);
                for (var i = 0; i < n; i++)
                {
                    var sum = Vector2.zero;
                    for (var k = -SmoothSamples; k <= SmoothSamples; k++) sum += centre[Index(i + k)] * Weight(k);
                    refCentre[i] = sum / weights;
                }
            }

            static float Weight(int k) => Mathf.Exp(-0.5f * (k / SmoothSigma) * (k / SmoothSigma));

            // Sample k of the level: on the chord of its neighbours, then a Newton step to the curvature interpolated from
            // theirs (the ends of an open line count as straight).
            void Adjust(List<int> idx, int k, float[] offset, float[] low, float[] high, ref float change)
            {
                var m = idx.Count;
                var i = idx[k];
                if (pinned[i] || (!closed && (k == 0 || k == m - 1))) return;
                var before = Point(idx[Wrap(k - 1, m)], offset);
                var at = Point(i, offset);
                var after = Point(idx[Wrap(k + 1, m)], offset);
                var bendBefore = !closed && k < 2 ? 0f : Bend(Point(idx[Wrap(k - 2, m)], offset), before, at);
                var bendAfter = !closed && k > m - 3 ? 0f : Bend(at, after, Point(idx[Wrap(k + 2, m)], offset));
                var lengthBefore = (at - before).magnitude;
                var lengthAfter = (after - at).magnitude;
                var target = (lengthAfter * bendBefore + lengthBefore * bendAfter) / Mathf.Max(1e-6f, lengthBefore + lengthAfter);
                var chord = after - before;
                var across = Cross(chord, refSide[i]);
                if (Mathf.Abs(across) < 1e-6f) return;
                var o = -Cross(chord, refCentre[i] - before) / across;
                var slope = Bend(before, refCentre[i] + refSide[i] * (o + NewtonStep), after);
                if (Mathf.Abs(slope) > 1e-9f) o += NewtonStep / slope * target;
                var security = lengthBefore * lengthAfter / SecurityScale;
                float from = low[i] + security, to = high[i] - security;
                if (from > to) from = to = 0.5f * (low[i] + high[i]);
                o = Mathf.Clamp(o, from, to);
                change = Mathf.Max(change, Mathf.Abs(o - offset[i]));
                offset[i] = o;
            }

            void LevelIndices(int stride, List<int> into)
            {
                into.Clear();
                for (var i = 0; i < n; i += stride) into.Add(i);
                if (!closed && into[into.Count - 1] != n - 1) into.Add(n - 1);
            }

            void FillBetween(List<int> idx, float[] offset, float[] low, float[] high)
            {
                var m = idx.Count;
                for (var k = 0; k < m; k++)
                {
                    var i = idx[k];
                    var j = k + 1 < m ? idx[k + 1] : closed ? idx[0] + n : -1;
                    if (j < 0) continue;
                    for (var x = i + 1; x < j; x++)
                    {
                        var at = x % n;
                        if (pinned[at]) continue;
                        offset[at] = Mathf.Clamp(Mathf.Lerp(offset[i], offset[j % n], (x - i) / (float)(j - i)), low[at], high[at]);
                    }
                }
            }

            int Wrap(int k, int m) => closed ? (k % m + m) % m : Mathf.Clamp(k, 0, m - 1);

            static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

            // Signed curvature (1/m) of the circle through three points: the sign tells which way it turns, consistently
            // for the solver's neighbours and its Newton step.
            static float Bend(Vector2 prev, Vector2 at, Vector2 next)
            {
                var toNext = next - at;
                var toPrev = prev - at;
                var chord = next - prev;
                var product = Mathf.Sqrt(toNext.sqrMagnitude * toPrev.sqrMagnitude * chord.sqrMagnitude);
                return product > 1e-12f ? 2f * Cross(toNext, toPrev) / product : 0f;
            }

            // Curvature of the circle through a, b and c, positive when it bends towards right (the chord's middle lies
            // on that side of b).
            static float SignedCurvature(Vector2 a, Vector2 b, Vector2 c, Vector2 right)
            {
                var k = Mathf.Abs(Bend(a, b, c));
                return Vector2.Dot(0.5f * (a + c) - b, right) >= 0f ? k : -k;
            }
        }
    }
}
