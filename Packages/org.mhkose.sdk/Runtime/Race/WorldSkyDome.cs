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
    /// Sky dome that stays where the scene placed it and surrounds the whole track, as in MHK3 and MHK Thunder (MHK2 and
    /// MHK Extra centred theirs on the camera: <see cref="FollowCameraPosition"/>). At track load the race camera's far
    /// clip is raised to reach the dome from anywhere near the racing lines and the dome's MHK/Lit materials are drawn
    /// unfogged. Parts much narrower than the dome (sun flares) glow additively in front of it.
    /// </summary>
    public sealed class WorldSkyDome : MonoBehaviour
    {
        // Source games whose engines kept the sky dome in the world. Their tracks imported by older SDKs carry a
        // FollowCameraPosition instead.
        static readonly string[] WorldSkyGames = { "mhk3", "mhkt" };
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
                var overlay = Width(r.bounds) < widest * ShellMinWidth;
                var materials = r.sharedMaterials;
                for (var i = 0; i < materials.Length; i++) materials[i] = SkyMaterial(materials[i], overlay, lit, made);
                r.sharedMaterials = materials;
            }
        }

        // Unfogged copy of an MHK/Lit material on the game's MHK/Lit (other shaders stay), additive and drawn after the
        // shell for overlay parts. The shell keeps its queue below Geometry and draws without depth, so the world
        // always covers it (weather cover ignores it).
        Material SkyMaterial(Material source, bool overlay, Shader lit, Dictionary<(Material, bool), Material> made)
        {
            if (source == null || source.shader == null || source.shader.name != MHKLitIds.ShaderName) return source;
            if (made.TryGetValue((source, overlay), out var copy)) return copy;
            copy = new Material(source) { name = source.name + " (world sky)" };
            if (lit != null) copy.shader = lit;
            copy.renderQueue = source.renderQueue + (overlay ? 1 : 0);
            copy.SetFloat(MHKLitIds.Fog, 0f);
            if (overlay)
            {
                copy.SetFloat(MHKLitIds.SrcBlend, (float)BlendMode.One);
                copy.SetFloat(MHKLitIds.DstBlend, (float)BlendMode.One);
                copy.SetFloat(MHKLitIds.ZWrite, 0f);
            }
            made.Add((source, overlay), copy);
            copies.Add(copy);
            return copy;
        }

        static float Width(Bounds b) => Mathf.Max(b.size.x, b.size.z);

        // Where cameras can be: around the racing lines, else anywhere in the dome.
        static Bounds CameraArea(TrackRuntime runtime, Bounds dome)
        {
            if (!runtime.HasMainPath) return dome;
            var area = runtime.path.WorldBounds;
            foreach (var alt in runtime.alternatePaths)
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
