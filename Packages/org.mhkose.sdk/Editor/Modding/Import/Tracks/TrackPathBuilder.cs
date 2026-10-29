using System.Collections.Generic;
using System.Linq;
using MHK.Editor.Tracks;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Racing line and alternative routes of track.json ("path", "altPaths", "checkpoints"): node 0 on the start/finish
    /// line, key checkpoints off every route, width spikes narrowed.
    /// </summary>
    public static class TrackPathBuilder
    {
        const float SnapToNodeDistance = 0.25f;
        const float MinAxisAlignment = 0.7f;
        const float Epsilon = 1e-6f;

        /// <summary>Builds Path (closed, the main line) and its open routes in Path/Routes under <paramref name="root"/>.</summary>
        public static TrackPath Build(TrackImportContext c, Transform root, TrackRuntime runtime)
        {
            var s = c.Settings;
            var path = ImportHierarchy.CreateChild(root, "Path").gameObject.AddComponent<TrackPath>();
            var nodes = PathNodes(ExportJson.Items(c.Json["path"]), c.Scale, s.defaultPathWidth);
            PlaceFinish(c, nodes);
            path.closed = true;
            path.SetNodes(nodes);
            runtime.InvalidateLines();
            BuildRoutes(c, runtime, path);
            if (s.widthSpikeFactor > 0f && TrackPathTools.ClampWidthSpikes(runtime, s.widthSpikeFactor) is var narrowed and > 0)
                Debug.Log($"[Import] Track {c.Id}: {narrowed} racing-line / route width spike(s) narrowed to their neighbours.");
            if (nodes.Count >= 3) TrackPathTools.PlaceKeyCheckpoints(runtime);
            return path;
        }

        static void PlaceFinish(TrackImportContext c, List<TrackPath.Node> nodes)
        {
            var checkpoints = c.Json["checkpoints"] as JArray;
            var start = checkpoints?.FirstOrDefault(cp => ExportJson.Str(cp["kind"], "").Contains("start")) ?? checkpoints?.FirstOrDefault();
            if (start?["pos"] == null || nodes.Count < 3)
            {
                TrackPathTools.MakeFinish(nodes, 0);
                return;
            }
            var linePos = ExportJson.Pos(start["pos"], c.Scale);
            if (!StartAtPlane(nodes, linePos, FinishLineNormal(start, nodes, linePos), out var lateral))
                Debug.LogWarning($"[Import] Track {c.Id}: the racing line does not cross the start/finish checkpoint; node 0 = first node.");
            else if (lateral > c.Settings.finishLineReportDistance)
                Debug.Log($"[Import] Track {c.Id}: racing line crosses the finish line {lateral:0.0} m from the checkpoint centre.");
        }

        static List<TrackPath.Node> PathNodes(JArray path, float scale, float defaultWidth)
        {
            var nodes = path.Select(n => new TrackPath.Node { position = ExportJson.Pos(n["pos"], scale), width = ExportJson.F(n["width"], -1f) }).ToList();
            var known = Enumerable.Range(0, nodes.Count).Where(i => nodes[i].width > 0f).ToList();
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (n.width > 0f) n.width *= scale;
                else if (known.Count == 0) n.width = defaultWidth * scale;
                else n.width = InterpolatedWidth(path, known, i, nodes.Count) * scale;
                nodes[i] = n;
            }
            return nodes;
        }

        static float InterpolatedWidth(JArray path, List<int> known, int i, int count)
        {
            var after = known.Where(k => k > i).DefaultIfEmpty(known[0]).First();
            var before = known.Where(k => k < i).DefaultIfEmpty(known[known.Count - 1]).Last();
            var span = (after - before + count) % count;
            var t = span == 0 ? 0f : ((i - before + count) % count) / (float)span;
            return Mathf.Lerp(ExportJson.F(path[before]["width"]), ExportJson.F(path[after]["width"]), t);
        }

        // Node 0 becomes the first forward crossing of the vertical plane through point (inserted when needed).
        static bool StartAtPlane(List<TrackPath.Node> nodes, Vector3 point, Vector3 normal, out float lateral)
        {
            var best = FindForwardCrossing(nodes, point, normal, out var bestT, out lateral);
            if (best < 0)
            {
                lateral = 0f;
                TrackPathTools.MakeFinish(nodes, 0);
                return false;
            }
            var n0 = nodes[best];
            var n1 = nodes[(best + 1) % nodes.Count];
            var len = Vector3.Distance(n0.position, n1.position);
            int index;
            if (bestT * len < SnapToNodeDistance) index = best;
            else if ((1f - bestT) * len < SnapToNodeDistance) index = (best + 1) % nodes.Count;
            else
            {
                nodes.Insert(best + 1, new TrackPath.Node
                {
                    position = Vector3.Lerp(n0.position, n1.position, bestT),
                    width = Mathf.Lerp(n0.width, n1.width, bestT),
                });
                index = best + 1;
            }
            TrackPathTools.MakeFinish(nodes, index);
            return true;
        }

        static int FindForwardCrossing(List<TrackPath.Node> nodes, Vector3 point, Vector3 normal, out float bestT, out float bestLateral)
        {
            var best = -1;
            bestT = 0f;
            bestLateral = float.MaxValue;
            for (var i = 0; i < nodes.Count; i++)
            {
                var a = nodes[i].position;
                var b = nodes[(i + 1) % nodes.Count].position;
                var da = Vector3.Dot(a - point, normal);
                var db = Vector3.Dot(b - point, normal);
                if (!(da < 0f && db >= 0f)) continue;
                var t = da / (da - db);
                var lat = Vector3.ProjectOnPlane(Vector3.Lerp(a, b, t) - point, Vector3.up).magnitude;
                if (lat >= bestLateral) continue;
                bestLateral = lat;
                best = i;
                bestT = t;
            }
            return best;
        }

        static Vector3 FinishLineNormal(JToken checkpoint, List<TrackPath.Node> nodes, Vector3 boxPos)
        {
            var nearest = NearestSegment(nodes, boxPos);
            var seg = nodes[(nearest + 1) % nodes.Count].position - nodes[nearest].position;
            var tangent = new Vector3(seg.x, 0f, seg.z).normalized;
            if (tangent == Vector3.zero) tangent = Vector3.forward;
            var dir = tangent;
            if (checkpoint?["rot"] is not JArray) return dir;
            var q = ExportJson.Rot(checkpoint["rot"]);
            var bestDot = MinAxisAlignment;
            foreach (var axis in new[] { q * Vector3.forward, q * Vector3.right })
            {
                var flat = new Vector3(axis.x, 0f, axis.z);
                if (flat.sqrMagnitude < Epsilon) continue;
                flat.Normalize();
                var dot = Vector3.Dot(flat, tangent);
                if (Mathf.Abs(dot) <= bestDot) continue;
                bestDot = Mathf.Abs(dot);
                dir = dot >= 0f ? flat : -flat;
            }
            return dir;
        }

        static int NearestSegment(List<TrackPath.Node> nodes, Vector3 p)
        {
            var nearest = 0;
            var bestD = float.MaxValue;
            for (var i = 0; i < nodes.Count; i++)
            {
                var a = nodes[i].position;
                var ab = nodes[(i + 1) % nodes.Count].position - a;
                var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, Epsilon));
                var d = (a + ab * t - p).sqrMagnitude;
                if (d < bestD) { bestD = d; nearest = i; }
            }
            return nearest;
        }

        static void BuildRoutes(TrackImportContext c, TrackRuntime runtime, TrackPath main)
        {
            if (c.Json["altPaths"] is not JArray alts || alts.Count == 0 || main.Count < 2) return;
            var routes = ImportHierarchy.CreateChild(main.transform, TrackPathTools.RoutesGroup);
            foreach (var a in alts.OfType<JArray>()) TryAddRoute(c, main, routes, RouteNodes(a, main, c.Scale));
            runtime.InvalidateLines();
            if (routes.childCount == 0) Object.DestroyImmediate(routes.gameObject);
            else if (TrackPathTools.MergeChainedRoutes(runtime) is var joined and > 0)
                Debug.Log($"[Import] Track {c.Id}: {joined} route(s) split where they cross the main line joined to the route before them.");
        }

        static List<TrackPath.Node> RouteNodes(JArray route, TrackPath main, float scale) =>
            route.Select(n =>
            {
                var p = ExportJson.Pos(n["pos"], scale);
                var w = n["width"] != null && n["width"].Type != JTokenType.Null ? ExportJson.F(n["width"]) * scale : main.GetWidth(main.Project(p));
                return new TrackPath.Node { position = p, width = w };
            }).ToList();

        static void TryAddRoute(TrackImportContext c, TrackPath main, Transform routes, List<TrackPath.Node> nodes)
        {
            if (nodes.Count < 2) return;
            var route = ImportHierarchy.CreateChild(routes, "Route " + (routes.childCount + 1)).gameObject.AddComponent<TrackPath>();
            route.closed = false;
            route.showOnMinimap = c.Settings.routesOnMinimap;
            route.SetNodes(nodes);
            var mainLength = main.Length;
            TrackRuntime.ComputeJoin(main, route, out var joinStart, out var span);
            if (span >= c.Settings.minRouteBypass * mainLength && span <= c.Settings.maxRouteBypass * mainLength) return;
            Debug.Log($"[Import] Track {c.Id}: route skipped (joins the main line at {joinStart:0} m, bypassing {span:0} of {mainLength:0} m).");
            Object.DestroyImmediate(route.gameObject);
        }
    }
}
