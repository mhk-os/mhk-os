using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>Graphics presets. Custom holds hand-picked values.</summary>
    public enum GraphicsPreset { Low, Medium, High, Custom }

    /// <summary>
    /// Height of the 3D image (vertical lines), upscaled to the screen. Auto renders at the screen height up to the
    /// limit of the preset tier (<see cref="GraphicsOptions.AutoRenderHeight"/>), the others are fixed caps.
    /// </summary>
    public enum RenderResolution
    {
        [InspectorName("540p")] P540,
        [InspectorName("720p")] P720,
        [InspectorName("1080p")] P1080,
        Native,
        Auto,
    }

    /// <summary>Desktop window mode (<see cref="ScreenSetup"/>). Exclusive fullscreen exists on Windows only.</summary>
    public enum ScreenMode { Fullscreen, Windowed, ExclusiveFullscreen }

    public enum ShadowLevel { Off, Low, Medium, High }
    public enum TextureLevel { Low, Medium, High }
    public enum AntiAliasing { Off, FXAA, MSAA2x, MSAA4x }

    /// <summary>
    /// Player graphics settings (part of <see cref="UserSettings"/>), applied by <see cref="GraphicsQuality"/> and
    /// <see cref="ScreenSetup"/>. Defaults are the Low preset at 30 fps on the Auto screen size. Enums are stored as
    /// numbers in settings.json: only append new values.
    /// </summary>
    [Serializable]
    public class GraphicsOptions
    {
        /// <summary>Layout of these options, <see cref="version"/> of every file written by this build.</summary>
        public const int CurrentVersion = 1;

        [Tooltip("Preset the options below came from, Custom once any of them was changed by hand.")]
        public GraphicsPreset preset = GraphicsPreset.Low;
        [Tooltip("Frame-rate cap (one of FrameRateChoices), 0 = unlimited (display refresh rate on mobile). Not part of presets.")]
        public int maxFps = 30;
        [Tooltip("Desktop only: sync to the display (maxFps then picks the refresh-rate divider). Not part of presets.")]
        public bool vSync;
        [Tooltip("Desktop only: output width in pixels, 0 = Auto (the main display's native size). Not part of presets.")]
        public int screenWidth;
        [Tooltip("Desktop only: output height in pixels, 0 = Auto. Not part of presets.")]
        public int screenHeight;
        [Tooltip("Desktop only: fullscreen window, resizable window or exclusive fullscreen (Windows). Not part of presets.")]
        public ScreenMode screenMode = ScreenMode.Fullscreen;
        [Tooltip("Render resolution: height of the 3D image, Auto = the screen height up to the preset tier's limit. The JSON key predates the screen size.")]
        public RenderResolution resolution = RenderResolution.Auto;
        public ShadowLevel shadows = ShadowLevel.Low;
        public TextureLevel textures = TextureLevel.Medium;
        public AntiAliasing antiAliasing = AntiAliasing.Off;
        [Tooltip("Screen-space ambient occlusion (second renderer of the URP asset).")]
        public bool ambientOcclusion;
        [Tooltip("The player's own values, restored when the Custom preset is chosen again.")]
        public PresetValues custom;
        [Tooltip("Whether custom holds values the player set (else choosing Custom keeps the current values).")]
        public bool hasCustom;
        [Tooltip("Layout of the file these options came from, 0 = before the screen size setting. Upgrade migrates it.")]
        public int version;

        static readonly int[] FrameRates = { 30, 60, 120, 0 };

        /// <summary>Frame-rate cap choices offered in the menus, 0 = unlimited.</summary>
        public static IReadOnlyList<int> FrameRateChoices => FrameRates;

        /// <summary>The options a preset writes.</summary>
        [Serializable]
        public struct PresetValues
        {
            public RenderResolution resolution;
            public ShadowLevel shadows;
            public TextureLevel textures;
            public AntiAliasing antiAliasing;
            public bool ambientOcclusion;
        }

        // Values of each preset, indexed by GraphicsPreset (Low, Medium, High). Tune here, with the Auto render limits below.
        static readonly PresetValues[] Presets =
        {
            new() { resolution = RenderResolution.Auto, shadows = ShadowLevel.Low, textures = TextureLevel.Medium, antiAliasing = AntiAliasing.Off, ambientOcclusion = false },
            new() { resolution = RenderResolution.Auto, shadows = ShadowLevel.Medium, textures = TextureLevel.High, antiAliasing = AntiAliasing.MSAA2x, ambientOcclusion = false },
            new() { resolution = RenderResolution.Auto, shadows = ShadowLevel.High, textures = TextureLevel.High, antiAliasing = AntiAliasing.MSAA4x, ambientOcclusion = true },
        };

        // Highest Auto render height per tier (Low, Medium, High). Mobile GPUs pay more per pixel and sit closer to the eye.
        static readonly int[] DesktopAutoHeights = { 1080, 1440, 2160 };
        static readonly int[] MobileAutoHeights = { 720, 1080, 1440 };

        /// <summary>Index of <paramref name="fps"/> in <see cref="FrameRateChoices"/> (the first for unknown values).</summary>
        public static int FrameRateIndex(int fps) => Mathf.Max(0, Array.IndexOf(FrameRates, fps));

        /// <summary>
        /// Switches to <paramref name="p"/> and writes its values. <see cref="GraphicsPreset.Custom"/> restores the values
        /// last set by hand (or keeps the current ones if there are none yet).
        /// </summary>
        public void SetPreset(GraphicsPreset p)
        {
            preset = p;
            if (p == GraphicsPreset.Custom)
            {
                if (hasCustom) Write(custom);
                else MarkCustom();
                return;
            }
            Write(Presets[(int)p]);
        }

        /// <summary>Call after the player changed an option by hand: the preset becomes Custom and remembers the values.</summary>
        public void MarkCustom()
        {
            preset = GraphicsPreset.Custom;
            custom = Snapshot();
            hasCustom = true;
        }

        /// <summary>
        /// Migrates options read from an older settings file (also a fresh profile, harmless there). Version 0 presets
        /// capped the render at 720p or 1080p, they now use their Auto value. A Custom cap was the player's choice and stays.
        /// </summary>
        public void Upgrade()
        {
            if (version >= CurrentVersion) return;
            if (preset != GraphicsPreset.Custom) resolution = Presets[(int)preset].resolution;
            version = CurrentVersion;
        }

        PresetValues Snapshot() => new()
        {
            resolution = resolution, shadows = shadows, textures = textures, antiAliasing = antiAliasing, ambientOcclusion = ambientOcclusion,
        };

        void Write(in PresetValues v)
        {
            resolution = v.resolution;
            shadows = v.shadows;
            textures = v.textures;
            antiAliasing = v.antiAliasing;
            ambientOcclusion = v.ambientOcclusion;
        }

        public GraphicsOptions Clone() => (GraphicsOptions)MemberwiseClone();

        /// <summary>Cap height in pixels of a fixed choice, 0 for <see cref="RenderResolution.Native"/> and <see cref="RenderResolution.Auto"/>.</summary>
        public static int CapHeight(RenderResolution r) => r switch
        {
            RenderResolution.P540 => 540,
            RenderResolution.P720 => 720,
            RenderResolution.P1080 => 1080,
            _ => 0,
        };

        /// <summary>Highest render height <see cref="RenderResolution.Auto"/> uses at <paramref name="tier"/> (Low, Medium or High).</summary>
        public static int AutoRenderHeight(GraphicsPreset tier, bool mobile)
        {
            var heights = mobile ? MobileAutoHeights : DesktopAutoHeights;
            return heights[Mathf.Clamp((int)tier, 0, heights.Length - 1)];
        }
    }
}
