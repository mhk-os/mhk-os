using System.Collections.Generic;
using MHK.Race;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    /// <summary>TrackRuntime inspector: default fields, the racing lines found below it, racing-line tools, validation and scaling.</summary>
    [CustomEditor(typeof(TrackRuntime))]
    public class TrackRuntimeEditor : UnityEditor.Editor
    {
        const string LinesHelp = "Racing lines are the TrackPaths below this object: the closed one is the main line, every open one " +
                                 "an alternative route (first node where it forks off the main line, last node where it rejoins).";
        const string ScaleHelp = "Scales the whole track about the world origin to change its size relative to the karts " +
                                 "(TrackScaler): item boxes keep their size. Bake the track's lighting again afterwards.";

        List<TrackIssue> issues;
        float scaleFactor = 1f;
        bool scaleFog = true;

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
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scale", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(ScaleHelp, MessageType.None);
            scaleFactor = EditorGUILayout.FloatField("Factor", scaleFactor);
            scaleFog = EditorGUILayout.Toggle(new GUIContent("Scale fog", "Off keeps the fog distances and density (a fog shared by all tracks of a game)."), scaleFog);
            using (new EditorGUI.DisabledScope(scaleFactor <= 0f || Mathf.Approximately(scaleFactor, 1f)))
                if (GUILayout.Button($"Scale track ×{scaleFactor:0.###}"))
                    Debug.Log(TrackScaler.Scale(runtime.gameObject.scene, scaleFactor, scaleFog));
        }
    }
}
