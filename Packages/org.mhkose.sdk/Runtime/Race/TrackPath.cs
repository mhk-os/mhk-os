using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>Role of a racing-line node.</summary>
    [Flags]
    public enum TrackNodeFlags
    {
        None = 0,
        /// <summary>Start/finish line: always node 0 of the main (closed) path.</summary>
        Finish = 1 << 0,
        /// <summary>Key checkpoint: all of them must be passed in order before crossing the finish counts as a lap.</summary>
        Checkpoint = 1 << 1,
    }

    /// <summary>
    /// Racing line of a track as a polyline in this transform's space. The closed main line starts at the finish line
    /// (node 0), so every distance on it is the distance driven since the line. Open paths below the same
    /// <see cref="TrackRuntime"/> are its alternative routes.
    /// </summary>
    public class TrackPath : MonoBehaviour
    {
        [Serializable]
        public struct Node
        {
            public Vector3 position;
            [Tooltip("Drivable width around the line (m).")]
            public float width;
            [Tooltip("Finish: the start/finish line (main path node 0). Checkpoint: key checkpoint of a lap.")]
            public TrackNodeFlags flags;
        }

        [Tooltip("Closed loop: the track's main racing line (one per track). Open: an alternative route (shortcut / " +
                 "branch) from its first node, where it forks off the main line, to its last, where it rejoins.")]
        public bool closed = true;
        [Tooltip("Draw this path on the race minimap (alternative routes are only shown for games whose maps did).")]
        public bool showOnMinimap = true;
        [SerializeField] List<Node> nodes = new();

        /// <summary>Road width (m) of a path without nodes.</summary>
        public const float DefaultWidth = 8f;
        /// <summary>Height differences count this much more than horizontal ones in projections (bridges, overpasses).</summary>
        public const float HeightWeight = 2.5f;
        /// <summary>Segments searched on either side of a valid projection hint.</summary>
        const int SearchRadius = 12;
        const float MinLength = 0.001f;

        Vector3[] worldPos;
        float[] cumulative;
        float[] checkpointDistances;
        float length;
        Bounds worldBounds;
        Vector3 finishNormal;
        bool baked;

        public int Count => nodes.Count;
        public float Length { get { Bake(); return length; } }
        public IReadOnlyList<Node> Nodes => nodes;
        /// <summary>World-space bounds of the nodes (road width not included).</summary>
        public Bounds WorldBounds { get { Bake(); return worldBounds; } }
        int SegmentCount => closed ? nodes.Count : Mathf.Max(0, nodes.Count - 1);

        /// <summary>Number of key checkpoints (nodes flagged <see cref="TrackNodeFlags.Checkpoint"/>).</summary>
        public int CheckpointCount { get { Bake(); return checkpointDistances.Length; } }

        /// <summary>Horizontal unit normal of the start/finish plane, pointing in driving direction.</summary>
        public Vector3 FinishNormal { get { Bake(); return finishNormal; } }

        /// <summary>Distance from the finish line of key checkpoint <paramref name="index"/> (in node order).</summary>
        public float GetCheckpointDistance(int index)
        {
            Bake();
            return checkpointDistances[index];
        }

        public void SetNodes(IEnumerable<Node> newNodes)
        {
            nodes = new List<Node>(newNodes);
            baked = false;
        }

        public void SetNode(int index, Node node)
        {
            nodes[index] = node;
            baked = false;
        }

        public void InsertNode(int index, Node node)
        {
            nodes.Insert(index, node);
            baked = false;
        }

        public void RemoveNode(int index)
        {
            nodes.RemoveAt(index);
            baked = false;
        }

        /// <summary>Drops the baked world-space data (call after editing nodes through serialization or undo).</summary>
        public void Invalidate() => baked = false;

        void OnValidate()
        {
            baked = false;
#if UNITY_EDITOR
            TrackRuntime.MarkLinesEdited();
#endif
        }

#if UNITY_EDITOR
        // A new racing line closes when its track has no main line yet, else it is a route.
        void Reset()
        {
            var runtime = GetComponentInParent<TrackRuntime>(true);
            var main = runtime != null ? runtime.MainPath : null;
            closed = main == null || main == this;
        }
#endif

        void Bake()
        {
            if (baked && worldPos != null && worldPos.Length == nodes.Count) return;
            var n = nodes.Count;
            worldPos = new Vector3[n];
            cumulative = new float[n + 1];
            for (var i = 0; i < n; i++) worldPos[i] = transform.TransformPoint(nodes[i].position);
            BakeDistances(n);
            worldBounds = new Bounds(n > 0 ? worldPos[0] : transform.position, Vector3.zero);
            for (var i = 1; i < n; i++) worldBounds.Encapsulate(worldPos[i]);
            // Finish plane normal: horizontal driving direction at node 0 (chord over its neighbours).
            finishNormal = n >= 2 ? worldPos[1] - worldPos[closed && n >= 3 ? n - 1 : 0] : transform.forward;
            finishNormal.y = 0f;
            finishNormal = finishNormal.sqrMagnitude > 1e-8f ? finishNormal.normalized : Vector3.forward;
            baked = true;
        }

        void BakeDistances(int n)
        {
            var acc = 0f;
            var checkpoints = 0;
            for (var i = 0; i < n; i++)
            {
                cumulative[i] = acc;
                if (closed || i < n - 1) acc += Vector3.Distance(worldPos[i], worldPos[(i + 1) % n]);
                if ((nodes[i].flags & TrackNodeFlags.Checkpoint) != 0) checkpoints++;
            }
            cumulative[n] = acc;
            length = Mathf.Max(acc, MinLength);
            checkpointDistances = new float[checkpoints];
            for (int i = 0, k = 0; i < n; i++)
                if ((nodes[i].flags & TrackNodeFlags.Checkpoint) != 0) checkpointDistances[k++] = cumulative[i];
        }

        public Vector3 GetNodePosition(int i)
        {
            Bake();
            return worldPos[Wrap(i)];
        }

        public float GetNodeWidth(int i) => nodes.Count == 0 ? DefaultWidth : Mathf.Max(1f, nodes[Wrap(i)].width);

        public TrackNodeFlags GetNodeFlags(int i) => nodes.Count == 0 ? TrackNodeFlags.None : nodes[Wrap(i)].flags;

        public float GetNodeDistance(int i)
        {
            Bake();
            return cumulative[Wrap(i)];
        }

        /// <summary>Node index wrapped around a closed loop, clamped to the ends of an open path.</summary>
        public int Wrap(int i)
        {
            var n = nodes.Count;
            if (n == 0) return 0;
            return closed ? ((i % n) + n) % n : Mathf.Clamp(i, 0, n - 1);
        }

        /// <summary>Distance wrapped into [0, Length) on a closed loop, clamped to [0, Length] on an open path.</summary>
        public float WrapDistance(float d)
        {
            var l = Length;
            if (!closed) return Mathf.Clamp(d, 0f, l);
            d %= l;
            return d < 0 ? d + l : d;
        }

        /// <summary>World position on the line at distance <paramref name="d"/> from node 0.</summary>
        public Vector3 GetPoint(float d)
        {
            Bake();
            if (nodes.Count == 0) return transform.position;
            if (nodes.Count == 1) return worldPos[0];
            SegmentParameter(d, out var i, out var t);
            return Vector3.Lerp(worldPos[i], worldPos[(i + 1) % nodes.Count], t);
        }

        /// <summary>Road width (m) at distance <paramref name="d"/>, interpolated between the nodes.</summary>
        public float GetWidth(float d)
        {
            Bake();
            if (nodes.Count == 0) return DefaultWidth;
            if (nodes.Count == 1) return GetNodeWidth(0);
            SegmentParameter(d, out var i, out var t);
            return Mathf.Lerp(GetNodeWidth(i), GetNodeWidth(i + 1), t);
        }

        void SegmentParameter(float d, out int segment, out float t)
        {
            d = WrapDistance(d);
            segment = FindSegment(d);
            var segLen = cumulative[segment + 1] - cumulative[segment];
            t = segLen > 1e-5f ? Mathf.Clamp01((d - cumulative[segment]) / segLen) : 0f;
        }

        /// <summary>Unit tangent (driving direction) at distance d, smoothed over a few metres.</summary>
        public Vector3 GetTangent(float d, float smoothing = 2f)
        {
            if (!closed) d = Mathf.Clamp(d, smoothing, Mathf.Max(smoothing, Length - smoothing));
            var a = GetPoint(d - smoothing);
            var b = GetPoint(d + smoothing);
            var t = b - a;
            return t.sqrMagnitude > 1e-6f ? t.normalized : transform.forward;
        }

        /// <summary>Horizontal unit driving direction at <paramref name="d"/>, world forward when the line is vertical there.</summary>
        public Vector3 GetFlatTangent(float d, float smoothing = 2f)
        {
            var t = GetTangent(d, smoothing);
            var flat = new Vector3(t.x, 0f, t.z).normalized;
            return flat.sqrMagnitude < 0.01f ? Vector3.forward : flat;
        }

        /// <summary>
        /// Horizontal unit vector to the right of <paramref name="tangent"/>, or <see cref="Vector3.right"/> when the
        /// tangent's horizontal part is not longer than sqrt(<paramref name="minSqrMagnitude"/>).
        /// </summary>
        public static Vector3 RightOf(Vector3 tangent, float minSqrMagnitude = 1e-8f)
        {
            var side = new Vector3(tangent.z, 0f, -tangent.x);
            return side.sqrMagnitude > minSqrMagnitude ? side.normalized : Vector3.right;
        }

        /// <summary>Horizontal unit vector to the right of the driving direction at <paramref name="d"/>.</summary>
        public Vector3 GetRight(float d, float smoothing = 2f) => RightOf(GetTangent(d, smoothing));

        /// <summary>
        /// Lateral road position of <paramref name="worldPosition"/> at <paramref name="d"/>: 0 = left edge, 1 = right
        /// edge of a road <paramref name="width"/> metres wide, unclamped.
        /// </summary>
        public float GetRoadLane(Vector3 worldPosition, float d, float width) =>
            0.5f + Vector3.Dot(worldPosition - GetPoint(d), GetRight(d)) / width;

        /// <summary>World point at <paramref name="lane"/> (0 = left edge, 1 = right edge) of a road <paramref name="width"/> metres wide.</summary>
        public Vector3 GetPointAtLane(float d, float lane, float width) =>
            GetPoint(d) + GetRight(d) * ((lane - 0.5f) * width);

        /// <summary>Point <paramref name="ahead"/> metres along the line from the projection of <paramref name="worldPosition"/>.</summary>
        public Vector3 PointAhead(Vector3 worldPosition, ref int hintSegment, float ahead) =>
            GetPoint(Project(worldPosition, ref hintSegment, out _) + ahead);

        /// <summary>
        /// Signed horizontal distance (m) of <paramref name="worldPosition"/> from the start/finish plane (vertical,
        /// through node 0, across the driving direction there), positive past the line.
        /// </summary>
        public float SignedDistanceToFinish(Vector3 worldPosition)
        {
            Bake();
            if (nodes.Count == 0) return 0f;
            var d = worldPosition - worldPos[0];
            return d.x * finishNormal.x + d.z * finishNormal.z;
        }

        /// <summary>Index of the segment containing distance <paramref name="d"/> (a projection hint).</summary>
        public int SegmentAt(float d)
        {
            Bake();
            return nodes.Count < 2 ? -1 : FindSegment(WrapDistance(d));
        }

        int FindSegment(float d)
        {
            int lo = 0, hi = SegmentCount - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) >> 1;
                if (cumulative[mid] <= d) lo = mid; else hi = mid - 1;
            }
            return Mathf.Max(0, lo);
        }

        /// <summary>
        /// Projects a world position onto the line. If <paramref name="hintSegment"/> is valid only nearby
        /// segments are searched (fast, avoids snapping to parallel track sections).
        /// </summary>
        public float Project(Vector3 worldPosition, ref int hintSegment, out float lateralDistance)
            => Project(worldPosition, ref hintSegment, out lateralDistance, out _);

        /// <summary>
        /// Projection as above. <paramref name="score"/> is the distance to the line with heights weighted by
        /// <see cref="HeightWeight"/>, comparable between paths.
        /// </summary>
        public float Project(Vector3 worldPosition, ref int hintSegment, out float lateralDistance, out float score)
            => Track(worldPosition, ref hintSegment, float.NaN, 0f, out lateralDistance, out score);

        public float Project(Vector3 worldPosition)
        {
            var hint = -1;
            return Project(worldPosition, ref hint, out _);
        }

        /// <summary>
        /// Projection that follows a moving object: candidates further than <paramref name="allowance"/> metres
        /// along the line from <paramref name="previousDistance"/> are penalised by the excess (squared), so the
        /// result walks continuously around hairpins instead of jumping to the other leg. Pass NaN for a plain
        /// nearest-point projection. <paramref name="score"/> is the unpenalised height-weighted distance.
        /// </summary>
        public float Track(Vector3 worldPosition, ref int hintSegment, float previousDistance, float allowance,
            out float lateralDistance, out float score)
        {
            Bake();
            var segs = SegmentCount;
            lateralDistance = 0f;
            score = float.MaxValue;
            if (nodes.Count < 2 || segs < 1) return 0f;
            var follow = !float.IsNaN(previousDistance);
            var best = float.MaxValue;
            var bestD = 0f;
            var bestSeg = 0;
            SearchWindow(hintSegment, segs, out var from, out var to);
            for (var k = from; k <= to; k++)
            {
                var i = closed ? Wrap(k) : k;
                var p = ClosestOnSegment(i, worldPosition, out var t, out var lenSq);
                var diff = worldPosition - p;
                diff.y *= HeightWeight;
                var dist = diff.sqrMagnitude;
                var d = cumulative[i] + Mathf.Sqrt(lenSq) * t;
                var cost = follow ? dist + FollowPenalty(previousDistance, d, allowance) : dist;
                if (!(cost < best)) continue;
                best = cost;
                bestSeg = i;
                bestD = d;
                score = Mathf.Sqrt(dist);
                lateralDistance = Vector3.Distance(new Vector3(worldPosition.x, 0, worldPosition.z), new Vector3(p.x, 0, p.z));
            }
            hintSegment = bestSeg;
            return bestD;
        }

        void SearchWindow(int hint, int segs, out int from, out int to)
        {
            var full = hint < 0 || hint >= segs || SearchRadius * 2 + 1 >= segs;
            from = full ? 0 : hint - SearchRadius;
            to = full ? segs - 1 : hint + SearchRadius;
            if (closed) return;
            from = Mathf.Max(0, from);
            to = Mathf.Min(segs - 1, to);
        }

        Vector3 ClosestOnSegment(int i, Vector3 worldPosition, out float t, out float lenSq)
        {
            var a = worldPos[i];
            var ab = worldPos[(i + 1) % nodes.Count] - a;
            lenSq = ab.sqrMagnitude;
            t = lenSq > 1e-6f ? Mathf.Clamp01(Vector3.Dot(worldPosition - a, ab) / lenSq) : 0f;
            return a + ab * t;
        }

        float FollowPenalty(float previousDistance, float d, float allowance)
        {
            var excess = Mathf.Abs(Delta(previousDistance, d)) - allowance;
            return excess > 0f ? excess * excess : 0f;
        }

        /// <summary>Signed distance travelled from <paramref name="from"/> to <paramref name="to"/> taking the loop into account.</summary>
        public float Delta(float from, float to)
        {
            var d = to - from;
            if (!closed) return d;
            var l = Length;
            if (d > l * 0.5f) d -= l;
            else if (d < -l * 0.5f) d += l;
            return d;
        }

#if UNITY_EDITOR
        /// <summary>Editor gizmo: line, road width, finish line and key checkpoints.</summary>
        public void DrawGizmoLines()
        {
            baked = false;
            Bake();
            var n = nodes.Count;
            for (var i = 0; i < n; i++)
            {
                var a = worldPos[i];
                if (closed || i < n - 1)
                {
                    Gizmos.color = closed ? Color.yellow : Color.cyan;
                    Gizmos.DrawLine(a, worldPos[(i + 1) % n]);
                }
                var flags = nodes[i].flags;
                if (flags == TrackNodeFlags.None) continue;
                var dir = closed && i == 0 ? finishNormal : GetTangent(cumulative[i], 3f);
                var side = RightOf(dir) * (GetNodeWidth(i) * 0.6f);
                Gizmos.color = (flags & TrackNodeFlags.Finish) != 0 ? Color.white : Color.magenta;
                Gizmos.DrawLine(a - side + Vector3.up * 0.5f, a + side + Vector3.up * 0.5f);
            }
        }
#endif
    }
}
