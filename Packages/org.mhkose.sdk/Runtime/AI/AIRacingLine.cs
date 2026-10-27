using System.Collections.Generic;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Racing line of one track line for computer drivers: the line with the evenest curvature within the measured
    /// drivable road (out, in to the apex, out again, using the width), as a lane, bend curvature and ground friction per
    /// distance. Lanes span the road across a smoothed copy of the track line, so the sharp kinks of node polylines
    /// (MHK2, MHK3) neither fold the lanes nor keep the line from cutting a corner. Built once per track and line on
    /// first use.
    /// </summary>
    internal sealed partial class AIRacingLine
    {
        const float Step = 2f;
        // The line keeps to the centre this far (m) before and after a gap (jump over a fall zone).
        const float GapRunIn = 20f, GapRunOut = 15f;
        const float StraightRadius = 1e4f;
        // Bend curvature is measured on the line points this many samples before and after.
        const int CurvatureSpan = 2;

        [AutoStaticsCleanup] static readonly Dictionary<TrackPath, AIRacingLine> Cache = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;
        [AutoStaticsCleanup] static float cacheMargin;

        readonly TrackPath path;
        readonly bool closed;
        readonly float length;
        readonly float sampleSpacing;
        // Racing line and measured road edges (m right of the frame centre): lanes are taken of the interpolated offsets,
        // so where the road's middle or width steps the line stays on the solved points.
        readonly float[] lineOffset;
        readonly float[] curvature;
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

        /// <summary>Road lane (0 = left edge of the drivable road, 0.5 = its middle, 1 = right edge) of the racing line at <paramref name="distance"/>.</summary>
        public float Lane(float distance) => LaneOf(Sample(lineOffset, distance), distance);

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

        /// <summary>Bend radius (m) of the racing line at <paramref name="distance"/> (large on straights).</summary>
        public float Radius(float distance) => 1f / Mathf.Max(1f / StraightRadius, Mathf.Abs(Sample(curvature, distance)));

        /// <summary>Side the racing line bends to at <paramref name="distance"/>: 1 = towards the right road edge, -1 = left, 0 = straight.</summary>
        public int BendSide(float distance)
        {
            var k = Sample(curvature, distance);
            return k > 1f / StraightRadius ? 1 : k < -1f / StraightRadius ? -1 : 0;
        }

        /// <summary>Lateral friction of the ground under the racing line at <paramref name="distance"/> (1 = road).</summary>
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
            path = line;
            closed = line.closed;
            length = line.Length;
            var n = closed ? Mathf.Max(3, Mathf.RoundToInt(length / Step)) : Mathf.Max(2, Mathf.CeilToInt(length / Step) + 1);
            sampleSpacing = closed ? length / n : Step;
            var mask = track.ObstacleMask;
            var road = new Road(n, closed);
            road.Measure(track, line, sampleSpacing, length, margin, mask);
            // Jumps are taken straight down the centre: the take-off ramp and the landing are built around the line.
            road.KeepCentreAroundGaps(Mathf.CeilToInt(GapRunIn / sampleSpacing), Mathf.CeilToInt(GapRunOut / sampleSpacing));
            if (!closed && track.path != null && track.path != line) PinRouteEnds(road, track, line, margin);
            road.Solve();
            road.Edges(margin, out edgeLeft, out edgeRight);
            frameCentre = road.refCentre;
            frameSide = road.refSide;
            frameShift = road.middle;
            frameWidth = road.roadWidth;
            lineOffset = road.offset;
            var points = road.Points();
            friction = Frictions(points, road.height, mask);
            curvature = road.Curvatures(CurvatureSpan);
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
            var n = lineOffset.Length;
            var d = closed ? path.WrapDistance(distance) : Mathf.Clamp(distance, 0f, length);
            var x = d / sampleSpacing;
            i = Mathf.Clamp((int)x, 0, n - 1);
            j = closed ? (i + 1) % n : Mathf.Min(i + 1, n - 1);
            t = x - (int)x;
        }

        Vector3 WorldPoint(float distance) => PointAt(distance, Lane(distance));

        // Alternative routes join the main racing line at both ends.
        static void PinRouteEnds(Road road, TrackRuntime track, TrackPath line, float margin)
        {
            var index = track.alternatePaths.IndexOf(line);
            if (index >= 0)
            {
                var main = Of(track, track.path, margin);
                road.Pin(main.WorldPoint(track.BranchJoinStart(index)), 0);
                road.Pin(main.WorldPoint(track.BranchJoinEnd(index)), road.n - 1);
            }
            road.pinned[0] = road.pinned[road.n - 1] = true;
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
        /// Road samples of a line being built: the track line, a smoothed copy of it (the frame), the drivable road measured
        /// across that frame, and the line solved in it.
        /// </summary>
        sealed class Road
        {
            const int CoarsestStride = 32, MinLevelSamples = 5;
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

            public readonly int n;
            public readonly bool[] pinned;
            public readonly float[] height;
            /// <summary>Smoothed centre and right side direction per sample: the frame lanes span.</summary>
            public readonly Vector2[] refCentre, refSide;
            /// <summary>Middle of the drivable road (m right of the frame centre) and its width per sample.</summary>
            public readonly float[] middle, roadWidth;
            readonly bool closed;
            readonly Vector2[] centre, side;
            /// <summary>Racing line per sample (m right of the frame centre), solved by <see cref="Solve"/>.</summary>
            public readonly float[] offset;
            // Track line (m right of the frame centre) and the bounds the line keeps, across the frame.
            readonly float[] trackLine, width, lo, hi;
            readonly bool[] gap;

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
                offset = new float[n];
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
                for (var i = 0; i < n; i++) offset[i] = Mathf.Clamp(0f, lo[i], hi[i]);
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
                        lo[j] = hi[j] = offset[j] = trackLine[j];
                    }
                }
            }

            public void Pin(Vector3 world, int i)
            {
                offset[i] = Mathf.Clamp(Vector2.Dot(new Vector2(world.x, world.z) - refCentre[i], refSide[i]), lo[i], hi[i]);
                pinned[i] = true;
            }

            // Curvature relaxation (K1999, R. Coulom) on successively finer sample spacings: each sample moves across the
            // road to the curvature interpolated from its neighbours', within the bounds. Bends become arcs that use the
            // width (out, in to the apex, out again) instead of following the track line's kinks.
            public void Solve()
            {
                var levels = new List<int>(n);
                for (var stride = CoarsestStride; stride >= 1; stride >>= 1)
                {
                    LevelIndices(stride, levels);
                    if (levels.Count < MinLevelSamples) continue;
                    var sweeps = Mathf.CeilToInt(SweepsPerStride * Mathf.Sqrt(stride));
                    for (var sweep = 0; sweep < sweeps; sweep++)
                    {
                        var change = 0f;
                        for (var k = 0; k < levels.Count; k++) Adjust(levels, k, ref change);
                        if (change < Converged) break;
                    }
                    if (stride > 1) FillBetween(levels);
                }
            }

            public Vector2[] Points()
            {
                var p = new Vector2[n];
                for (var i = 0; i < n; i++) p[i] = Point(i);
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
            public float[] Curvatures(int span)
            {
                var result = new float[n];
                for (var i = 0; i < n; i++)
                {
                    int a = i - span, b = i + span;
                    if (!closed && (a < 0 || b >= n)) continue;
                    result[i] = SignedCurvature(Point(Index(a)), Point(i), Point(Index(b)), refSide[i]);
                }
                if (closed || n <= 2 * span) return result;
                for (var i = 0; i < span; i++)
                {
                    result[i] = result[span];
                    result[n - 1 - i] = result[n - 1 - span];
                }
                return result;
            }

            Vector2 Point(int i) => refCentre[i] + refSide[i] * offset[i];

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
            void Adjust(List<int> idx, int k, ref float change)
            {
                var m = idx.Count;
                var i = idx[k];
                if (pinned[i] || (!closed && (k == 0 || k == m - 1))) return;
                var before = Point(idx[Wrap(k - 1, m)]);
                var at = Point(i);
                var after = Point(idx[Wrap(k + 1, m)]);
                var bendBefore = !closed && k < 2 ? 0f : Bend(Point(idx[Wrap(k - 2, m)]), before, at);
                var bendAfter = !closed && k > m - 3 ? 0f : Bend(at, after, Point(idx[Wrap(k + 2, m)]));
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
                float low = lo[i] + security, high = hi[i] - security;
                if (low > high) low = high = 0.5f * (lo[i] + hi[i]);
                o = Mathf.Clamp(o, low, high);
                change = Mathf.Max(change, Mathf.Abs(o - offset[i]));
                offset[i] = o;
            }

            void LevelIndices(int stride, List<int> into)
            {
                into.Clear();
                for (var i = 0; i < n; i += stride) into.Add(i);
                if (!closed && into[into.Count - 1] != n - 1) into.Add(n - 1);
            }

            void FillBetween(List<int> idx)
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
                        offset[at] = Mathf.Clamp(Mathf.Lerp(offset[i], offset[j % n], (x - i) / (float)(j - i)), lo[at], hi[at]);
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
