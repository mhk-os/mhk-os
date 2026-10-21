using System;
using MHK.Race;
using UnityEditor;
using Object = UnityEngine.Object;

namespace MHK.Editor.Tracks
{
    /// <summary>Undoable racing-line edits shared by the inspectors, the scene tool and the path tools.</summary>
    internal static class TrackPathUndo
    {
        /// <summary>Records <paramref name="target"/> for undo, applies <paramref name="change"/> and keeps prefab instance overrides.</summary>
        public static void Record<T>(T target, string label, Action<T> change) where T : Object
        {
            Undo.RecordObject(target, label);
            change(target);
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        /// <summary>Undoable edit of <paramref name="path"/> that also drops its baked data and its track's route junctions.</summary>
        public static void Edit(TrackPath path, string label, Action<TrackPath> change)
        {
            Record(path, label, change);
            Invalidate(path);
        }

        /// <summary>Drops the baked world-space data of <paramref name="path"/> and the junctions of its track.</summary>
        public static void Invalidate(TrackPath path)
        {
            path.Invalidate();
            var runtime = path.GetComponentInParent<TrackRuntime>(true);
            if (runtime != null) runtime.InvalidateJoins();
        }
    }
}
