using System.Collections.Generic;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Racing line of one track line for computer drivers: the smoothest path within the measured drivable road, as a
    /// lane, bend radius and ground friction per distance. Built once per track and line on first use.
    /// </summary>
    internal sealed partial class AIRacingLine
    {
        const float Step = 2f;
        // The line keeps to the centre this far (m) before and after a gap (jump over a fall zone).
        const float GapRunIn = 20f, GapRunOut = 15f;
        const float StraightRadius = 1e4f;
        const int RadiusSpan = 2;

        [AutoStaticsCleanup] static readonly Dictionary<TrackPath, AIRacingLine> Cache = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;
        [AutoStaticsCleanup] static float cacheMargin;

        readonly TrackPath path;
        readonly bool closed;
        readonly float length;
        readonly float sampleSpacing;
        readonly float[] lane;
        readonly float[] radius;
        readonly float[] friction;
        readonly float[] roadLeft, roadRight;

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

        /// <summary>Road lane (0 = left edge, 0.5 = centre, 1 = right edge) of the racing line at <paramref name="distance"/>.</summary>
        public float Lane(float distance) => Sample(lane, distance);

        /// <summary>Bend radius (m) of the racing line at <paramref name="distance"/> (large on straights).</summary>
        public float Radius(float distance) => Sample(radius, distance);

        /// <summary>Lateral friction of the ground under the racing line at <paramref name="distance"/> (1 = road).</summary>
        public float Friction(float distance) => Sample(friction, distance);

        /// <summary>
        /// Road lanes of the left and right edge of the drivable road at <paramref name="distance"/>: walls, drops and offroad
        /// ground cut the track line's width, and near gaps only the centre band counts.
        /// </summary>
        public void RoadEdges(float distance, out float left, out float right)
        {
            left = Sample(roadLeft, distance);
            right = Sample(roadRight, distance);
        }

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
            road.Edges(margin, out roadLeft, out roadRight);
            var points = road.Points();
            lane = road.Lanes();
            friction = Frictions(points, road.height, mask);
            radius = Radii(points, closed);
        }

        float Sample(float[] values, float distance)
        {
            var n = values.Length;
            if (n == 1) return values[0];
            var d = closed ? path.WrapDistance(distance) : Mathf.Clamp(distance, 0f, length);
            var x = d / sampleSpacing;
            var i = Mathf.Clamp((int)x, 0, n - 1);
            var j = closed ? (i + 1) % n : Mathf.Min(i + 1, n - 1);
            return Mathf.Lerp(values[i], values[j], x - (int)x);
        }

        Vector3 WorldPoint(float distance) => path.GetPointAtLane(distance, Lane(distance), Mathf.Max(1f, path.GetWidth(distance)));

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

        static float[] Radii(Vector2[] p, bool closed)
        {
            var n = p.Length;
            var result = new float[n];
            for (var i = 0; i < n; i++)
            {
                int a = i - RadiusSpan, b = i + RadiusSpan;
                if (closed) { a = (a + n) % n; b %= n; }
                else if (a < 0 || b >= n) { result[i] = StraightRadius; continue; }
                result[i] = Circumradius(p[a], p[i], p[b]);
            }
            if (!closed)
            {
                for (var i = 0; i < Mathf.Min(2, n); i++) result[i] = result[Mathf.Min(2, n - 1)];
                for (var i = Mathf.Max(0, n - 2); i < n; i++) result[i] = result[Mathf.Max(0, n - 3)];
            }
            return result;
        }

        static float Circumradius(Vector2 a, Vector2 b, Vector2 c)
        {
            var ab = b - a;
            var bc = c - b;
            var ca = a - c;
            var cross = Mathf.Abs(ab.x * bc.y - ab.y * bc.x);
            if (cross < 1e-6f) return StraightRadius;
            return Mathf.Min(StraightRadius, ab.magnitude * bc.magnitude * ca.magnitude / (2f * cross));
        }

        /// <summary>Road samples of a line being built: centre, side, measured bounds and the solved offsets.</summary>
        sealed class Road
        {
            const int CoarsestStride = 8, MinLevelSamples = 5;
            const int CoarseSweeps = 600, FineSweeps = 300;
            const float Converged = 1e-3f;

            public readonly int n;
            public readonly bool[] pinned;
            public readonly float[] height;
            readonly bool closed;
            readonly Vector2[] centre, side;
            readonly float[] width, lo, hi, offset;
            readonly bool[] gap;

            public Road(int n, bool closed)
            {
                this.n = n;
                this.closed = closed;
                centre = new Vector2[n];
                side = new Vector2[n];
                width = new float[n];
                height = new float[n];
                lo = new float[n];
                hi = new float[n];
                gap = new bool[n];
                offset = new float[n];
                pinned = new bool[n];
            }

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
                    var half = width[i] * 0.5f - margin;
                    if (half <= 0f || gap[i]) continue;
                    lo[i] = -Mathf.Max(0f, AIRoadProbe.Reach(c, -right, width[i] * 0.5f, mask) - margin);
                    hi[i] = Mathf.Max(0f, AIRoadProbe.Reach(c, right, width[i] * 0.5f, mask) - margin);
                }
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
                        lo[j] = hi[j] = 0f;
                    }
                }
            }

            public void Pin(Vector3 world, int i)
            {
                offset[i] = Mathf.Clamp(Vector2.Dot(new Vector2(world.x, world.z) - centre[i], side[i]), lo[i], hi[i]);
                pinned[i] = true;
            }

            // Offsets within [lo, hi] minimising the sum of squared second differences, by projected Gauss-Seidel sweeps
            // of the biharmonic update on successively finer sample spacings.
            public void Solve()
            {
                var levels = new List<int>(n);
                for (var stride = CoarsestStride; stride >= 1; stride >>= 1)
                {
                    LevelIndices(stride, levels);
                    if (levels.Count < MinLevelSamples) continue;
                    RelaxLevel(levels, stride >= CoarsestStride ? CoarseSweeps : FineSweeps);
                    if (stride > 1) FillBetween(levels);
                }
            }

            public Vector2[] Points()
            {
                var p = new Vector2[n];
                for (var i = 0; i < n; i++) p[i] = Point(i);
                return p;
            }

            public float[] Lanes()
            {
                var result = new float[n];
                for (var i = 0; i < n; i++) result[i] = AILanes.Centre + offset[i] / width[i];
                return result;
            }

            /// <summary>Road lanes of the measured edges: the bounds the line keeps plus its margin.</summary>
            public void Edges(float margin, out float[] left, out float[] right)
            {
                left = new float[n];
                right = new float[n];
                for (var i = 0; i < n; i++)
                {
                    left[i] = AILanes.Centre + (lo[i] - margin) / width[i];
                    right[i] = AILanes.Centre + (hi[i] + margin) / width[i];
                }
            }

            Vector2 Point(int i) => centre[i] + side[i] * offset[i];

            void LevelIndices(int stride, List<int> into)
            {
                into.Clear();
                for (var i = 0; i < n; i += stride) into.Add(i);
                if (!closed && into[into.Count - 1] != n - 1) into.Add(n - 1);
            }

            void RelaxLevel(List<int> idx, int sweeps)
            {
                for (var sweep = 0; sweep < sweeps; sweep++)
                {
                    var change = 0f;
                    for (var k = 0; k < idx.Count; k++) RelaxSample(idx, k, ref change);
                    if (change < Converged) break;
                }
            }

            void RelaxSample(List<int> idx, int k, ref float change)
            {
                var m = idx.Count;
                var i = idx[k];
                if (pinned[i]) return;
                if (!closed && (k < 2 || k > m - 3))
                {
                    // Next to the ends of an open line: the midpoint of the neighbours (a straight run-in).
                    if (k == 0 || k == m - 1) return;
                    Relax(i, 0.5f * (Point(idx[k - 1]) + Point(idx[k + 1])), ref change);
                    return;
                }
                int a = idx[Wrap(k - 2, m)], b = idx[Wrap(k - 1, m)], c = idx[Wrap(k + 1, m)], d = idx[Wrap(k + 2, m)];
                Relax(i, (4f * (Point(b) + Point(c)) - Point(a) - Point(d)) / 6f, ref change);
            }

            void Relax(int i, Vector2 target, ref float change)
            {
                var o = Mathf.Clamp(Vector2.Dot(target - centre[i], side[i]), lo[i], hi[i]);
                change = Mathf.Max(change, Mathf.Abs(o - offset[i]));
                offset[i] = o;
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
        }
    }
}
