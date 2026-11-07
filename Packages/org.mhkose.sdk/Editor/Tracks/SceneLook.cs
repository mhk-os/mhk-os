using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// The lighting look a scene keeps in its own RenderSettings (ambient light, fog, skybox), captured to copy it to another
    /// scene or to put it back.
    /// </summary>
    internal sealed class SceneLook
    {
        AmbientMode ambientMode;
        Color ambientLight, ambientEquator, ambientGround;
        SphericalHarmonicsL2 ambientProbe;
        float ambientIntensity;
        bool fog;
        FogMode fogMode;
        Color fogColor;
        float fogDensity, fogStart, fogEnd;
        Material skybox;

        /// <summary>The look of the loaded <paramref name="scene"/>.</summary>
        public static SceneLook Of(Scene scene)
        {
            var look = new SceneLook();
            TrackScenes.WithActive(scene, () =>
            {
                look.ambientMode = RenderSettings.ambientMode;
                look.ambientLight = RenderSettings.ambientLight;
                look.ambientEquator = RenderSettings.ambientEquatorColor;
                look.ambientGround = RenderSettings.ambientGroundColor;
                look.ambientProbe = RenderSettings.ambientProbe;
                look.ambientIntensity = RenderSettings.ambientIntensity;
                look.fog = RenderSettings.fog;
                look.fogMode = RenderSettings.fogMode;
                look.fogColor = RenderSettings.fogColor;
                look.fogDensity = RenderSettings.fogDensity;
                look.fogStart = RenderSettings.fogStartDistance;
                look.fogEnd = RenderSettings.fogEndDistance;
                look.skybox = RenderSettings.skybox;
            });
            return look;
        }

        /// <summary>Gives the loaded <paramref name="scene"/> this look.</summary>
        public void ApplyTo(Scene scene) =>
            TrackScenes.WithActive(scene, () =>
            {
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientEquatorColor = ambientEquator;
                RenderSettings.ambientGroundColor = ambientGround;
                RenderSettings.ambientProbe = ambientProbe;
                RenderSettings.ambientIntensity = ambientIntensity;
                RenderSettings.fog = fog;
                RenderSettings.fogMode = fogMode;
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.fogStartDistance = fogStart;
                RenderSettings.fogEndDistance = fogEnd;
                RenderSettings.skybox = skybox;
            });
    }
}
