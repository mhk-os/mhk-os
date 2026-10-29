using System.Collections.Generic;
using System.Linq;
using MHK.Race;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Inspector for <see cref="TrackPath"/> racing lines: node fields, insert and delete, finish line, checkpoint and
    /// validation tools. Scene handles live in <see cref="TrackPathTool"/>. Every edit is undoable.
    /// </summary>
    [CustomEditor(typeof(TrackPath))]
    public class TrackPathEditor : UnityEditor.Editor
    {
        const float MinRoadWidth = 0f;
        const float NavButtonWidth = 28f;
        const float FrameButtonWidth = 52f;
        const float FrameSize = 30f;

        bool showNodeList;
        List<TrackIssue> issues;
        TrackRuntime runtime;

        TrackPath Path => (TrackPath)target;

        void OnEnable()
        {
            runtime = Path != null ? Path.GetComponentInParent<TrackRuntime>(true) : null;
            if (Path != null) TrackPathNodes.Select(Path, -1);
            Undo.undoRedoPerformed += OnUndoRedo;
            TrackPathNodes.Changed += Repaint;
            EditorApplication.delayCall += ActivateTool;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            TrackPathNodes.Changed -= Repaint;
        }

        static void ActivateTool()
        {
            if (Selection.activeGameObject != null && Selection.activeGameObject.TryGetComponent<TrackPath>(out _)
                && ToolManager.activeToolType != typeof(TrackPathTool))
                ToolManager.SetActiveTool<TrackPathTool>();
        }

        void OnUndoRedo()
        {
            if (Path != null) Changed();
        }

        void Changed()
        {
            runtime = Path.GetComponentInParent<TrackRuntime>(true);
            TrackPathUndo.Invalidate(Path);
            TrackPathNodes.Repaint();
        }

        public override void OnInspectorGUI()
        {
            var path = Path;
            EditorGUILayout.EditorToolbarForTarget(EditorGUIUtility.TrTempContent("Edit racing line"), this);
            PropertyFieldsGUI("closed", "showOnMinimap");
            var isMain = runtime != null && runtime.MainPath == path;
            var route = runtime != null ? runtime.RouteIndex(path) : -1;
            SummaryGUI(path, isMain, route);
            NodeGUI(path);
            ToolsGUI(path, isMain, route);
            DrawIssues(issues);

            EditorGUILayout.Space();
            showNodeList = EditorGUILayout.Foldout(showNodeList, "Node list", true);
            if (showNodeList) PropertyFieldsGUI("nodes");
        }

        void PropertyFieldsGUI(params string[] names)
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            foreach (var name in names) EditorGUILayout.PropertyField(serializedObject.FindProperty(name), true);
            if (!EditorGUI.EndChangeCheck()) return;
            serializedObject.ApplyModifiedProperties();
            Changed();
        }

        void SummaryGUI(TrackPath path, bool isMain, int route)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Role(path, isMain, route), EditorStyles.boldLabel);
            var problem = RoleProblem(path, isMain, route);
            if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Warning);
            EditorGUILayout.LabelField("Nodes / length", $"{path.Count} / {path.Length:0.0} m");
            if (isMain && path.Count > 0)
            {
                var finishOk = (path.Nodes[0].flags & TrackNodeFlags.Finish) != 0;
                EditorGUILayout.LabelField("Finish line", finishOk ? "node 0" : "node 0 is not flagged Finish");
                var cps = Enumerable.Range(0, path.CheckpointCount).Select(k => $"{path.GetCheckpointDistance(k):0} m");
                EditorGUILayout.LabelField("Key checkpoints", path.CheckpointCount == 0 ? "none" : string.Join(", ", cps));
            }
            if (route >= 0)
            {
                EditorGUILayout.LabelField("Fork / merge on main line", $"{runtime.BranchJoinStart(route):0} m → {runtime.BranchJoinEnd(route):0} m");
                EditorGUILayout.LabelField("Bypasses", $"{runtime.BranchSpan(route):0} m of the main line");
            }
        }

        string Role(TrackPath path, bool isMain, int route) =>
            runtime == null ? "Racing line (no TrackRuntime above it)"
            : isMain ? "Main racing line"
            : path.closed ? "Second closed racing line"
            : route >= 0 ? $"Alternative route {route}"
            : "Alternative route (not used yet)";

        // Why a racing line does nothing in races, null when it is in use.
        string RoleProblem(TrackPath path, bool isMain, int route)
        {
            if (runtime == null) return "Races only use racing lines below the track's TrackRuntime: move it there.";
            if (isMain || route >= 0) return null;
            if (path.closed) return $"Only the first closed line ({runtime.MainPath.name}) is the main line. Untick Closed to make this an alternative route.";
            var problem = TrackRuntime.RouteProblem(runtime.MainPath, path);
            return $"Races leave it out: {problem}{(problem == TrackRuntime.ReversedRouteProblem ? " (use 'Reverse direction')" : "")}.";
        }

        static void NodeGUI(TrackPath path)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Selected node (click a node in the Scene view)", EditorStyles.boldLabel);
            if (path.Count == 0) return;
            var selected = NodeNavigationGUI(path, TrackPathNodes.Selected(path));
            if (selected < 0 || selected >= path.Count) return;
            NodeFieldsGUI(path, selected);
            NodeButtonsGUI(path, selected);
        }

        static int NodeNavigationGUI(TrackPath path, int selected)
        {
            var before = selected;
            using (new EditorGUILayout.HorizontalScope())
            {
                selected = EditorGUILayout.IntSlider(selected, -1, path.Count - 1);
                if (GUILayout.Button("◀", GUILayout.Width(NavButtonWidth))) selected = path.Wrap(selected - 1);
                if (GUILayout.Button("▶", GUILayout.Width(NavButtonWidth))) selected = path.Wrap(selected + 1);
                if (GUILayout.Button("Frame", GUILayout.Width(FrameButtonWidth)) && selected >= 0 && SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.Frame(new Bounds(path.GetNodePosition(selected), Vector3.one * FrameSize), false);
            }
            if (selected != before) TrackPathNodes.Select(path, selected);
            return selected;
        }

        static void NodeFieldsGUI(TrackPath path, int selected)
        {
            var node = path.Nodes[selected];
            EditorGUI.BeginChangeCheck();
            node.position = EditorGUILayout.Vector3Field("Position (local)", node.position);
            node.width = Mathf.Max(MinRoadWidth, EditorGUILayout.FloatField("Road width", node.width));
            node.flags = (TrackNodeFlags)EditorGUILayout.EnumFlagsField("Flags", node.flags);
            if (EditorGUI.EndChangeCheck())
                TrackPathNodes.Edit(path, "Edit racing-line node", p => p.SetNode(selected, node));
            EditorGUILayout.LabelField("Distance from node 0", $"{path.GetNodeDistance(selected):0.0} m");
        }

        static void NodeButtonsGUI(TrackPath path, int selected)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Insert before")) TrackPathNodes.InsertNear(path, selected, false);
                if (GUILayout.Button("Insert after")) TrackPathNodes.InsertNear(path, selected, true);
                using (new EditorGUI.DisabledScope(!TrackPathNodes.CanDelete(path, selected)))
                    if (GUILayout.Button(new GUIContent("Delete", "Node 0 of the main line is the finish: set another node as finish first.")))
                        TrackPathNodes.Delete(path, selected);
            }
            if (path.closed && selected != 0 && GUILayout.Button("Set as finish line (becomes node 0)"))
                TrackPathNodes.SetFinish(path, selected);
        }

        void ToolsGUI(TrackPath path, bool isMain, int route)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);
            var rt = runtime;
            var main = rt != null ? rt.MainPath : null;
            if (isMain)
            {
                if (GUILayout.Button("Auto-place key checkpoints (25 / 50 / 75 %, off the alternative routes)"))
                    TrackPathNodes.Edit(path, "Place key checkpoints", _ => TrackPathTools.PlaceKeyCheckpoints(rt));
                if (GUILayout.Button("Clear key checkpoints"))
                    TrackPathNodes.Edit(path, "Clear key checkpoints", TrackPathTools.ClearCheckpoints);
                var selected = TrackPathNodes.Selected(path);
                using (new EditorGUI.DisabledScope(selected < 0))
                    if (GUILayout.Button(new GUIContent("New alternative route from the selected node",
                            "An open racing line in Routes that forks off here and rejoins a little further on: drag its nodes where the route goes.")))
                    {
                        Selection.activeObject = TrackPathTools.CreateRoute(rt, path.GetNodeDistance(selected));
                        GUIUtility.ExitGUI();
                    }
            }
            if (!path.closed && main != null)
            {
                if (GUILayout.Button("Snap ends to main line"))
                    TrackPathNodes.Edit(path, "Snap route ends", p => TrackPathTools.SnapRouteEnds(main, p));
                if (GUILayout.Button(new GUIContent("Reverse direction", "First node = where the route forks off, last node = where it rejoins, in driving direction.")))
                    TrackPathNodes.Edit(path, "Reverse route", TrackPathTools.Reverse);
            }
            using (new EditorGUI.DisabledScope(rt == null))
                if (GUILayout.Button("Validate track")) issues = TrackPathTools.Validate(rt);
        }

        /// <summary>Validation results as help boxes (shared with the TrackRuntime inspector).</summary>
        public static void DrawIssues(List<TrackIssue> list)
        {
            if (list == null) return;
            if (list.All(i => i.Severity == TrackIssueSeverity.Info))
                EditorGUILayout.HelpBox("No problems found.", MessageType.Info);
            foreach (var issue in list) EditorGUILayout.HelpBox(issue.Message, MessageTypeOf(issue.Severity));
        }

        static MessageType MessageTypeOf(TrackIssueSeverity severity) => severity switch
        {
            TrackIssueSeverity.Error => MessageType.Error,
            TrackIssueSeverity.Warning => MessageType.Warning,
            _ => MessageType.Info,
        };
    }
}
