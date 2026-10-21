using System.Collections.Generic;
using MHK.Race;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    /// <summary>TrackRuntime inspector: default fields plus racing-line tools and validation.</summary>
    [CustomEditor(typeof(TrackRuntime))]
    public class TrackRuntimeEditor : UnityEditor.Editor
    {
        List<TrackIssue> issues;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var runtime = (TrackRuntime)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Racing lines", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(runtime.path == null))
            {
                if (GUILayout.Button("Edit main path")) Selection.activeObject = runtime.path;
                if (GUILayout.Button("Auto-place key checkpoints (25 / 50 / 75 %)"))
                {
                    TrackPathUndo.Edit(runtime.path, "Place key checkpoints", _ => TrackPathTools.PlaceKeyCheckpoints(runtime));
                    SceneView.RepaintAll();
                }
            }
            if (GUILayout.Button("Validate track")) issues = TrackPathTools.Validate(runtime);
            TrackPathEditor.DrawIssues(issues);
        }
    }
}
