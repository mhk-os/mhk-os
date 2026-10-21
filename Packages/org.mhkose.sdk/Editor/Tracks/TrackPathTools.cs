using System.Collections.Generic;
using System.Linq;
using MHK.Race;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Racing-line tools of the inspectors and the importers. Node lists are in the path's local space and paths are
    /// assumed unscaled (distances equal world metres).
    /// </summary>
    public static class TrackPathTools
    {
        public const float DefaultChainMaxGapMetres = 4f;
        public const float DefaultChainMinHeightMetres = 2f;
        public const float DefaultWidthSpikeFactor = 2.5f;
        public const float DefaultWidthSpikeMinMetres = 30f;
        public const int DefaultWidthSpikeWindow = 3;

        static readonly float[] KeyCheckpointFractions = { 0.25f, 0.5f, 0.75f };
        const float KeyJunctionMarginMetres = 10f;
        const float KeyFinishMarginMetres = 40f;
        const float KeyFinishMarginMaxLapFraction = 0.1f;
        const float BlockedIntervalStepMetres = 0.5f;
        const float MinCheckpointSpacingMetres = 1f;
        const float NodeMergeToleranceMetres = 0.25f;
        const string MergeRoutesLabel = "Merge chained routes";

        /// <summary>Rotates a closed node list so <paramref name="index"/> becomes node 0 and the only finish node.</summary>
        public static void MakeFinish(List<TrackPath.Node> nodes, int index)
        {
            if (nodes.Count == 0) return;
            index = ((index % nodes.Count) + nodes.Count) % nodes.Count;
            var rotated = nodes.Skip(index).Concat(nodes.Take(index)).ToList();
            for (var i = 0; i < rotated.Count; i++)
            {
                var node = rotated[i];
                node.flags = i == 0 ? node.flags | TrackNodeFlags.Finish : node.flags & ~TrackNodeFlags.Finish;
                rotated[i] = node;
            }
            nodes.Clear();
            nodes.AddRange(rotated);
        }

        /// <summary>
        /// Replaces the key checkpoints of <paramref name="runtime"/>'s main path with nodes at 25, 50 and 75 % of the lap,
        /// moved off every alternative-route section and away from the finish so every route passes every checkpoint.
        /// </summary>
        public static void PlaceKeyCheckpoints(TrackRuntime runtime)
        {
            var path = runtime.path;
            var nodes = NodesWithoutCheckpoints(path);
            foreach (var d in KeyCheckpointDistances(Length(nodes), RouteSections(runtime)))
            {
                var index = InsertAtDistance(nodes, d);
                var node = nodes[index];
                if ((node.flags & TrackNodeFlags.Finish) != 0) continue;
                node.flags |= TrackNodeFlags.Checkpoint;
                nodes[index] = node;
            }
            path.SetNodes(nodes);
            runtime.InvalidateJoins();
        }

        /// <summary>Removes every key checkpoint flag of a path.</summary>
        public static void ClearCheckpoints(TrackPath path) => path.SetNodes(NodesWithoutCheckpoints(path));

        /// <summary>Moves the first and last node of an alternative route onto the main line (nearest points).</summary>
        public static void SnapRouteEnds(TrackPath main, TrackPath route)
        {
            if (main == null || route == null || route.Count < 2) return;
            var nodes = route.Nodes.ToList();
            foreach (var i in new[] { 0, nodes.Count - 1 })
            {
                var d = main.Project(route.GetNodePosition(i));
                var node = nodes[i];
                node.position = route.transform.InverseTransformPoint(main.GetPoint(d));
                nodes[i] = node;
            }
            route.SetNodes(nodes);
        }

        /// <summary>
        /// Joins alternative routes split where they pass over or under the main line: route B starts within
        /// <paramref name="maxGap"/> m of route A's end and both junction neighbours lie over <paramref name="minHeight"/> m
        /// off the main line. A keeps its nodes followed by B's, B is destroyed. Returns the number of joins.
        /// </summary>
        public static int MergeChainedRoutes(TrackRuntime runtime, float maxGap = DefaultChainMaxGapMetres, float minHeight = DefaultChainMinHeightMetres)
        {
            var main = runtime != null ? runtime.path : null;
            if (main == null || main.Count < 2) return 0;
            var merged = 0;
            while (TryFindChainedPair(runtime, main, maxGap, minHeight, out var a, out var b))
            {
                JoinRoutes(runtime, a, b);
                merged++;
            }
            return merged;
        }

        /// <summary>
        /// Narrows single-node width spikes of the racing line and every route: a node wider than <paramref name="minSpike"/> m
        /// and <paramref name="factor"/> times the median of the <paramref name="window"/> nodes on each side gets that
        /// median. Returns the number of nodes changed.
        /// </summary>
        public static int ClampWidthSpikes(TrackRuntime runtime, float factor = DefaultWidthSpikeFactor,
                                           float minSpike = DefaultWidthSpikeMinMetres, int window = DefaultWidthSpikeWindow)
        {
            if (runtime == null) return 0;
            var changed = 0;
            var paths = new List<TrackPath> { runtime.path };
            paths.AddRange(runtime.alternatePaths);
            foreach (var path in paths)
                if (path != null && path.Count >= 3) changed += ClampPathSpikes(path, factor, minSpike, window);
            if (changed > 0) runtime.InvalidateJoins();
            return changed;
        }

        /// <summary>Checks a track's racing lines. See <see cref="TrackIssue"/> for the severities.</summary>
        public static List<TrackIssue> Validate(TrackRuntime runtime) => TrackPathValidator.Validate(runtime);

        static float Length(IReadOnlyList<TrackPath.Node> nodes)
        {
            var acc = 0f;
            for (var i = 0; i < nodes.Count; i++) acc += Vector3.Distance(nodes[i].position, nodes[(i + 1) % nodes.Count].position);
            return acc;
        }

        static int InsertAtDistance(List<TrackPath.Node> nodes, float d)
        {
            var n = nodes.Count;
            var acc = 0f;
            for (var i = 0; i < n; i++)
            {
                var a = nodes[i];
                var b = nodes[(i + 1) % n];
                var len = Vector3.Distance(a.position, b.position);
                if (d <= acc + len || i == n - 1)
                {
                    var along = Mathf.Clamp(d - acc, 0f, len);
                    if (along < NodeMergeToleranceMetres) return i;
                    if (len - along < NodeMergeToleranceMetres) return (i + 1) % n;
                    var t = along / len;
                    nodes.Insert(i + 1, new TrackPath.Node
                    {
                        position = Vector3.Lerp(a.position, b.position, t),
                        width = Mathf.Lerp(a.width, b.width, t),
                    });
                    return i + 1;
                }
                acc += len;
            }
            return 0;
        }

        static float[] KeyCheckpointDistances(float lapLength, IList<Vector2> routeSections)
        {
            var len = lapLength;
            var blocked = BlockedIntervals(len, routeSections);
            var lo = Mathf.Min(KeyFinishMarginMetres, len * KeyFinishMarginMaxLapFraction);
            var hi = len - lo;
            var keys = new float[KeyCheckpointFractions.Length];
            var prev = 0f;
            for (var k = 0; k < keys.Length; k++)
            {
                var best = NearestFreeDistance(KeyCheckpointFractions[k] * len, prev, blocked, lo, hi);
                keys[k] = Mathf.Max(best, prev + MinCheckpointSpacingMetres);
                prev = keys[k];
            }
            return keys;
        }

        static List<Vector2> BlockedIntervals(float len, IList<Vector2> routeSections)
        {
            var blocked = new List<Vector2>();
            foreach (var r in routeSections)
            {
                var from = r.x - KeyJunctionMarginMetres;
                var to = from + r.y + 2f * KeyJunctionMarginMetres;
                blocked.Add(new Vector2(from, to));
                blocked.Add(new Vector2(from - len, to - len));
                blocked.Add(new Vector2(from + len, to + len));
            }
            return blocked;
        }

        static bool IsFree(float q, float lo, float hi, List<Vector2> blocked)
        {
            if (q < lo || q > hi) return false;
            foreach (var iv in blocked)
                if (q > iv.x && q < iv.y) return false;
            return true;
        }

        static float NearestFreeDistance(float nominal, float prev, List<Vector2> blocked, float lo, float hi)
        {
            if (IsFree(nominal, lo, hi, blocked)) return nominal;
            var best = nominal;
            var bestErr = float.MaxValue;
            foreach (var iv in blocked)
            {
                Consider(iv.x - BlockedIntervalStepMetres);
                Consider(iv.y + BlockedIntervalStepMetres);
            }
            return best;

            void Consider(float q)
            {
                if (!IsFree(q, lo, hi, blocked) || q <= prev) return;
                var err = Mathf.Abs(q - nominal);
                if (err < bestErr) { bestErr = err; best = q; }
            }
        }

        static List<Vector2> RouteSections(TrackRuntime runtime)
        {
            var sections = new List<Vector2>();
            foreach (var alt in runtime.alternatePaths)
            {
                if (alt == null) continue;
                TrackRuntime.ComputeJoin(runtime.path, alt, out var start, out var span);
                sections.Add(new Vector2(start, span));
            }
            return sections;
        }

        static List<TrackPath.Node> NodesWithoutCheckpoints(TrackPath path) =>
            path.Nodes.Select(n => { n.flags &= ~TrackNodeFlags.Checkpoint; return n; }).ToList();

        static bool TryFindChainedPair(TrackRuntime runtime, TrackPath main, float maxGap, float minHeight, out int a, out int b)
        {
            var routes = runtime.alternatePaths;
            for (a = 0; a < routes.Count; a++)
            for (b = 0; b < routes.Count; b++)
                if (a != b && IsChained(main, routes[a], routes[b], maxGap, minHeight)) return true;
            a = b = -1;
            return false;
        }

        static bool IsChained(TrackPath main, TrackPath ra, TrackPath rb, float maxGap, float minHeight)
        {
            if (ra == null || rb == null || ra.Count < 3 || rb.Count < 3) return false;
            var end = ra.GetNodePosition(ra.Count - 1);
            var start = rb.GetNodePosition(0);
            if (new Vector2(end.x - start.x, end.z - start.z).magnitude > maxGap) return false;
            var before = ra.GetNodePosition(ra.Count - 2);
            var after = rb.GetNodePosition(1);
            return !(Mathf.Abs(before.y - main.GetPoint(main.Project(before)).y) < minHeight ||
                     Mathf.Abs(after.y - main.GetPoint(main.Project(after)).y) < minHeight);
        }

        static void JoinRoutes(TrackRuntime runtime, int a, int b)
        {
            var ra = runtime.alternatePaths[a];
            var rb = runtime.alternatePaths[b];
            var nodes = new List<TrackPath.Node>();
            for (var i = 0; i < ra.Count - 1; i++) nodes.Add(WorldNode(ra, ra.Nodes[i], ra));
            for (var i = 1; i < rb.Count; i++) nodes.Add(WorldNode(rb, rb.Nodes[i], ra));
            TrackPathUndo.Edit(ra, MergeRoutesLabel, p => p.SetNodes(nodes));
            TrackPathUndo.Record(runtime, MergeRoutesLabel, r => r.alternatePaths.RemoveAt(b));
            Undo.DestroyObjectImmediate(rb.gameObject);
            runtime.InvalidateJoins();
        }

        static TrackPath.Node WorldNode(TrackPath from, TrackPath.Node n, TrackPath to)
        {
            n.position = to.transform.InverseTransformPoint(from.transform.TransformPoint(n.position));
            return n;
        }

        static int ClampPathSpikes(TrackPath path, float factor, float minSpike, int window)
        {
            var nodes = path.Nodes.ToList();
            var widths = nodes.Select(n => n.width).ToArray();
            var around = new List<float>();
            var changed = 0;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (!TryNeighbourMedian(widths, i, window, path.closed, around, out var median)) continue;
                if (widths[i] <= minSpike || widths[i] <= factor * median) continue;
                var n = nodes[i];
                n.width = median;
                nodes[i] = n;
                changed++;
            }
            if (!nodes.Select(n => n.width).SequenceEqual(widths))
                TrackPathUndo.Edit(path, "Clamp width spikes", p => p.SetNodes(nodes));
            return changed;
        }

        static bool TryNeighbourMedian(float[] widths, int i, int window, bool closed, List<float> around, out float median)
        {
            around.Clear();
            for (var o = -window; o <= window; o++)
            {
                if (o == 0) continue;
                var j = i + o;
                if (closed) j = (j % widths.Length + widths.Length) % widths.Length;
                else if (j < 0 || j >= widths.Length) continue;
                around.Add(widths[j]);
            }
            median = 0f;
            if (around.Count == 0) return false;
            around.Sort();
            median = around[around.Count / 2];
            return true;
        }
    }
}
