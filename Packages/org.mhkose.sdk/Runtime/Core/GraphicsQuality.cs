using MHK.Lighting;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Application = UnityEngine.Device.Application;
using Screen = UnityEngine.Device.Screen;

namespace MHK.Core
{
    /// <summary>Detail of the race camera's screen-edge speed effect (<see cref="GraphicsQuality.SpeedEffect"/>).</summary>
    public enum SpeedEffectLevel { Off, Medium, High }

    /// <summary>
    /// Applies <see cref="GraphicsOptions"/>: the Quality level and URP asset of the preset, frame pacing, texture
    /// quality and the lights and cameras of loaded scenes. Never writes project assets.
    /// </summary>
    public static partial class GraphicsQuality
    {
        const int FallbackRefreshRate = 60;
        const int MaxVSyncDivider = 4;
        const int LowTextureMipLimit = 1;

        [AutoStaticsCleanup] static GraphicsOptions current;
        [AutoStaticsCleanup] static int appliedScreenHeight;

        /// <summary>The active URP asset has the ambient-occlusion renderer (index 1) next to the normal one (index 0).</summary>
        public static bool AmbientOcclusionAvailable
        {
            get
            {
                var asset = QualityLevels.Active;
                return asset != null && asset.rendererDataList.Length > 1;
            }
        }

        /// <summary>
        /// Detail of the speed effect: off at Low, fewer blur samples at Medium, full at High. Custom follows its shadow
        /// level, full detail when no options were applied (editor scenes).
        /// </summary>
        public static SpeedEffectLevel SpeedEffect => current == null ? SpeedEffectLevel.High : TierOf(current) switch
        {
            GraphicsPreset.High => SpeedEffectLevel.High,
            GraphicsPreset.Medium => SpeedEffectLevel.Medium,
            _ => SpeedEffectLevel.Off,
        };

        /// <summary>
        /// Render-resolution tier of the applied options: 0 = 720p or lower, 1 = 1080p, 2 = native or none applied. Auto
        /// counts as its preset tier (Low 0, Medium 1, High 2).
        /// </summary>
        public static int ResolutionTier => current == null ? 2 : current.resolution switch
        {
            RenderResolution.Native => 2,
            RenderResolution.P1080 => 1,
            RenderResolution.Auto => (int)TierOf(current),
            _ => 0,
        };

        /// <summary>Preset tier of the applied options (Custom by its shadow quality), High when none were applied (editor scenes).</summary>
        public static GraphicsPreset Tier => current == null ? GraphicsPreset.High : TierOf(current);

        /// <summary>Applies <paramref name="o"/> (startup and every change). A Custom setup renders with the level of its shadow quality.</summary>
        public static void Apply(GraphicsOptions o)
        {
            current = o;
            QualityLevels.Select(TierOf(o), RenderScale(o), MsaaSamples(o.antiAliasing));
            ApplyFramePacing(o);
            ApplyTextures(o.textures);
            ApplyToScene();
        }

        /// <summary>Applies the per-object settings (light shadows, camera anti-aliasing and renderer) to all loaded scenes.</summary>
        public static void ApplyToScene()
        {
            if (current == null) return;
            var asset = QualityLevels.Active;
            SceneGraphics.ApplyLights(current.shadows, asset != null);
            if (asset == null) return;
            var renderer = current.ambientOcclusion && asset.rendererDataList.Length > 1 ? 1 : 0;
            SceneGraphics.ApplyCameras(current.antiAliasing == AntiAliasing.FXAA, renderer);
        }

        /// <summary>Per frame: keeps the ambient cube current and the render height matched to the window height.</summary>
        public static void Tick()
        {
            AmbientCube.Update();
            if (current == null || Screen.height == appliedScreenHeight) return;
            QualityLevels.SetRenderScale(RenderScale(current));
        }

        /// <summary>Height limit of the 3D image of <paramref name="o"/> in pixels, 0 = none (the screen height).</summary>
        public static int RenderHeightLimit(GraphicsOptions o) => o.resolution == RenderResolution.Auto
            ? GraphicsOptions.AutoRenderHeight(TierOf(o), Application.isMobilePlatform)
            : GraphicsOptions.CapHeight(o.resolution);

        /// <summary>Height in pixels the 3D image of <paramref name="o"/> renders at on the current screen.</summary>
        public static int RenderHeight(GraphicsOptions o)
        {
            var limit = RenderHeightLimit(o);
            return limit > 0 ? Mathf.Min(limit, Screen.height) : Screen.height;
        }

        /// <summary>Height in pixels <see cref="RenderResolution.Auto"/> would render at on the current screen with the tier of <paramref name="o"/>.</summary>
        public static int AutoRenderHeight(GraphicsOptions o) =>
            Mathf.Min(GraphicsOptions.AutoRenderHeight(TierOf(o), Application.isMobilePlatform), Screen.height);

        static GraphicsPreset TierOf(GraphicsOptions o) => o.preset != GraphicsPreset.Custom ? o.preset : o.shadows switch
        {
            ShadowLevel.High => GraphicsPreset.High,
            ShadowLevel.Medium => GraphicsPreset.Medium,
            _ => GraphicsPreset.Low,
        };

        static float RenderScale(GraphicsOptions o)
        {
            appliedScreenHeight = Screen.height;
            var limit = RenderHeightLimit(o);
            var scale = limit > 0 && appliedScreenHeight > 0 ? Mathf.Min(1f, limit / (float)appliedScreenHeight) : 1f;
            if (!UnityEngine.Application.isEditor)
                Debug.Log(System.FormattableString.Invariant($"[Graphics] Screen {Screen.width}x{appliedScreenHeight}, 3D render height {RenderHeight(o)} (scale {scale:0.###}, {o.resolution}, {TierOf(o)} tier)."));
            return scale;
        }

        static int MsaaSamples(AntiAliasing aa) => aa switch
        {
            AntiAliasing.MSAA2x => 2,
            AntiAliasing.MSAA4x => 4,
            _ => 1,
        };

        static void ApplyTextures(TextureLevel textures)
        {
            QualitySettings.globalTextureMipmapLimit = textures == TextureLevel.Low ? LowTextureMipLimit : 0;
            QualitySettings.anisotropicFiltering = textures switch
            {
                TextureLevel.Low => AnisotropicFiltering.Disable,
                TextureLevel.Medium => AnisotropicFiltering.Enable,
                _ => AnisotropicFiltering.ForceEnable,
            };
        }

        static void ApplyFramePacing(GraphicsOptions o)
        {
            var refresh = Mathf.RoundToInt((float)Screen.currentResolution.refreshRateRatio.value);
            if (refresh <= 0) refresh = FallbackRefreshRate;
            if (Application.isMobilePlatform)
            {
                // Mobile cannot run uncapped meaningfully: "unlimited" means the display refresh rate.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = o.maxFps > 0 ? o.maxFps : refresh;
            }
            else if (o.vSync)
            {
                QualitySettings.vSyncCount = o.maxFps > 0 ? Mathf.Clamp(Mathf.RoundToInt(refresh / (float)o.maxFps), 1, MaxVSyncDivider) : 1;
                Application.targetFrameRate = -1;
            }
            else
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = o.maxFps > 0 ? o.maxFps : -1;
            }
        }
    }
}
