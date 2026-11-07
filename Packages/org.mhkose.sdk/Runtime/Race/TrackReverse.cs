using System.Collections.Generic;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Turns a loaded track round for a reverse variant (<see cref="Modding.TrackOptionRules.reverse"/>): the main racing
    /// line runs from the finish line the other way (its key checkpoints stay where they are), every alternative route is
    /// driven from its other end, routes that can only be driven the original way (a gap jumped, a drop driven down) are
    /// left out, the start grid moves behind the finish line in the new direction, and speed pads and respawn points face
    /// the other way. Runs once right after the track (and its overlays) loaded, before anything measured the lines.
    /// </summary>
    public static class TrackReverse
    {
        // A route falling more than this share of a sample step (steeper than about 45°) is a drop: no climbing it.
        const float DropShare = 0.7f;
        const float SampleStep = 2f;

        /// <summary>Turns <paramref name="runtime"/>'s track round.</summary>
        public static void Apply(TrackRuntime runtime)
        {
            var main = runtime.MainPath;
            if (main == null || main.Count < 3) return;
            var grid = MeasureGrid(runtime, main);
            var oneWay = new List<TrackPath>();
            foreach (var route in runtime.Routes)
                if (IsOneWay(runtime, route)) oneWay.Add(route);

            ReverseMain(main);
            foreach (var route in runtime.Routes) ReverseRoute(route);
            foreach (var route in oneWay) route.enabled = false;
            runtime.InvalidateLines();
            PlaceGrid(runtime, main, grid);
            TurnZones(runtime);
            foreach (var root in runtime.gameObject.scene.GetRootGameObjects())
            foreach (var bridge in root.GetComponentsInChildren<CollapsingBridge>(true))
                bridge.RebakeRoute();
        }

        // Node 0 (the finish line) stays first, the others run backwards: [0, n-1, ..., 1].
        static void ReverseMain(TrackPath main)
        {
            var nodes = main.Nodes;
            var reversed = new List<TrackPath.Node>(nodes.Count) { nodes[0] };
            for (var i = nodes.Count - 1; i >= 1; i--) reversed.Add(nodes[i]);
            main.SetNodes(reversed);
            TrackGaps.Forget(main);
        }

        static void ReverseRoute(TrackPath route)
        {
            var reversed = new List<TrackPath.Node>(route.Nodes);
            reversed.Reverse();
            route.SetNodes(reversed);
            TrackGaps.Forget(route);
        }

        // A gap or a drop along the route's original direction: driven backwards it would be a jump onto a wall or a climb.
        static bool IsOneWay(TrackRuntime runtime, TrackPath route)
        {
            for (var x = 0f; x <= route.Length; x += SampleStep)
                if (TrackGaps.IsGap(runtime, route, x) || route.GetPoint(x).y - route.GetPoint(x + SampleStep).y > DropShare * SampleStep)
                    return true;
            return false;
        }

        // Each start slot as distance along the main line from the finish line (negative: behind it), offset to the right of
        // the line and height above it.
        static List<(Transform slot, float along, float lateral, float height)> MeasureGrid(TrackRuntime runtime, TrackPath main)
        {
            var grid = new List<(Transform, float, float, float)>(runtime.startGrid.Count);
            foreach (var slot in runtime.startGrid)
            {
                if (slot == null) continue;
                var d = main.Project(slot.position);
                var point = main.GetPoint(d);
                grid.Add((slot, main.Delta(0f, d), Vector3.Dot(slot.position - point, main.GetRight(d)), slot.position.y - point.y));
            }
            return grid;
        }

        // The same distances and offsets along the reversed line: behind the finish line in the new direction, the pole still
        // on its side of the road as seen in driving direction.
        static void PlaceGrid(TrackRuntime runtime, TrackPath main, List<(Transform slot, float along, float lateral, float height)> grid)
        {
            foreach (var (slot, along, lateral, height) in grid)
            {
                var d = main.WrapDistance(along);
                var forward = main.GetFlatTangent(d);
                var position = main.GetPoint(d) + TrackPath.RightOf(forward) * lateral + Vector3.up * height;
                slot.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
            }
        }

        // Speed pads turn karts to their forward axis and respawn points face the driving direction: both turn round about
        // their own up axis (a pad on a slope stays on it), speed pads about their box centre. Conveyor belts and fans (push
        // zones) keep pushing the way they are drawn.
        static void TurnZones(TrackRuntime runtime)
        {
            foreach (var root in runtime.gameObject.scene.GetRootGameObjects())
            foreach (var zone in root.GetComponentsInChildren<TrackZone>(true))
            {
                switch (zone.kind)
                {
                    case TrackZoneKind.Boost:
                        var box = zone.GetComponent<BoxCollider>();
                        var centre = box != null ? zone.transform.TransformPoint(box.center) : zone.transform.position;
                        zone.transform.RotateAround(centre, zone.transform.up, 180f);
                        break;
                    case TrackZoneKind.RespawnPoint when zone.respawnPoint != null:
                        zone.respawnPoint.Rotate(0f, 180f, 0f, Space.Self);
                        break;
                }
            }
        }
    }
}
