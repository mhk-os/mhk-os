using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Race
{
    /// <summary>
    /// Sky dome that stays where the scene placed it and surrounds the whole track, as in TINcan, MHK3 and MHK Thunder
    /// (MHK2 and MHK Extra centred theirs on the camera: <see cref="FollowCameraPosition"/>). At track load the race
    /// camera's far clip is raised to reach the dome from anywhere near the racing lines. The unfogged shell writes
    /// depth to hide geometry outside it; smaller sun flares glow over the shell, before the world is drawn.
    /// </summary>
    public sealed class WorldSkyDome : MonoBehaviour
    {
        // Source games whose engines kept the sky dome in the world. Their tracks imported by older SDKs carry a
        // FollowCameraPosition instead.
        static readonly string[] WorldSkyGames = { "tincan", "mhk3", "mhkt" };
        // Parts at least this wide (fraction of the widest part) form the dome's shell, as in FollowCameraPosition.
        const float ShellMinWidth = 0.5f;
        // Race, intro and attract cameras stay this close (m) to a racing line.
        const float CameraReach = 50f;

        readonly List<Material> copies = new();

        /// <summary>Whether the engine of <paramref name="sourceGame"/> (a pack id) kept its sky domes in the world.</summary>
        public static bool IsWorldSkyGame(string sourceGame) =>
            Array.Exists(WorldSkyGames, g => string.Equals(g, sourceGame, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Prepares the world sky domes of the loaded track <paramref name="scene"/>, before the race camera is set up.
        /// On a track of a world sky game a <see cref="FollowCameraPosition"/> becomes a world sky dome first.
        /// </summary>
        public static void Apply(Scene scene, TrackDefinition track, TrackRuntime runtime, GameConfig config)
        {
            var anchor = track != null && IsWorldSkyGame(track.SourceGame);
            var lit = config != null ? config.litShader : null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (anchor)
                    foreach (var follow in root.GetComponentsInChildren<FollowCameraPosition>(true))
                    {
                        if (!follow.TryGetComponent<WorldSkyDome>(out _)) follow.gameObject.AddComponent<WorldSkyDome>();
                        follow.enabled = false; // no LateUpdate before the deferred destroy
                        follow.RestorePlacement();
                        Destroy(follow);
                    }
                foreach (var dome in root.GetComponentsInChildren<WorldSkyDome>(true)) dome.Prepare(runtime, lit);
            }
        }

        void Prepare(TrackRuntime runtime, Shader lit)
        {
            var renderers = GetComponentsInChildren<MeshRenderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            var widest = 0f;
            foreach (var r in renderers)
            {
                bounds.Encapsulate(r.bounds);
                widest = Mathf.Max(widest, Width(r.bounds));
            }
            runtime.farClip = Mathf.Max(runtime.farClip, FarthestDistance(CameraArea(runtime, bounds), bounds));
            var made = new Dictionary<(Material, bool), Material>();
            foreach (var r in renderers)
            {
                var overlay = IsOverlay(r.bounds, widest);
                var materials = r.sharedMaterials;
                for (var i = 0; i < materials.Length; i++) materials[i] = SkyMaterial(materials[i], overlay, lit, made);
                r.sharedMaterials = materials;
            }
        }

        // Draw the shell before the world, with real depth: nearer track geometry covers it, farther geometry cannot.
        // Flares draw immediately after the shell and ignore its depth (their source meshes can lie outside it).
        // The world still draws after the flares, so they never paint over nearer track geometry. Weather cover ignores
        // both queues, which remain below Geometry.
        Material SkyMaterial(Material source, bool overlay, Shader lit, Dictionary<(Material, bool), Material> made)
        {
            if (source == null || source.shader == null || source.shader.name != MHKLitIds.ShaderName) return source;
            if (made.TryGetValue((source, overlay), out var copy)) return copy;
            copy = new Material(source) { name = source.name + " (world sky)" };
            if (lit != null) copy.shader = lit;
            ConfigureMaterial(copy, overlay);
            made.Add((source, overlay), copy);
            copies.Add(copy);
            return copy;
        }

        /// <summary>Smaller sky parts are additive flares, rather than the depth-occluding shell.</summary>
        public static bool IsOverlay(Bounds part, float shellWidth) => Width(part) < shellWidth * ShellMinWidth;

        /// <summary>
        /// Configures an owned MHK/Lit material for a world sky. Importers pass their material assets; the runtime
        /// passes private copies, so older bundles work without changing their source assets.
        /// </summary>
        public static void ConfigureMaterial(Material material, bool overlay)
        {
            if (material == null || material.shader == null || material.shader.name != MHKLitIds.ShaderName) return;
            material.renderQueue = (int)RenderQueue.Geometry - 100 + (overlay ? 1 : 0);
            material.SetFloat(MHKLitIds.Fog, 0f);
            material.SetFloat(MHKLitIds.SrcBlend, (float)BlendMode.One);
            material.SetFloat(MHKLitIds.DstBlend, overlay ? (float)BlendMode.One : (float)BlendMode.Zero);
            material.SetFloat(MHKLitIds.ZWrite, overlay ? 0f : 1f);
            material.SetFloat(MHKLitIds.ZTest, overlay ? (float)CompareFunction.Always : (float)CompareFunction.LessEqual);
            // These opaque-queue flares must not seed the depth prepass (MHK/Lit's DepthOnly writes unconditionally).
            material.SetShaderPassEnabled("DepthOnly", !overlay);
            material.SetOverrideTag("RenderType", overlay ? "Transparent" : "Opaque");
        }

        static float Width(Bounds b) => Mathf.Max(b.size.x, b.size.z);

        // Where cameras can be: around the racing lines, else anywhere in the dome.
        static Bounds CameraArea(TrackRuntime runtime, Bounds dome)
        {
            if (!runtime.HasMainPath) return dome;
            var area = runtime.MainPath.WorldBounds;
            foreach (var alt in runtime.Routes)
                if (alt != null && alt.Count > 0) area.Encapsulate(alt.WorldBounds);
            area.Expand(2f * CameraReach);
            return area;
        }

        // Upper bound of the distance between a point in a and one in b.
        static float FarthestDistance(Bounds a, Bounds b) => Vector3.Max(b.max - a.min, a.max - b.min).magnitude;

        void OnDestroy()
        {
            foreach (var m in copies) Destroy(m);
        }
    }
}
