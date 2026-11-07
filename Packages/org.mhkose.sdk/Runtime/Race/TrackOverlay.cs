using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>How an overlay changes one value of the track scene's environment.</summary>
    public enum OverrideMode
    {
        [Tooltip("Leave the value as the track scene (and earlier overlays) set it.")]
        Keep,
        [Tooltip("Replace the value.")]
        Set,
        [Tooltip("Multiply the value (colours per channel).")]
        Multiply,
    }

    /// <summary>A colour change of an overlay (alpha stays).</summary>
    [Serializable]
    public struct ColorOverride
    {
        public OverrideMode mode;
        public Color value;

        public ColorOverride(OverrideMode mode, Color value)
        {
            this.mode = mode;
            this.value = value;
        }

        /// <summary><paramref name="color"/> changed by this override.</summary>
        public Color Apply(Color color) => mode switch
        {
            OverrideMode.Set => new Color(value.r, value.g, value.b, color.a),
            OverrideMode.Multiply => new Color(color.r * value.r, color.g * value.g, color.b * value.b, color.a),
            _ => color,
        };
    }

    /// <summary>A number change of an overlay.</summary>
    [Serializable]
    public struct FloatOverride
    {
        public OverrideMode mode;
        public float value;

        public FloatOverride(OverrideMode mode, float value)
        {
            this.mode = mode;
            this.value = value;
        }

        /// <summary><paramref name="x"/> changed by this override.</summary>
        public float Apply(float x) => mode switch
        {
            OverrideMode.Set => value,
            OverrideMode.Multiply => x * value,
            _ => x,
        };
    }

    /// <summary>
    /// A sky material of the track scene as an overlay shows it: another texture, a tint, or hidden (a sun flare at night).
    /// Matched by material (or, for a copy loaded from another mod, by material name).
    /// </summary>
    [Serializable]
    public class SkyTexture
    {
        [Tooltip("Sky material of the track scene (on a SkyDome part).")]
        public Material material;
        [Tooltip("Texture shown instead. Empty = the material's own.")]
        public Texture2D texture;
        [Tooltip("Multiplies the material's colour.")]
        public Color tint = Color.white;
        [Tooltip("Hide the sky parts drawn with this material.")]
        public bool hide;
        [Tooltip("Only while this option (id) is selected too, e.g. a darker rain sky with 'core.night'. Such an entry beats " +
                 "one without. Empty = always.")]
        public string requiresOption = "";
    }

    /// <summary>
    /// Root of an overlay scene (<see cref="Modding.TrackOverlayDefinition.scene"/>), loaded on top of a track scene while
    /// its option is selected. Its child objects (rain, lamps, ramps) simply join the race; the settings here change the
    /// track scene's environment, which the overlay cannot reference directly: the track scene's sun (its shadowed
    /// directional light) and fill lights, ambient light, fog, background, baked light, sky textures and textures turned
    /// round (speed pad arrows in reverse). Overlays apply in modifier order, each on top of the result of the ones before
    /// (<see cref="TrackEnvironment"/>).
    /// </summary>
    [DisallowMultipleComponent]
    public class TrackOverlay : MonoBehaviour
    {
        /// <summary>Name of an overlay scene's root object.</summary>
        public const string RootName = "Overlay";

        [Header("Sun (the track scene's shadowed directional light)")]
        public ColorOverride sunColor;
        public FloatOverride sunIntensity;

        [Header("Fill lights (the other directional lights)")]
        public ColorOverride fillColor;
        public FloatOverride fillIntensity;

        [Header("Ambient light (flat)")]
        public ColorOverride ambient;

        [Header("Fog")]
        [Tooltip("A track scene without fog gets linear fog from Fog Start to Fog End (m), so the colour and distance below " +
                 "apply to it too (Fog Distance multiplies these as well: a sandstorm on a clear track). Tracks with fog keep theirs.")]
        public bool enableFog;
        [Min(0f)] public float fogStart = 30f;
        [Min(0f)] public float fogEnd = 400f;
        public ColorOverride fogColor;
        [Tooltip("Fog distance: linear start and end are multiplied by the value, an exponential density divided by it " +
                 "(below 1 = thicker fog).")]
        public FloatOverride fogDistance;

        [Header("Camera background (where nothing is drawn)")]
        public ColorOverride background;

        [Header("Baked light (BakedIndirectLighting scenes)")]
        [Tooltip("Intensity of the baked indirect light: lamps, windows and sky light baked into the track.")]
        public FloatOverride bakedIndirect;

        [Header("Sky")]
        public List<SkyTexture> skyTextures = new();

        [Header("Textures turned round")]
        [Tooltip("Materials of the track scene whose texture turns by 180° (and whose UV scroll runs the other way) wherever " +
                 "they are drawn: speed pad arrows of a reverse overlay. Matched by material (or material name).")]
        public List<Material> flipTextures = new();

        [Header("Weather")]
        [Tooltip("What covers the track, for wet options: rain wets and loosens only uncovered ground. Empty = nothing covers it.")]
        public TrackCoverMap cover;
    }
}
