using System.Collections.Generic;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Collapsing bridges of track.json ("triggers", MHK Extra Trigger.ini): a <see cref="CollapsingBridge"/> per trigger
    /// over the chunk corridor fromChunk..toChunk ("gates"), on the alternative route closest to it, with the named
    /// visual pieces ("objects": segment, startTime). No sound: the export carries only the original sound name.
    /// </summary>
    public static class TrackBridgeBuilder
    {
        /// <summary>
        /// The original moves a falling piece by its speed once per frame (speed += fallAcceleration × frame time), so
        /// its acceleration per second² is fallAcceleration × the frame rate. The start times are multiples of 1/30 s.
        /// </summary>
        const float OriginalFrameRate = 30f;
        /// <summary>Added to the kart-body growth of the road (borderOffset): the corridor's reach beyond the gate ends.</summary>
        const float ExtraSideMargin = 0.15f;

        /// <summary>Builds the bridges under the Props group of <paramref name="root"/> (after the visual and the routes).</summary>
        public static void Build(TrackImportContext c, Transform root, Transform visual, TrackRuntime runtime)
        {
            if (c.Json["triggers"] is not JArray triggers || triggers.Count == 0) return;
            var gates = new Dictionary<int, JToken>();
            foreach (var g in ExportJson.Items(c.Json["gates"])) gates[ExportJson.I(g["chunk"], -1)] = g;
            var parent = root.Find("Props");
            if (parent == null) parent = ImportHierarchy.CreateChild(root, "Props");
            foreach (var t in triggers)
            {
                var name = ExportJson.Str(t["name"]) ?? "Collapsing Bridge";
                var bridge = ImportHierarchy.CreateChild(parent, name).gameObject.AddComponent<CollapsingBridge>();
                // A kart on chunks fromChunk..toChunk sets it off; chunk k is the corridor from gate k-1 to gate k.
                for (var k = ExportJson.I(t["fromChunk"]) - 1; k <= ExportJson.I(t["toChunk"]); k++)
                {
                    if (!gates.TryGetValue(k, out var g)) continue;
                    bridge.gates.Add(new CollapsingBridge.Gate { left = ExportJson.Pos(g["left"], c.Scale), right = ExportJson.Pos(g["right"], c.Scale) });
                }
                bridge.sideMargin = ExportJson.F(c.Json["borderOffset"]) * c.Scale + ExtraSideMargin;
                bridge.triggerWait = ExportJson.F(t["triggerWaitTime"], bridge.triggerWait);
                bridge.dropWait = ExportJson.F(t["dropWaitTime"], bridge.dropWait);
                bridge.fallAcceleration = Mathf.Abs(ExportJson.F(t["fallAcceleration"])) * OriginalFrameRate * c.Scale;
                bridge.fallDepth = ExportJson.F(t["fallHeight"]) * c.Scale;
                AddSegments(c, t, visual, bridge);
                bridge.route = ClosestRoute(runtime, bridge);
                if (bridge.gates.Count < 2) Debug.LogWarning($"[Import] {c.ShortName}: bridge {name} has fewer than two gates.");
            }
        }

        static void AddSegments(TrackImportContext c, JToken trigger, Transform visual, CollapsingBridge bridge)
        {
            foreach (var o in ExportJson.Items(trigger["objects"]))
            {
                var piece = visual != null ? ImportHierarchy.FindBelow(visual, ExportJson.Str(o["segment"])) : null;
                if (piece == null)
                {
                    Debug.LogWarning($"[Import] {c.ShortName}: bridge piece {ExportJson.Str(o["segment"])} not found.");
                    continue;
                }
                // Static batching would freeze the piece in place.
                foreach (var t in piece.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, GameObjectUtility.GetStaticEditorFlags(t.gameObject) & ~StaticEditorFlags.BatchingStatic);
                bridge.segments.Add(new CollapsingBridge.Segment { piece = piece, delay = ExportJson.F(o["startTime"]) });
            }
        }

        // The alternative route nearest to the gate midpoints (the bridges of the originals are one-time shortcuts).
        static TrackPath ClosestRoute(TrackRuntime runtime, CollapsingBridge bridge)
        {
            TrackPath best = null;
            var bestSum = float.MaxValue;
            foreach (var route in runtime.Routes)
            {
                if (route == null || route.Count < 2) continue;
                var sum = 0f;
                foreach (var g in bridge.gates)
                {
                    var hint = -1;
                    route.Project(bridge.transform.TransformPoint((g.left + g.right) * 0.5f), ref hint, out var lateral);
                    sum += lateral;
                }
                if (sum >= bestSum) continue;
                bestSum = sum;
                best = route;
            }
            var limit = bridge.gates.Count * 2f;
            return bestSum <= limit ? best : null;
        }
    }
}
