using System.Collections.Generic;
using MHK.Race;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor.Tracks
{
    internal static class TrackPathHandleStyle
    {
        public static readonly Color OtherLine = new(1f, 1f, 1f, 0.6f);
        public static readonly Color RoadEdge = new(1f, 0.45f, 0f);
        public static readonly Color RoadFill = new(1f, 0.45f, 0f, 0.18f);
        public static readonly Color WidthHandle = new(1f, 0.45f, 0f);
        /// <summary>Colour multiplier for whatever scene geometry hides: drawn darker and fainter instead of not at all.</summary>
        public static readonly Color Occluded = new(0.5f, 0.5f, 0.5f, 0.4f);
        // Line widths in points (scaled to pixels on high-DPI screens).
        public const float OtherLineWidth = 2f;
        public const float LineWidth = 3f;
        public const float EdgeWidth = 4f;
        public const float GateWidth = 4f;
        public const float GateHalfWidthFactor = 0.6f;
        public const float GateLift = 0.3f;
        public const float GateLabelHeight = 2f;
        public const float IndexLabelHeight = 1f;
        public const float SelectedLabelHeight = 2.5f;
        public const int IndexLabelStride = 10;
        public const float NodeSize = 0.06f;
        /// <summary>Lines, fill and handles float this many handle sizes (a few pixels) above the road they lie on, so the depth test doesn't half-bury them.</summary>
        public const float SurfaceLift = 0.06f;
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
        static readonly Vector3[] Quad = new Vector3[4];

        readonly List<Vector3[]> otherLines = new();
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
            try
            {
                if (Event.current.type == EventType.Repaint) DrawTrack(path);
                var clicked = DrawNodes(path, TrackPathNodes.Selected(path));
                if (clicked >= 0) TrackPathNodes.Select(path, clicked);

                var selected = TrackPathNodes.Selected(path);
                if (selected < 0 || selected >= path.Count) return;
                SelectedNodeHandles(path, selected);
                HandleDeleteKey(path, TrackPathNodes.Selected(path));
            }
            finally
            {
                Handles.zTest = CompareFunction.Always;
            }
        }

        static void InvalidateIfMoved(TrackPath path)
        {
            if (!path.transform.hasChanged) return;
            // The track was moved, rotated or scaled: the baked world-space nodes are stale.
            TrackPathUndo.Invalidate(path);
            path.transform.hasChanged = false;
        }

        // Everything twice: first the parts scene geometry hides, darkened, then the visible parts on top.
        void DrawTrack(TrackPath path)
        {
            CollectOtherLines(path);
            var line = Points(path, path.closed);
            Edges(path, out var left, out var right);
            for (var pass = 0; pass < 2; pass++)
            {
                var tint = DepthPass(pass == 0);
                Handles.color = TrackPathHandleStyle.OtherLine * tint;
                foreach (var other in otherLines) PolyLine(TrackPathHandleStyle.OtherLineWidth, other);
                DrawFill(left, right, tint);
                Handles.color = TrackPathHandleStyle.RoadEdge * tint;
                PolyLine(TrackPathHandleStyle.EdgeWidth, left);
                PolyLine(TrackPathHandleStyle.EdgeWidth, right);
                Handles.color = (path.closed ? Color.yellow : Color.cyan) * tint;
                PolyLine(TrackPathHandleStyle.LineWidth, line);
                DrawGates(path, tint);
            }
        }

        /// <summary>Sets the depth test of one drawing pass and returns its colour multiplier.</summary>
        static Color DepthPass(bool occluded)
        {
            Handles.zTest = occluded ? CompareFunction.Greater : CompareFunction.LessEqual;
            return occluded ? TrackPathHandleStyle.Occluded : Color.white;
        }

        // A solid texture: the default anti-aliasing one fades out half the width and draws at 75 % alpha.
        static void PolyLine(float widthPoints, params Vector3[] points) =>
            Handles.DrawAAPolyLine(Texture2D.whiteTexture, widthPoints * EditorGUIUtility.pixelsPerPoint, points);

        /// <summary>
        /// Repaint: draws the hidden part of an interactive handle's cap darkened. Then sets the depth test for the
        /// visible part, which the handle itself draws.
        /// </summary>
        static void OccludedCap(Handles.CapFunction cap, Vector3 position, Quaternion rotation, float size)
        {
            if (Event.current.type == EventType.Repaint)
            {
                var color = Handles.color;
                Handles.color = color * DepthPass(true);
                cap(0, position, rotation, size, EventType.Repaint);
                Handles.color = color;
            }
            DepthPass(false);
        }

        void CollectOtherLines(TrackPath path)
        {
            otherLines.Clear();
            if (runtime == null) return;
            foreach (var other in runtime.Routes) AddOtherLine(other, path);
            AddOtherLine(runtime.MainPath, path);
        }

        void AddOtherLine(TrackPath other, TrackPath path)
        {
            if (other != null && other != path && other.Count > 1) otherLines.Add(Points(other, other.closed));
        }

        static void DrawFill(Vector3[] left, Vector3[] right, Color tint)
        {
            Handles.color = tint;
            for (var i = 0; i + 1 < left.Length; i++)
            {
                Quad[0] = left[i];
                Quad[1] = left[i + 1];
                Quad[2] = right[i + 1];
                Quad[3] = right[i];
                Handles.DrawSolidRectangleWithOutline(Quad, TrackPathHandleStyle.RoadFill, Color.clear);
            }
        }

        static void DrawGates(TrackPath path, Color tint)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var flags = path.GetNodeFlags(i);
                if (flags == TrackNodeFlags.None) continue;
                var finish = (flags & TrackNodeFlags.Finish) != 0;
                var p = path.GetNodePosition(i) + Vector3.up * TrackPathHandleStyle.GateLift;
                var across = path.closed && i == 0 ? Vector3.Cross(Vector3.up, path.FinishNormal) : Side(path, i);
                var side = across * (path.GetNodeWidth(i) * TrackPathHandleStyle.GateHalfWidthFactor);
                Handles.color = (finish ? Color.white : Color.magenta) * tint;
                PolyLine(TrackPathHandleStyle.GateWidth, p - side, p + side);
            }
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
                    var finish = (flags & TrackNodeFlags.Finish) != 0;
                    if (repaint)
                        Handles.Label(p + Vector3.up * TrackPathHandleStyle.GateLabelHeight, finish ? "Finish" : $"Checkpoint {checkpoint}", EditorStyles.boldLabel);
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
            p = Lift(p);
            Handles.color = isSelected ? Color.red : flags != TrackNodeFlags.None ? Color.white : path.closed ? Color.yellow : Color.cyan;
            // Nodes ignore depth: always visible and clickable, also behind scene geometry.
            Handles.zTest = CompareFunction.Always;
            return Handles.Button(p, Quaternion.identity, size, size * TrackPathHandleStyle.PickSizeFactor, Handles.DotHandleCap);
        }

        static void SelectedNodeHandles(TrackPath path, int index)
        {
            var world = path.GetNodePosition(index);
            var node = path.Nodes[index];
            if (Event.current.type == EventType.Repaint)
                Handles.Label(world + Vector3.up * TrackPathHandleStyle.SelectedLabelHeight, $"Node {index}  ({path.GetNodeDistance(index):0} m)");
            // Move and width handles ignore depth: always visible and grabbable, also behind scene geometry.
            Handles.zTest = CompareFunction.Always;
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
            var edge = Lift(world + side * (node.width * 0.5f));
            var size = HandleUtility.GetHandleSize(edge) * TrackPathHandleStyle.WidthHandleSize;
            Handles.color = TrackPathHandleStyle.WidthHandle;
            EditorGUI.BeginChangeCheck();
            var dragged = Handles.Slider(edge, side, size, Handles.CubeHandleCap, 0f);
            if (!EditorGUI.EndChangeCheck()) return;
            // The lift is vertical, the side horizontal: it doesn't count towards the width.
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
            var mid = Lift((world + path.GetNodePosition(path.Wrap(neighbour))) * 0.5f);
            var size = HandleUtility.GetHandleSize(mid) * TrackPathHandleStyle.InsertButtonSize;
            OccludedCap(Handles.SphereHandleCap, mid, Quaternion.identity, size);
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

        static Vector3 Lift(Vector3 p) => p + Vector3.up * (HandleUtility.GetHandleSize(p) * TrackPathHandleStyle.SurfaceLift);

        static Vector3[] Points(TrackPath path, bool closed)
        {
            var pts = new Vector3[closed ? path.Count + 1 : path.Count];
            for (var i = 0; i < pts.Length; i++) pts[i] = Lift(path.GetNodePosition(path.Wrap(i)));
            return pts;
        }

        static void Edges(TrackPath path, out Vector3[] left, out Vector3[] right)
        {
            left = new Vector3[path.closed ? path.Count + 1 : path.Count];
            right = new Vector3[left.Length];
            for (var i = 0; i < left.Length; i++)
            {
                var k = path.Wrap(i);
                var p = path.GetNodePosition(k);
                var side = Side(path, k) * (path.GetNodeWidth(k) * 0.5f);
                left[i] = Lift(p - side);
                right[i] = Lift(p + side);
            }
        }

        static Vector3 Side(TrackPath path, int i)
        {
            var t = path.GetTangent(path.GetNodeDistance(i), TrackPathHandleStyle.SideTangentWindow);
            var side = Vector3.Cross(Vector3.up, new Vector3(t.x, 0f, t.z));
            return side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.right;
        }
    }
}
