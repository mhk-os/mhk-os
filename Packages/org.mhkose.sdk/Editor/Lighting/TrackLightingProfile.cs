using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Lighting
{
    /// <summary>
    /// Per-track baked-light setup (BakedIndirectLighting, environment overrides, glowing emitters and their baked
    /// lights), applied by <see cref="TrackLightingProfiles"/> on every Prepare and Bake.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Track Lighting Profile", fileName = "TrackLightingProfile")]
    public class TrackLightingProfile : ScriptableObject
    {
        [Tooltip("One entry per track scene.")]
        public List<Track> tracks = new();

        /// <summary>The entry for the scene at <paramref name="scenePath"/>, else null.</summary>
        public Track Find(string scenePath)
        {
            foreach (var t in tracks)
                if (t.scene != null && AssetDatabase.GetAssetPath(t.scene) == scenePath)
                    return t;
            return null;
        }

        /// <summary>Lighting of one track scene.</summary>
        [Serializable]
        public class Track
        {
            [Tooltip("Track scene this entry applies to.")]
            public SceneAsset scene;

            [Header("Baked indirect light")]
            [Tooltip("Give the scene a BakedIndirectLighting (ambient replaced by the bake: sky light blocked by roofs, " +
                     "baked lights, glow). Off removes it again.")]
            public bool bakedIndirect = true;
            [Tooltip("BakedIndirectLighting.intensity: multiplier of the baked light (no re-bake needed).")]
            [Min(0f)] public float indirectIntensity = 1f;
            [Tooltip("BakedIndirectLighting.minimum: light level (0 to 1) of places no lamp, window or sky reaches.")]
            [Range(0f, 0.5f)] public float minimum = 0.1f;
            [Tooltip("BakedIndirectLighting.sunShadowStrength: 1 keeps the sun out of roofed areas completely.")]
            [Range(0f, 1f)] public float sunShadowStrength = 1f;
            [Tooltip("BakedIndirectLighting.lightmapMaxSize: size cap (texels per side) of the colour lightmaps.")]
            [Min(0)] public int lightmapMaxSize = 512;

            [Header("Environment")]
            [Tooltip("Set the scene's ambient colour (flat). In a baked-indirect scene this is the sky light of the bake.")]
            public bool overrideAmbient;
            [Tooltip("Ambient colour when Override Ambient is on.")]
            public Color ambientColor = new(0.3f, 0.3f, 0.3f);
            [Tooltip("Set the sun's rotation (the scene's shadowed directional light).")]
            public bool overrideSunRotation;
            [Tooltip("Sun rotation (Euler angles) when Override Sun Rotation is on.")]
            public Vector3 sunRotation;
            [Tooltip("Sun intensity. Negative keeps the scene's.")]
            public float sunIntensity = -1f;
            [Tooltip("Intensity of the unshadowed directional fill lights (baked with shadows in baked-indirect scenes). Negative keeps the scene's.")]
            public float fillIntensity = -1f;
            [Tooltip("Glob (as the emitter object patterns) of static objects kept out of the bake and lit like the open air " +
                     "(scene ambient, sun without baked shadows) and casting no shadows: backdrops seen through windows " +
                     "(a skyline). Empty = none.")]
            public string unbakedObjects = "";

            [Header("Light sources")]
            [Tooltip("Lamps, lit windows and other surfaces that give off light.")]
            public List<Emitter> emitters = new();
        }

        /// <summary>Kind of baked light placed at each glowing surface cluster.</summary>
        public enum LightShape
        {
            /// <summary>Glow only.</summary>
            None,
            Point,
            Spot,
            /// <summary>Rectangle area light sized to the cluster (baked only).</summary>
            Area,
        }

        /// <summary>Direction baked spot / area lights face, and the offset direction of every light.</summary>
        public enum LightFacing
        {
            /// <summary>Area-weighted normal of the glowing faces, straight down when they face every way (closed tubes).</summary>
            SurfaceNormal,
            Down,
            Up,
        }

        /// <summary>
        /// Surfaces that give off light: materials matching <see cref="materialPattern"/> on renderers matching
        /// <see cref="objectPattern"/>. Glow is a material setting (every use of the material in the track glows),
        /// lights are placed only at the matching renderers' faces.
        /// </summary>
        [Serializable]
        public class Emitter
        {
            [Tooltip("Name of the generated lights.")]
            public string label = "Lamp";
            [Tooltip("Glob (* and ?) on the renderer's object name or any parent's below the track root, case-insensitive, " +
                     "spaces and underscores alike. Several patterns are separated by '|'. Empty = any object.")]
            public string objectPattern = "";
            [Tooltip("Glob on the material names (same rules). Empty = every material of the matching objects.")]
            public string materialPattern = "";

            [Header("Glow")]
            [Tooltip("Turn on MHK/Lit Glow on the matching materials (drawn bright and emitting light into the bake).")]
            public bool glow = true;
            [Tooltip("Glow colour (HDR): added to the drawn colour (texture × colour).")]
            [ColorUsage(false, true)] public Color glowColor = Color.white;
            [Tooltip("Glow light in the bake, a multiplier of the glow colour. Large surfaces such as windows need less than small lamps.")]
            [Min(0f)] public float glowBake = 4f;
            [Tooltip("Glow mask (RGB × texture: white glows). None = the whole surface. With baked lights, only faces on its white parts get lights (lamps painted into a texture).")]
            public Texture2D glowMap;

            [Header("Baked lights")]
            [Tooltip("Baked light placed at every cluster of matching faces (connected faces closer than Merge Distance form one).")]
            public LightShape lights = LightShape.None;
            [Tooltip("Light colour.")]
            public Color lightColor = Color.white;
            [Tooltip("Light intensity (inverse-square falloff).")]
            [Min(0f)] public float lightIntensity = 2f;
            [Tooltip("Light range (m).")]
            [Min(0.1f)] public float lightRange = 10f;
            [Tooltip("Spot lights: cone angle (degrees).")]
            [Range(1f, 179f)] public float spotAngle = 120f;
            [Tooltip("Where the lights face (spot / area) and are moved off the surface.")]
            public LightFacing facing = LightFacing.SurfaceNormal;
            [Tooltip("Distance (m) the light is moved off the glowing surface along its facing, out of the lamp's own geometry.")]
            [Min(0f)] public float offset = 0.3f;
            [Tooltip("Clusters whose centres are closer than this (m) share one light.")]
            [Min(0f)] public float mergeDistance = 1f;
            [Tooltip("Clusters smaller than this (m², glowing face area) get no light.")]
            [Min(0f)] public float minArea;
            [Tooltip("Baked shadow softness: radius (m) of point / spot lights.")]
            [Min(0f)] public float shadowRadius = 0.1f;
        }
    }
}
