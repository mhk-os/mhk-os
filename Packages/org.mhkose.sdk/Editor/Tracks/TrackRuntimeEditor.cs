using System.Collections.Generic;
using MHK.Race;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    /// <summary>TrackRuntime inspector: default fields, the racing lines found below it, racing-line tools and validation.</summary>
    [CustomEditor(typeof(TrackRuntime))]
    public class TrackRuntimeEditor : UnityEditor.Editor
    {
        const string LinesHelp = "Racing lines are the TrackPaths below this object: the closed one is the main line, every open one " +
                                 "an alternative route (first node where it forks off the main line, last node where it rejoins).";

        List<TrackIssue> issues;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var runtime = (TrackRuntime)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Racing lines", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(LinesHelp, MessageType.None);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Main line", runtime.MainPath, typeof(TrackPath), true);
                for (var i = 0; i < runtime.Routes.Count; i++)
                    EditorGUILayout.ObjectField($"Route {i}", runtime.Routes[i], typeof(TrackPath), true);
            }
            using (new EditorGUI.DisabledScope(runtime.MainPath == null))
            {
                if (GUILayout.Button("Edit main path")) Selection.activeObject = runtime.MainPath;
                if (GUILayout.Button("Auto-place key checkpoints (25 / 50 / 75 %)"))
                {
                    TrackPathUndo.Edit(runtime.MainPath, "Place key checkpoints", _ => TrackPathTools.PlaceKeyCheckpoints(runtime));
                    SceneView.RepaintAll();
                }
            }
            if (GUILayout.Button("Validate track")) issues = TrackPathTools.Validate(runtime);
            TrackPathEditor.DrawIssues(issues);
        }
    }
}
