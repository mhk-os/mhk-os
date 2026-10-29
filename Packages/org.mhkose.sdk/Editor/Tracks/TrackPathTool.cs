using MHK.Race;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    internal static class TrackPathHandleStyle
    {
        public static readonly Color OtherLine = new(1f, 1f, 1f, 0.35f);
        public static readonly Color RoadEdge = new(1f, 0.5f, 0f, 0.7f);
        public static readonly Color WidthHandle = new(1f, 0.5f, 0f);
        public const float OtherLineWidth = 2f;
        public const float LineWidth = 4f;
        public const float EdgeWidth = 2f;
        public const float GateWidth = 6f;
        public const float GateHalfWidthFactor = 0.6f;
        public const float GateLift = 0.3f;
        public const float GateLabelHeight = 2f;
        public const float IndexLabelHeight = 1f;
        public const float SelectedLabelHeight = 2.5f;
        public const int IndexLabelStride = 10;
        public const float NodeSize = 0.06f;
        public const float WidthHandleSize = 0.12f;
        public const float InsertButtonSize = 0.05f;
        public const float PickSizeFactor = 1.5f;
        public const float MinDraggedWidth = 0.5f;
        public const float SideTangentWindow = 3f;
    }

    /// <summary>Scene-view tool for racing lines: select nodes, move them, drag the road width, insert and delete nodes.</summary>
    [EditorTool("Edit Racing Line", typeof(TrackPath))]
    sealed class TrackPathTool : EditorTool
    {
        TrackPath cachedPath;
        TrackRuntime runtime;

        public override void OnToolGUI(EditorWindow window)
        {
            var path = target as TrackPath;
            if (path == null || path.Count == 0) return;
            if (path != cachedPath)
            {
                cachedPath = path;
                runtime = path.GetComponentInParent<TrackRuntime>(true);
            }
            InvalidateIfMoved(path);
            if (Event.current.type == EventType.Repaint)
            {
                DrawOtherLines(path);
                DrawLineAndEdges(path);
            }
            var clicked = DrawNodes(path, TrackPathNodes.Selected(path));
            if (clicked >= 0) TrackPathNodes.Select(path, clicked);

            var selected = TrackPathNodes.Selected(path);
            if (selected < 0 || selected >= path.Count) return;
            SelectedNodeHandles(path, selected);
            HandleDeleteKey(path, TrackPathNodes.Selected(path));
        }

        static void InvalidateIfMoved(TrackPath path)
        {
            if (!path.transform.hasChanged) return;
            // The track was moved, rotated or scaled: the baked world-space nodes are stale.
            TrackPathUndo.Invalidate(path);
            path.transform.hasChanged = false;
        }

        void DrawOtherLines(TrackPath path)
        {
            if (runtime == null) return;
            Handles.color = TrackPathHandleStyle.OtherLine;
            foreach (var other in runtime.Routes) DrawOtherLine(other, path);
            DrawOtherLine(runtime.MainPath, path);
        }

        static void DrawOtherLine(TrackPath other, TrackPath path)
        {
            if (other != null && other != path && other.Count > 1)
                Handles.DrawAAPolyLine(TrackPathHandleStyle.OtherLineWidth, Points(other, other.closed));
        }

        static void DrawLineAndEdges(TrackPath path)
        {
            var closed = path.closed;
            Handles.color = closed ? Color.yellow : Color.cyan;
            Handles.DrawAAPolyLine(TrackPathHandleStyle.LineWidth, Points(path, closed));
            var left = new Vector3[closed ? path.Count + 1 : path.Count];
            var right = new Vector3[left.Length];
            for (var i = 0; i < left.Length; i++)
            {
                var k = path.Wrap(i);
                var p = path.GetNodePosition(k);
                var side = Side(path, k) * (path.GetNodeWidth(k) * 0.5f);
                left[i] = p - side;
                right[i] = p + side;
            }
            Handles.color = TrackPathHandleStyle.RoadEdge;
            Handles.DrawAAPolyLine(TrackPathHandleStyle.EdgeWidth, left);
            Handles.DrawAAPolyLine(TrackPathHandleStyle.EdgeWidth, right);
        }

        static int DrawNodes(TrackPath path, int selected)
        {
            var repaint = Event.current.type == EventType.Repaint;
            var clicked = -1;
            var checkpoint = 0;
            for (var i = 0; i < path.Count; i++)
            {
                var p = path.GetNodePosition(i);
                var flags = path.GetNodeFlags(i);
                if (flags != TrackNodeFlags.None)
                {
                    if (repaint) DrawGate(path, i, p, flags, checkpoint);
                    if ((flags & TrackNodeFlags.Checkpoint) != 0) checkpoint++;
                }
                if (NodeButton(path, p, flags, i == selected)) clicked = selected = i;
                if (repaint && i % TrackPathHandleStyle.IndexLabelStride == 0 && i != selected)
                    Handles.Label(p + Vector3.up * TrackPathHandleStyle.IndexLabelHeight, i.ToString());
            }
            return clicked;
        }

        static bool NodeButton(TrackPath path, Vector3 p, TrackNodeFlags flags, bool isSelected)
        {
            var size = HandleUtility.GetHandleSize(p) * TrackPathHandleStyle.NodeSize;
            Handles.color = isSelected ? Color.red : flags != TrackNodeFlags.None ? Color.white : path.closed ? Color.yellow : Color.cyan;
            return Handles.Button(p, Quaternion.identity, size, size * TrackPathHandleStyle.PickSizeFactor, Handles.DotHandleCap);
        }

        static void DrawGate(TrackPath path, int i, Vector3 p, TrackNodeFlags flags, int checkpoint)
        {
            var finish = (flags & TrackNodeFlags.Finish) != 0;
            var across = path.closed && i == 0 ? Vector3.Cross(Vector3.up, path.FinishNormal) : Side(path, i);
            var side = across * (path.GetNodeWidth(i) * TrackPathHandleStyle.GateHalfWidthFactor);
            var lift = Vector3.up * TrackPathHandleStyle.GateLift;
            Handles.color = finish ? Color.white : Color.magenta;
            Handles.DrawAAPolyLine(TrackPathHandleStyle.GateWidth, p - side + lift, p + side + lift);
            Handles.Label(p + Vector3.up * TrackPathHandleStyle.GateLabelHeight, finish ? "Finish" : $"Checkpoint {checkpoint}", EditorStyles.boldLabel);
        }

        static void SelectedNodeHandles(TrackPath path, int index)
        {
            var world = path.GetNodePosition(index);
            var node = path.Nodes[index];
            if (Event.current.type == EventType.Repaint)
                Handles.Label(world + Vector3.up * TrackPathHandleStyle.SelectedLabelHeight, $"Node {index}  ({path.GetNodeDistance(index):0} m)");
            PositionHandle(path, index, world, node);
            WidthHandle(path, index, world, node);
            InsertButtons(path, index, world);
        }

        static void PositionHandle(TrackPath path, int index, Vector3 world, TrackPath.Node node)
        {
            EditorGUI.BeginChangeCheck();
            var moved = Handles.PositionHandle(world, Quaternion.identity);
            if (!EditorGUI.EndChangeCheck()) return;
            node.position = path.transform.InverseTransformPoint(moved);
            TrackPathNodes.Edit(path, "Move racing-line node", p => p.SetNode(index, node));
        }

        static void WidthHandle(TrackPath path, int index, Vector3 world, TrackPath.Node node)
        {
            var side = Side(path, index);
            var edge = world + side * (node.width * 0.5f);
            Handles.color = TrackPathHandleStyle.WidthHandle;
            EditorGUI.BeginChangeCheck();
            var dragged = Handles.Slider(edge, side, HandleUtility.GetHandleSize(edge) * TrackPathHandleStyle.WidthHandleSize, Handles.CubeHandleCap, 0f);
            if (!EditorGUI.EndChangeCheck()) return;
            node.width = Mathf.Max(TrackPathHandleStyle.MinDraggedWidth, 2f * Mathf.Abs(Vector3.Dot(dragged - world, side)));
            TrackPathNodes.Edit(path, "Change road width", p => p.SetNode(index, node));
        }

        static void InsertButtons(TrackPath path, int index, Vector3 world)
        {
            Handles.color = Color.green;
            InsertButton(path, index, world, false);
            InsertButton(path, index, world, true);
        }

        static void InsertButton(TrackPath path, int index, Vector3 world, bool after)
        {
            var neighbour = after ? index + 1 : index - 1;
            if (!path.closed && (neighbour < 0 || neighbour >= path.Count)) return;
            var mid = (world + path.GetNodePosition(path.Wrap(neighbour))) * 0.5f;
            var size = HandleUtility.GetHandleSize(mid) * TrackPathHandleStyle.InsertButtonSize;
            if (Handles.Button(mid, Quaternion.identity, size, size * TrackPathHandleStyle.PickSizeFactor, Handles.SphereHandleCap))
                TrackPathNodes.InsertNear(path, index, after);
        }

        static void HandleDeleteKey(TrackPath path, int selected)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || (e.keyCode != KeyCode.Delete && e.keyCode != KeyCode.Backspace)) return;
            if (selected < 0 || selected >= path.Count || !TrackPathNodes.CanDelete(path, selected)) return;
            TrackPathNodes.Delete(path, selected);
            e.Use();
        }

        static Vector3[] Points(TrackPath path, bool closed)
        {
            var pts = new Vector3[closed ? path.Count + 1 : path.Count];
            for (var i = 0; i < pts.Length; i++) pts[i] = path.GetNodePosition(path.Wrap(i));
            return pts;
        }

        static Vector3 Side(TrackPath path, int i)
        {
            var t = path.GetTangent(path.GetNodeDistance(i), TrackPathHandleStyle.SideTangentWindow);
            var side = Vector3.Cross(Vector3.up, new Vector3(t.x, 0f, t.z));
            return side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.right;
        }
    }
}
