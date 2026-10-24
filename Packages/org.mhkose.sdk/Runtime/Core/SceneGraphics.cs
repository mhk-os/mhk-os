using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MHK.Core
{
    /// <summary>Per-object graphics options on the loaded scenes: light shadow types and the base cameras' anti-aliasing and renderer.</summary>
    static partial class SceneGraphics
    {
        [AutoStaticsCleanup] static readonly Dictionary<Light, LightShadows> authoredShadows = new();
        [NoAutoStaticsCleanup] static readonly List<Light> deadLights = new();

        /// <summary>
        /// Directional lights get none, hard or soft shadows by level, point and spot lights keep their authored shadows
        /// only on High. Lights authored without shadows never get any.
        /// </summary>
        public static void ApplyLights(ShadowLevel level, bool urp)
        {
            ForgetDestroyedLights();
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (!authoredShadows.TryGetValue(light, out var authored)) authoredShadows[light] = authored = light.shadows;
                if (authored == LightShadows.None) continue;
                if (light.type != LightType.Directional)
                {
                    light.shadows = level == ShadowLevel.High ? authored : LightShadows.None;
                    continue;
                }
                light.shadows = DirectionalShadows(level);
                if (urp && level >= ShadowLevel.Medium)
                    light.GetUniversalAdditionalLightData().softShadowQuality =
                        level == ShadowLevel.High ? SoftShadowQuality.High : SoftShadowQuality.Low;
            }
        }

        /// <summary>FXAA (needs the camera's post-processing pass) and the renderer index on every base camera.</summary>
        public static void ApplyCameras(bool fxaa, int renderer)
        {
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            {
                var data = cam.GetUniversalAdditionalCameraData();
                if (data.renderType == CameraRenderType.Overlay) continue;
                data.antialiasing = fxaa ? AntialiasingMode.FastApproximateAntialiasing : AntialiasingMode.None;
                data.renderPostProcessing = fxaa;
                data.SetRenderer(renderer);
            }
        }

        static LightShadows DirectionalShadows(ShadowLevel level) => level switch
        {
            ShadowLevel.Off => LightShadows.None,
            ShadowLevel.Low => LightShadows.Hard,
            _ => LightShadows.Soft,
        };

        static void ForgetDestroyedLights()
        {
            foreach (var light in authoredShadows.Keys)
                if (light == null) deadLights.Add(light);
            foreach (var light in deadLights) authoredShadows.Remove(light);
            deadLights.Clear();
        }
    }
}
