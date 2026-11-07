using System;
using System.Collections.Generic;
using MHK.Race;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// The track scene behind an overlay being edited, as a locked reference: nothing in it can be picked in the Scene view,
    /// it can be drawn as a see-through x-ray instead of its own look, and its racing lines show with arrows in the driving
    /// direction (turned round for a reversing option). Changes nothing in the scene: picking and visibility are editor
    /// state, the x-ray draws the meshes itself.
    /// </summary>
    internal sealed class TrackOverlayGhost : IDisposable
    {
        const float LineStep = 2f, ArrowSpacing = 15f, LineLift = 0.3f, ArrowLift = 0.6f, ArrowSize = 0.25f, LineWidth = 4f;
        const string XrayShader = "Hidden/Internal-Colored";
        static readonly Color XrayColor = new(0.55f, 0.75f, 1f, 0.12f);
        static readonly Color MainLineColor = new(1f, 0.85f, 0.2f);
        static readonly Color RouteColor = new(0.3f, 0.85f, 1f);

        readonly Scene scene;
        readonly TrackRuntime runtime;
        readonly List<(Mesh mesh, Matrix4x4 matrix)> meshes = new();
        Material xrayMaterial;
        bool xray;

        /// <summary>Draws the racing lines.</summary>
        public bool ShowLines { get; set; } = true;

        /// <summary>The arrows point against the racing lines' direction (an option with <see cref="MHK.Modding.TrackOptionRules.reverse"/>).</summary>
        public bool Reverse { get; set; }

        /// <summary>The scene is hidden in the Scene view and drawn as a see-through silhouette instead.</summary>
        public bool Xray
        {
            get => xray;
            set
            {
                if (xray == value) return;
                xray = value;
                var roots = scene.GetRootGameObjects();
                if (xray) SceneVisibilityManager.instance.Hide(roots, true);
                else SceneVisibilityManager.instance.Show(roots, true);
                SceneView.RepaintAll();
            }
        }

        /// <summary>Locks the loaded track scene <paramref name="trackScene"/> as the reference of an overlay.</summary>
        public TrackOverlayGhost(Scene trackScene)
        {
            scene = trackScene;
            runtime = TrackScenes.FindRuntime(trackScene);
            SceneVisibilityManager.instance.DisablePicking(trackScene.GetRootGameObjects(), true);
            CollectMeshes();
            SceneView.duringSceneGui += OnSceneGui;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            SceneView.RepaintAll();
        }

        /// <summary>Unlocks and shows the scene again (when it is still loaded).</summary>
        public void Dispose()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            if (scene.IsValid() && scene.isLoaded)
            {
                var roots = scene.GetRootGameObjects();
                SceneVisibilityManager.instance.EnablePicking(roots, true);
                if (xray) SceneVisibilityManager.instance.Show(roots, true);
            }
            xray = false;
            if (xrayMaterial != null) Object.DestroyImmediate(xrayMaterial);
            SceneView.RepaintAll();
        }

        // Visible meshes of the scene, sky domes left out (they would only veil the view).
        void CollectMeshes()
        {
            foreach (var root in scene.GetRootGameObjects())
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || r.GetComponentInParent<FollowCameraPosition>(true) != null || r.GetComponentInParent<WorldSkyDome>(true) != null) continue;
                if (r.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null) meshes.Add((filter.sharedMesh, r.localToWorldMatrix));
            }
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!xray || camera.cameraType != CameraType.SceneView || meshes.Count == 0) return;
            var material = XrayMaterial();
            if (material == null) return;
            foreach (var (mesh, matrix) in meshes)
                for (var s = 0; s < mesh.subMeshCount; s++)
                    Graphics.DrawMesh(mesh, matrix, material, 0, camera, s);
        }

        Material XrayMaterial()
        {
            if (xrayMaterial != null) return xrayMaterial;
            var shader = Shader.Find(XrayShader);
            if (shader == null) return null;
            xrayMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = (int)RenderQueue.Transparent };
            xrayMaterial.SetColor("_Color", XrayColor);
            xrayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            xrayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            xrayMaterial.SetInt("_Cull", (int)CullMode.Off);
            xrayMaterial.SetInt("_ZWrite", 0);
            xrayMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            return xrayMaterial;
        }

        void OnSceneGui(SceneView view)
        {
            if (!ShowLines || runtime == null || Event.current.type != EventType.Repaint) return;
            if (runtime.MainPath != null) DrawLine(runtime.MainPath, MainLineColor);
            foreach (var route in runtime.Routes) DrawLine(route, RouteColor);
        }

        void DrawLine(TrackPath path, Color color)
        {
            if (path.Count < 2) return;
            var length = path.Length;
            var steps = Mathf.Max(1, Mathf.CeilToInt(length / LineStep));
            var points = new Vector3[steps + 1];
            for (var i = 0; i <= steps; i++) points[i] = path.GetPoint(length * i / steps) + Vector3.up * LineLift;
            using (new Handles.DrawingScope(color))
            {
                Handles.DrawAAPolyLine(LineWidth, points);
                for (var d = ArrowSpacing * 0.5f; d < length; d += ArrowSpacing)
                {
                    var tangent = path.GetTangent(d) * (Reverse ? -1f : 1f);
                    if (tangent.sqrMagnitude < 1e-6f) continue;
                    var p = path.GetPoint(d) + Vector3.up * ArrowLift;
                    Handles.ConeHandleCap(0, p, Quaternion.LookRotation(tangent), HandleUtility.GetHandleSize(p) * ArrowSize, EventType.Repaint);
                }
            }
        }
    }
}
