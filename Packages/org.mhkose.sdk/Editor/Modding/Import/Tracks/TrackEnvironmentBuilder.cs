using System.IO;
using System.Linq;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Scene lighting from track.json "environment": sun, fill lights, ambient, fog, camera far clip and SkyDome (in the
    /// world for <see cref="WorldSkyDome.IsWorldSkyGame"/> packs, else centred on the camera).
    /// Ambient and light intensities are scaled by the settings because the originals lit in gamma space.
    /// </summary>
    public static class TrackEnvironmentBuilder
    {
        const float VerticalLimit = 0.99f;

        /// <summary>Applies the environment to the active scene and <paramref name="runtime"/>.</summary>
        public static void Apply(TrackImportContext c, TrackRuntime runtime)
        {
            var env = c.Json["environment"] as JObject ?? new JObject();
            ApplyAmbient(c.Settings, env);
            var lighting = new GameObject("Lighting").transform;
            CreateSun(c, env["sun"] as JObject, lighting);
            CreateFillLights(c.Settings, env, lighting);
            ApplyFog(c, env["fog"] as JObject);
            runtime.farClip = ExportJson.F(env["farClip"], c.Settings.defaultFarClip) * c.Scale;
            if (env["clearColor"] != null) runtime.backgroundColor = ExportJson.Col(env["clearColor"]);
            else if (RenderSettings.fog) runtime.backgroundColor = RenderSettings.fogColor;
            CreateSkyDome(c, ExportJson.Str(env["skybox"]));
        }

        static void ApplyAmbient(ExportImportSettings s, JObject env)
        {
            Lightmapping.lightingSettings = s.lightingSettings;
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = env["ambient"] != null ? (ExportJson.Col(env["ambient"]).linear * s.ambientScale).gamma : s.defaultAmbient;
        }

        static void CreateSun(TrackImportContext c, JObject sunJson, Transform lighting)
        {
            var s = c.Settings;
            var sun = CreateDirectionalLight(lighting, "Sun", LightShadows.Soft);
            sun.shadowStrength = s.sunShadowStrength;
            sun.color = sunJson != null ? ExportJson.Col(sunJson["color"]) : Color.white;
            sun.intensity = (sunJson != null ? ExportJson.F(sunJson["intensity"], 1f) : 1f) * s.lightScale;
            var dir = sunJson?["dir"] != null ? ExportJson.Dir(sunJson["dir"]).normalized : s.defaultSunDirection.normalized;
            // An upward vector points towards the light, Unity lights shine along their forward.
            if (dir.y > 0f) dir = -dir;
            sun.transform.rotation = LookAlong(dir);
            var pose = s.sunOverrides.FirstOrDefault(o => o != null && o.trackId == c.Id);
            if (pose != null) sun.transform.SetLocalPositionAndRotation(pose.position, pose.rotation.normalized);
            RenderSettings.sun = sun;
        }

        static void CreateFillLights(ExportImportSettings s, JObject env, Transform lighting)
        {
            var index = 0;
            foreach (var l in ExportJson.Items(env["extraLights"]))
            {
                if (ExportJson.Str(l["type"], "directional") != "directional" || l["dir"] == null) continue;
                var light = CreateDirectionalLight(lighting, "Fill Light " + ++index, LightShadows.None);
                light.color = ExportJson.Col(l["color"]);
                light.intensity = ExportJson.F(l["intensity"], 1f) * s.lightScale * s.fillScale;
                light.transform.rotation = LookAlong(ExportJson.Dir(l["dir"]).normalized);
            }
        }

        static Light CreateDirectionalLight(Transform parent, string name, LightShadows shadows)
        {
            var light = ImportHierarchy.CreateChild(parent, name).gameObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = shadows;
            return light;
        }

        static Quaternion LookAlong(Vector3 dir) => Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > VerticalLimit ? Vector3.forward : Vector3.up);

        static void ApplyFog(TrackImportContext c, JObject fog)
        {
            var s = c.Settings;
            RenderSettings.fog = fog != null && ExportJson.Str(fog["mode"], "exp") != "none" && ExportJson.B(fog["enabled"], true);
            if (!RenderSettings.fog) return;
            RenderSettings.fogColor = ExportJson.Col(fog["color"]);
            RenderSettings.fogMode = ExportJson.Str(fog["mode"], "exp") switch
            {
                "linear" => FogMode.Linear,
                "exp2" => FogMode.ExponentialSquared,
                _ => FogMode.Exponential,
            };
            RenderSettings.fogDensity = ExportJson.F(fog["density"], s.defaultFogDensity) / Mathf.Max(c.Scale, 1e-3f);
            RenderSettings.fogStartDistance = ExportJson.F(fog["start"], s.defaultFogStart) * c.Scale;
            RenderSettings.fogEndDistance = ExportJson.F(fog["end"], s.defaultFogEnd) * c.Scale;
        }

        static void CreateSkyDome(TrackImportContext c, string sky)
        {
            var skyModel = sky != null ? c.Model(sky, GlbLightingStyle.Sky) : null;
            if (skyModel == null) return;
            var dome = new GameObject("SkyDome");
            if (WorldSkyDome.IsWorldSkyGame(c.Settings.pack.packId)) dome.AddComponent<WorldSkyDome>();
            else dome.AddComponent<FollowCameraPosition>();
            var inst = c.Instance(skyModel, dome.transform, Path.GetFileNameWithoutExtension(sky));
            ImportHierarchy.StripAnimation(inst);
            foreach (var col in inst.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }
    }
}
