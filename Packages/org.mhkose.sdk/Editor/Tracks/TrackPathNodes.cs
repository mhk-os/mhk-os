using System;
using System.Linq;
using MHK.Race;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    /// <summary>The selected racing-line node and the node edits shared by the TrackPath inspector and scene tool.</summary>
    internal static class TrackPathNodes
    {
        const float OpenEndExtensionMetres = 5f;

        static TrackPath owner;
        static int selected = -1;

        /// <summary>Raised after a selection change or an edit, so the inspector repaints.</summary>
        public static event Action Changed;

        public static int Selected(TrackPath path) => path == owner ? selected : -1;

        public static void Select(TrackPath path, int index)
        {
            owner = path;
            selected = index;
            Repaint();
        }

        public static void Repaint()
        {
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        /// <summary>Undoable edit of <paramref name="path"/> followed by a repaint of the inspector and Scene views.</summary>
        public static void Edit(TrackPath path, string label, Action<TrackPath> change)
        {
            TrackPathUndo.Edit(path, label, change);
            Repaint();
        }

        /// <summary>Node 0 of a closed path is the finish line and never deletable.</summary>
        public static bool CanDelete(TrackPath path, int index) =>
            path.closed ? path.Count > 3 && index != 0 : path.Count > 2;

        public static void Delete(TrackPath path, int index)
        {
            if (!CanDelete(path, index)) return;
            Edit(path, "Delete racing-line node", p => p.RemoveNode(index));
            Select(path, Mathf.Min(index, path.Count - 1));
        }

        public static void InsertNear(TrackPath path, int index, bool after)
        {
            var n = path.Count;
            var node = NewNodeNear(path, index, after);
            var at = after ? index + 1 : index;
            // Between the last node and node 0 of a loop: append, so node 0 stays the finish line.
            if (path.closed && ((after && index == n - 1) || (!after && index == 0))) at = n;
            Edit(path, "Insert racing-line node", p => p.InsertNode(at, node));
            Select(path, at);
        }

        public static void SetFinish(TrackPath path, int index)
        {
            Edit(path, "Set finish line", p =>
            {
                var list = p.Nodes.ToList();
                TrackPathTools.MakeFinish(list, index);
                p.SetNodes(list);
            });
            Select(path, 0);
        }

        static TrackPath.Node NewNodeNear(TrackPath path, int index, bool after)
        {
            var a = path.Nodes[index];
            var neighbour = after ? index + 1 : index - 1;
            if (path.closed || (neighbour >= 0 && neighbour < path.Count))
            {
                var b = path.Nodes[path.Wrap(neighbour)];
                return new TrackPath.Node { position = (a.position + b.position) * 0.5f, width = (a.width + b.width) * 0.5f };
            }
            var inner = path.Nodes[after ? index - 1 : index + 1];
            var dir = (a.position - inner.position).normalized;
            return new TrackPath.Node { position = a.position + dir * OpenEndExtensionMetres, width = a.width };
        }
    }
}
