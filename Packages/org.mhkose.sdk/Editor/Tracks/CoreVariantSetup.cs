using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Core;
using MHK.Effects;
using MHK.Modding;
using MHK.Race;
using MHK.Rendering;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Sets up the track variants of the official mods: <c>-executeMethod MHK.Editor.Tracks.CoreVariantSetup.Run
    /// -mhkVariantSpec &lt;json&gt;</c> (with <c>-batchmode -quit</c>) in a mod project. The spec maps mod ids to their
    /// tracks: <c>{ "mhk2": [ { "track", "scene", "folder", "definition", "options": [ { "option", "default", "scene" } ] } ] }</c>,
    /// paths as in the workspace (the part from "Packages/" on is the project path). For every track of a mod in this project
    /// it moves the track scene into the <see cref="TrackSceneLayout"/>, writes one <see cref="TrackOverlayDefinition"/> per
    /// option into the track's Variants folder and the mod's pack, and builds the overlay scenes of the options with a scene
    /// from the track scene: night, day (on night tracks), rain, snow and sandstorm with their sky textures, weather effect
    /// and cover map, and reverse with the speed pad materials to turn round. Core options the spec no longer lists for a
    /// track lose their definitions, scenes and cover map. A rerun updates everything in place.
    /// </summary>
    public static class CoreVariantSetup
    {
        /// <summary>Command-line option: path of the spec file.</summary>
        public const string SpecOption = "-mhkVariantSpec";

        const int FailureExitCode = 1;
        const string ModPackagePrefix = "Packages/org.mhkose.mod.";
        const string CorePrefix = "core.";
        const string ReverseOption = "core.reverse", DayOption = "core.day", NightOption = "core.night";
        const string RainOption = "core.rain", SnowOption = "core.snow", SandstormOption = "core.sandstorm";
        const string SkyTextureFolder = "Textures/Sky";
        // A sky part this flat (height / width) and below the dome's middle is ground or water, darkened at night.
        const float GroundFlatness = 0.1f;
        // A track without a time option looks like night when its ambient light and fog are darker than this (grey value).
        const float NightBrightness = 0.4f;
        // A material is a speed pad's when more than this share of its triangles lies in speed pad zones, whose boxes count
        // this many metres higher and lower (the pad's surface lies on the ground, the zone's box over it).
        const float PadShare = 0.5f, PadReach = 1f;
        // Smallest colour channel a weather colour is divided by.
        const float MinChannel = 0.05f;

        static readonly Color NightSunColor = new(0.55f, 0.65f, 1f);
        static readonly Color NightAmbient = new(0.28f, 0.32f, 0.45f);
        static readonly Color NightFog = new(0.12f, 0.15f, 0.25f);
        static readonly Color NightGroundTint = new(0.2f, 0.22f, 0.3f);
        static readonly Color DaySunColor = new(1f, 0.96f, 0.88f);
        static readonly Color DayAmbient = new(0.55f, 0.57f, 0.6f);
        static readonly Color DayFog = new(0.62f, 0.72f, 0.85f);
        static readonly Color RainSunColor = new(0.85f, 0.88f, 0.95f);
        static readonly Color RainAmbient = new(0.8f, 0.82f, 0.88f);
        static readonly Color RainFog = new(0.8f, 0.82f, 0.85f);
        static readonly Color RainBackground = new(0.75f, 0.78f, 0.82f);
        static readonly Color SnowSunColor = new(0.9f, 0.95f, 1.05f);
        static readonly Color SnowAmbient = new(1.05f, 1.08f, 1.15f);
        static readonly Color SnowFog = new(1.1f, 1.12f, 1.18f);
        static readonly Color SnowBackground = new(0.9f, 0.92f, 0.98f);
        static readonly Color SandSunColor = new(1f, 0.85f, 0.62f);
        static readonly Color SandAmbient = new(1.05f, 0.9f, 0.7f);
        // Fog and background of a sandstorm by day; at night the time overlay's darkening carries over.
        static readonly Color SandFog = new(0.72f, 0.56f, 0.36f);

        /// <summary>The <c>-executeMethod</c> entry: reads the spec option, sets up the tracks and exits (batch mode only, else it only logs).</summary>
        public static void Run()
        {
            var ok = false;
            try
            {
                var spec = CommandLine.Value(SpecOption);
                if (string.IsNullOrEmpty(spec)) Debug.LogError($"[Variants] {SpecOption} <spec json path> is missing.");
                else ok = Run(spec);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : FailureExitCode);
        }

        /// <summary>
        /// Sets up the tracks of the mods in the spec file at <paramref name="specPath"/> whose package is in this project.
        /// Returns false when a track failed or no mod of the spec is in the project.
        /// </summary>
        public static bool Run(string specPath)
        {
            var spec = JObject.Parse(File.ReadAllText(specPath));
            var ok = true;
            var tracks = 0;
            foreach (var mod in spec)
            {
                var root = ModPackagePrefix + mod.Key;
                if (!AssetDatabase.IsValidFolder(root)) continue;
                var pack = EditorAssets.FindAll<ContentPack>(root).FirstOrDefault(p => AssetPaths.DirectoryOf(p) == root);
                if (pack == null)
                {
                    Debug.LogError($"[Variants] {root}: no pack asset in the package root.");
                    ok = false;
                    continue;
                }
                foreach (var entry in (mod.Value as JArray ?? new JArray()).OfType<JObject>())
                {
                    try
                    {
                        ok &= SetUp(entry, AssetDatabase.GetAssetPath(pack));
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        ok = false;
                    }
                    tracks++;
                }
            }
            AssetDatabase.SaveAssets();
            if (tracks == 0)
            {
                Debug.LogError($"[Variants] {specPath}: no mod of the spec is in this project.");
                return false;
            }
            Debug.Log($"[Variants] {tracks} tracks set up{(ok ? "" : ", with errors (see above)")}.");
            return ok;
        }

        sealed class OptionSpec
        {
            public TrackOptionDefinition option;
            public string id;
            public bool isDefault, scene;
        }

        // A material of the track scene's sky dome and how it is drawn.
        sealed class SkyMaterial
        {
            public Material material;
            public Texture texture;
            public bool flare = true, ground = true;
        }

        // What the presets read from the loaded track scene, once per track.
        sealed class TrackLook
        {
            public Scene scene;
            public string folder;
            // The time options the track offers.
            public List<string> times;
            // The track scene shows night.
            public bool night;
            public Color fog, background;
            // Fog and background a weather overlay starts from by day: the track scene's on a day track, else the day
            // overlay's.
            public Color dayFog, dayBackground;
            public List<SkyMaterial> sky;
        }

        // A speed pad zone's box (TrackZone Boost), reaching PadReach metres further up and down.
        readonly struct PadBox
        {
            readonly Matrix4x4 toBox;
            readonly Vector3 half;
            public readonly Bounds bounds;

            public PadBox(BoxCollider box)
            {
                var t = box.transform;
                half = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z)) * 0.5f;
                half.y += PadReach / Mathf.Max(Mathf.Abs(t.lossyScale.y), 1e-4f);
                toBox = Matrix4x4.Translate(-box.center) * t.worldToLocalMatrix;
                var toWorld = t.localToWorldMatrix;
                bounds = new Bounds(toWorld.MultiplyPoint3x4(box.center), Vector3.zero);
                for (var i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
                    bounds.Encapsulate(toWorld.MultiplyPoint3x4(box.center + corner));
                }
            }

            public bool Contains(Vector3 p)
            {
                var local = toBox.MultiplyPoint3x4(p);
                return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
            }
        }

        // packPath: the mod's pack asset, loaded again after the track scene opened (opening a scene alone unloads unused
        // assets, so an object loaded before would be a stale copy whose changes are never saved).
        static bool SetUp(JObject entry, string packPath)
        {
            var trackId = (string)entry["track"];
            var folder = ProjectPath((string)entry["folder"]);
            var definitionPath = ProjectPath((string)entry["definition"]);
            var track = AssetDatabase.LoadAssetAtPath<TrackDefinition>(definitionPath);
            if (track == null || !string.Equals(track.Id, trackId, StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[Variants] {definitionPath}: no track definition '{trackId}'.");
                return false;
            }
            var scenePath = track.scene.IsSet ? track.scene.Path : $"{folder}/{(string)entry["scene"]}.unity";
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
            {
                Debug.LogError($"[Variants] {trackId}: track scene {scenePath} not found.");
                return false;
            }
            var options = new List<OptionSpec>();
            foreach (var o in (entry["options"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var id = (string)o["option"];
                var option = TrackOverlayAssets.Option(id);
                if (option == null)
                {
                    Debug.LogError($"[Variants] {trackId}: no track option '{id}' in the project.");
                    return false;
                }
                options.Add(new OptionSpec { id = id, option = option, isDefault = (bool?)o["default"] ?? false, scene = (bool?)o["scene"] ?? false });
            }

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            TrackSceneLayout.Migrate(scene);
            var pack = AssetDatabase.LoadAssetAtPath<ContentPack>(packPath);
            var packFolder = AssetPaths.DirectoryOf(pack);
            track = AssetDatabase.LoadAssetAtPath<TrackDefinition>(definitionPath);
            foreach (var o in options) o.option = TrackOverlayAssets.Option(o.id);
            RemoveDropped(track, options, pack, scene, folder);
            var look = ReadLook(scene, folder, options);
            var made = new List<string>();
            foreach (var o in options)
            {
                var def = TrackOverlayAssets.CreateOrUpdate(track, o.option, o.isDefault, folder, pack);
                var pads = o.scene && o.option.Id == ReverseOption ? SpeedPadMaterials(scene, trackId) : null;
                if (o.scene && (pads == null || pads.Count > 0)) made.Add(BuildOverlayScene(def, o.option, look, pads));
                else
                    foreach (var path in TrackOverlayAssets.RemoveScene(def, scene.path, packFolder))
                        Debug.Log($"[Variants] {trackId}: '{o.option.Id}' has no overlay scene, removed {path}.");
            }
            if (scene.isDirty) EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Variants] {trackId}: {options.Count} options, overlay scenes {(made.Count > 0 ? string.Join(", ", made) : "none")}.");
            return true;
        }

        // Removes what the spec no longer asks for on the track: the overlay definitions of core options in the pack's folder
        // (from the pack's content and from disk, with their scenes), leftover overlay scenes of those options, and the cover
        // map when no option needs one. Overlays of other options or other packs stay.
        static void RemoveDropped(TrackDefinition track, List<OptionSpec> options, ContentPack pack, Scene trackScene, string folder)
        {
            var scope = AssetPaths.DirectoryOf(pack);
            bool Listed(string optionId) => options.Any(o => string.Equals(o.option.Id, optionId, StringComparison.OrdinalIgnoreCase));
            bool Core(string optionId) => !string.IsNullOrEmpty(optionId) && optionId.StartsWith(CorePrefix, StringComparison.OrdinalIgnoreCase);
            foreach (var def in TrackOverlayAssets.ForTrack(track))
            {
                var path = AssetDatabase.GetAssetPath(def);
                if (!Core(def.optionId) || Listed(def.optionId) || !path.StartsWith(scope + "/", StringComparison.Ordinal)) continue;
                TrackOverlayAssets.Delete(def, pack, trackScene.path);
                Debug.Log($"[Variants] {track.Id}: '{def.optionId}' is no longer offered, removed {path} and its scene.");
            }
            foreach (var option in TrackOverlayAssets.AllOptions().Where(o => Core(o.Id) && !Listed(o.Id)))
            {
                var path = TrackOverlayAssets.ScenePath(trackScene.path, option.Id);
                if (TrackOverlayAssets.DeleteScene(path, scope)) Debug.Log($"[Variants] {track.Id}: '{option.Id}' is no longer offered, removed {path}.");
            }
            var cover = CoverPath(folder, trackScene);
            if (!options.Any(o => NeedsCover(o.option)) && AssetDatabase.LoadMainAssetAtPath(cover) != null && AssetDatabase.DeleteAsset(cover))
                Debug.Log($"[Variants] {track.Id}: no option needs a cover map any more, removed {cover}.");
        }

        // Options whose rules make the outdoor ground wet or slippery need the cover map: their rules apply only where
        // nothing covers the track.
        static bool NeedsCover(TrackOptionDefinition option) => option.rules != null && (option.rules.wetness > 0f || option.rules.outdoorGrip < 1f);

        static string CoverPath(string folder, Scene trackScene) => $"{folder}/{Path.GetFileNameWithoutExtension(trackScene.path)}.cover.asset";

        static TrackLook ReadLook(Scene scene, string folder, List<OptionSpec> options)
        {
            var look = new TrackLook
            {
                scene = scene,
                folder = folder,
                times = options.Select(o => o.option.Id).Where(id => id is DayOption or NightOption).ToList(),
                sky = SkyMaterials(scene),
            };
            var ambient = Color.black;
            var fogOn = false;
            TrackScenes.WithActive(scene, () =>
            {
                ambient = RenderSettings.ambientLight;
                look.fog = RenderSettings.fogColor;
                fogOn = RenderSettings.fog;
            });
            var runtime = TrackScenes.FindRuntime(scene);
            look.background = runtime != null ? runtime.backgroundColor : look.fog;
            var defaultTime = options.FirstOrDefault(o => o.isDefault && o.option.Id is DayOption or NightOption);
            look.night = defaultTime != null
                ? defaultTime.option.Id == NightOption
                : Mathf.Max(ambient.grayscale, fogOn ? look.fog.grayscale : 0f) < NightBrightness;
            var dayOverlay = look.night && look.times.Contains(DayOption);
            look.dayFog = dayOverlay ? DayFog : look.fog;
            look.dayBackground = dayOverlay ? DayFog : look.background;
            return look;
        }

        // The overlay scene of def: the preset of its option from the track scene, saved and closed. Returns its path.
        // pads: the speed pad materials of a reverse overlay.
        static string BuildOverlayScene(TrackOverlayDefinition def, TrackOptionDefinition option, TrackLook look, List<Material> pads)
        {
            var scene = TrackOverlayAssets.OpenOrCreateScene(def, look.scene);
            try
            {
                var overlay = TrackSceneLayout.EnsureOverlayLayout(scene);
                Reset(overlay);
                switch (option.Id)
                {
                    case ReverseOption:
                        overlay.flipTextures = new List<Material>(pads);
                        break;
                    case NightOption:
                        Night(overlay, look);
                        break;
                    case DayOption:
                        Day(overlay, look);
                        break;
                    case RainOption:
                        Rain(overlay, look);
                        break;
                    case SnowOption:
                        Snow(overlay, look);
                        break;
                    case SandstormOption:
                        Sandstorm(overlay, look);
                        break;
                    default:
                        Debug.LogWarning($"[Variants] {def.Id}: no preset for '{option.Id}', the overlay keeps the track's environment.");
                        break;
                }
                if (NeedsCover(option)) overlay.cover = TrackCoverMapBaker.Bake(look.scene, CoverPath(look.folder, look.scene));
                EditorUtility.SetDirty(overlay);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                return scene.path;
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        static void Reset(TrackOverlay o)
        {
            o.sunColor = default;
            o.sunIntensity = default;
            o.fillColor = default;
            o.fillIntensity = default;
            o.ambient = default;
            o.enableFog = false;
            o.fogColor = default;
            o.fogDistance = default;
            o.background = default;
            o.bakedIndirect = default;
            o.skyTextures = new List<SkyTexture>();
            o.flipTextures = new List<Material>();
            o.cover = null;
        }

        static void Night(TrackOverlay o, TrackLook look)
        {
            var fog = look.fog;
            o.sunIntensity = new FloatOverride(OverrideMode.Multiply, 0.2f);
            o.sunColor = new ColorOverride(OverrideMode.Multiply, NightSunColor);
            o.fillIntensity = new FloatOverride(OverrideMode.Multiply, 0.3f);
            o.ambient = new ColorOverride(OverrideMode.Multiply, NightAmbient);
            o.fogColor = new ColorOverride(OverrideMode.Set, new Color(fog.r * NightFog.r, fog.g * NightFog.g, fog.b * NightFog.b));
            o.background = new ColorOverride(OverrideMode.Multiply, NightFog);
            o.bakedIndirect = new FloatOverride(OverrideMode.Multiply, 0.35f);
            foreach (var s in look.sky)
            {
                var texture = SkyVariant(s, look.folder, "night");
                if (texture != null) o.skyTextures.Add(new SkyTexture { material = s.material, texture = texture });
                else if (s.ground) o.skyTextures.Add(new SkyTexture { material = s.material, tint = NightGroundTint });
                else if (s.flare) o.skyTextures.Add(new SkyTexture { material = s.material, hide = true });
                else LogMissing(s, look.folder, "night");
            }
        }

        static void Day(TrackOverlay o, TrackLook look)
        {
            o.sunColor = new ColorOverride(OverrideMode.Set, DaySunColor);
            o.sunIntensity = new FloatOverride(OverrideMode.Set, 1.1f);
            o.fillColor = new ColorOverride(OverrideMode.Set, Color.white);
            o.fillIntensity = new FloatOverride(OverrideMode.Set, 0.3f);
            o.ambient = new ColorOverride(OverrideMode.Set, DayAmbient);
            o.fogColor = new ColorOverride(OverrideMode.Set, DayFog);
            o.background = new ColorOverride(OverrideMode.Set, DayFog);
            o.bakedIndirect = new FloatOverride(OverrideMode.Multiply, 2.2f);
            foreach (var s in look.sky.Where(m => !m.flare && !m.ground))
            {
                var texture = SkyVariant(s, look.folder, "day");
                if (texture != null) o.skyTextures.Add(new SkyTexture { material = s.material, texture = texture });
                else LogMissing(s, look.folder, "day");
            }
        }

        static void Rain(TrackOverlay o, TrackLook look)
        {
            o.sunIntensity = new FloatOverride(OverrideMode.Multiply, 0.45f);
            o.sunColor = new ColorOverride(OverrideMode.Multiply, RainSunColor);
            o.ambient = new ColorOverride(OverrideMode.Multiply, RainAmbient);
            o.fogColor = new ColorOverride(OverrideMode.Multiply, RainFog);
            o.fogDistance = new FloatOverride(OverrideMode.Multiply, 0.6f);
            EnableFog(o, 60f, 500f);
            o.background = new ColorOverride(OverrideMode.Multiply, RainBackground);
            WeatherSky(o, look, "rain");
            PlaceWeather(o, look.scene, SdkPaths.RainPrefab);
        }

        static void Snow(TrackOverlay o, TrackLook look)
        {
            o.sunIntensity = new FloatOverride(OverrideMode.Multiply, 0.7f);
            o.sunColor = new ColorOverride(OverrideMode.Multiply, SnowSunColor);
            o.ambient = new ColorOverride(OverrideMode.Multiply, SnowAmbient);
            // Whiter fog, at most white on the brightest look the overlay starts from.
            var brightest = new Color(Mathf.Max(look.fog.r, look.dayFog.r), Mathf.Max(look.fog.g, look.dayFog.g), Mathf.Max(look.fog.b, look.dayFog.b));
            o.fogColor = new ColorOverride(OverrideMode.Multiply, new Color(
                Mathf.Min(SnowFog.r, 1f / Mathf.Max(brightest.r, MinChannel)),
                Mathf.Min(SnowFog.g, 1f / Mathf.Max(brightest.g, MinChannel)),
                Mathf.Min(SnowFog.b, 1f / Mathf.Max(brightest.b, MinChannel))));
            o.fogDistance = new FloatOverride(OverrideMode.Multiply, 0.6f);
            EnableFog(o, 40f, 450f);
            o.background = new ColorOverride(OverrideMode.Multiply, SnowBackground);
            WeatherSky(o, look, "snow");
            PlaceWeather(o, look.scene, SdkPaths.SnowPrefab);
        }

        // Thick sand-coloured fog on every track. Weather applies after the time of day, so a colour Set would undo the
        // night: fog and background are multiplied by what turns the day look into the sand colour.
        static void Sandstorm(TrackOverlay o, TrackLook look)
        {
            o.sunIntensity = new FloatOverride(OverrideMode.Multiply, 0.55f);
            o.sunColor = new ColorOverride(OverrideMode.Multiply, SandSunColor);
            o.fillIntensity = new FloatOverride(OverrideMode.Multiply, 0.7f);
            o.ambient = new ColorOverride(OverrideMode.Multiply, SandAmbient);
            o.fogColor = new ColorOverride(OverrideMode.Multiply, Ratio(SandFog, look.dayFog));
            o.fogDistance = new FloatOverride(OverrideMode.Multiply, 0.18f);
            EnableFog(o, 10f, 160f);
            o.background = new ColorOverride(OverrideMode.Multiply, Ratio(SandFog, look.dayBackground));
            WeatherSky(o, look, "sandstorm");
            PlaceWeather(o, look.scene, SdkPaths.SandstormPrefab);
        }

        static Color Ratio(Color target, Color from) => new(
            target.r / Mathf.Max(from.r, MinChannel), target.g / Mathf.Max(from.g, MinChannel), target.b / Mathf.Max(from.b, MinChannel));

        // Turns fog on for track scenes without fog, reaching from start to end (m) once the overlay's fog distance (set
        // before) has applied to it as to any fog.
        static void EnableFog(TrackOverlay o, float start, float end)
        {
            var distance = o.fogDistance.Apply(1f);
            o.enableFog = true;
            o.fogStart = start / distance;
            o.fogEnd = end / distance;
        }

        // The weather's sky textures '<base texture>_<kind>_<day|night>.png' for the sky shell: one per time option the
        // track offers (while that option is selected), else the one of the track scene's own look (always).
        static void WeatherSky(TrackOverlay o, TrackLook look, string kind)
        {
            var times = look.times.Count > 0
                ? look.times.Select(t => (time: TrackOverlayAssets.ShortName(t), requires: t)).ToList()
                : new List<(string time, string requires)> { (look.night ? "night" : "day", "") };
            foreach (var s in look.sky.Where(m => !m.flare && !m.ground))
            foreach (var (time, requires) in times)
            {
                var variant = kind + "_" + time;
                var texture = SkyVariant(s, look.folder, variant);
                if (texture != null) o.skyTextures.Add(new SkyTexture { material = s.material, texture = texture, requiresOption = requires });
                else LogMissing(s, look.folder, variant);
            }
        }

        // The weather effect (the prefab at prefabPath) in the overlay's Environment: the one already there, else the track
        // scene's own (moved: the weather shows only in its variants now), else a new prefab instance. A track scene's
        // instance besides the overlay's is deleted. It covers itself with the loaded track's Visual group.
        static void PlaceWeather(TrackOverlay o, Scene trackScene, string prefabPath)
        {
            var environment = o.transform.Find(TrackSceneLayout.EnvironmentName);
            var existing = Instances(environment.GetComponentsInChildren<WeatherEffect>(true), prefabPath).FirstOrDefault();
            var fromTrack = Instances(trackScene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<WeatherEffect>(true)), prefabPath).ToList();
            GameObject weather;
            if (existing != null)
            {
                weather = existing;
                foreach (var duplicate in fromTrack)
                {
                    Debug.Log($"[Variants] {trackScene.path}: '{duplicate.name}' removed, the {Path.GetFileNameWithoutExtension(prefabPath)} overlay has its own.");
                    UnityEngine.Object.DestroyImmediate(duplicate);
                    EditorSceneManager.MarkSceneDirty(trackScene);
                }
            }
            else if (fromTrack.Count > 0)
            {
                weather = fromTrack[0];
                weather.transform.SetParent(null, true);
                SceneManager.MoveGameObjectToScene(weather, o.gameObject.scene);
                weather.transform.SetParent(environment, true);
                foreach (var duplicate in fromTrack.Skip(1)) UnityEngine.Object.DestroyImmediate(duplicate);
                EditorSceneManager.MarkSceneDirty(trackScene);
            }
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    Debug.LogError($"[Variants] {prefabPath} not found: the overlay of {trackScene.path} has no weather effect.");
                    return;
                }
                weather = (GameObject)PrefabUtility.InstantiatePrefab(prefab, o.gameObject.scene);
                weather.transform.SetParent(environment, false);
            }
            foreach (var effect in weather.GetComponentsInChildren<WeatherEffect>(true)) CoverWithTrack(effect);
        }

        static IEnumerable<GameObject> Instances(IEnumerable<WeatherEffect> effects, string prefabPath) =>
            effects.Where(w => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(w.gameObject) == prefabPath)
                .Select(w => PrefabUtility.GetNearestPrefabInstanceRoot(w.gameObject)).Distinct();

        static void CoverWithTrack(WeatherEffect effect)
        {
            var so = new SerializedObject(effect);
            var trackVisual = so.FindProperty("coverTrackVisual");
            if (trackVisual == null)
            {
                Debug.LogError("[Variants] WeatherEffect has no 'coverTrackVisual' setting: the overlay's weather is not covered.", effect);
                return;
            }
            trackVisual.boolValue = true;
            so.FindProperty("cover").arraySize = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // The MHK/Lit materials of the track's Visual group drawn mostly on speed pads (most of their triangles' centres in
        // the box of a TrackZone Boost): the arrows a reverse overlay turns round. Logged per track.
        static List<Material> SpeedPadMaterials(Scene trackScene, string trackId)
        {
            var runtime = TrackScenes.FindRuntime(trackScene);
            var visual = runtime != null ? runtime.transform.Find(TrackRuntime.VisualGroup) : null;
            var pads = runtime == null
                ? new List<PadBox>()
                : runtime.GetComponentsInChildren<TrackZone>(true).Where(z => z.kind == TrackZoneKind.Boost)
                    .Select(z => z.GetComponent<BoxCollider>()).Where(b => b != null).Select(b => new PadBox(b)).ToList();
            var found = new List<Material>();
            if (visual != null && pads.Count > 0)
            {
                var parts = visual.GetComponentsInChildren<MeshRenderer>()
                    .Where(r => r.enabled && r.TryGetComponent<MeshFilter>(out var f) && f.sharedMesh != null)
                    .Select(r => (renderer: r, mesh: r.GetComponent<MeshFilter>().sharedMesh,
                        near: pads.Where(p => p.bounds.Intersects(r.bounds)).ToArray()))
                    .ToList();
                // Only material assets drawn near a pad can be a pad's (an overlay cannot name a scene's own material).
                var candidates = new HashSet<Material>();
                foreach (var (renderer, mesh, near) in parts)
                    if (near.Length > 0)
                        foreach (var m in renderer.sharedMaterials.Take(mesh.subMeshCount))
                            if (m != null && m.shader != null && m.shader.name == MHKLitIds.ShaderName && EditorUtility.IsPersistent(m))
                                candidates.Add(m);
                var inside = new Dictionary<Material, int>();
                var total = new Dictionary<Material, int>();
                var meshes = new Dictionary<Mesh, (Vector3[] vertices, int[][] indices)>();
                foreach (var (renderer, mesh, near) in parts)
                {
                    var materials = renderer.sharedMaterials;
                    var toWorld = renderer.localToWorldMatrix;
                    for (var s = 0; s < materials.Length && s < mesh.subMeshCount; s++)
                    {
                        var m = materials[s];
                        if (m == null || !candidates.Contains(m)) continue;
                        if (!meshes.TryGetValue(mesh, out var data)) meshes.Add(mesh, data = ReadTriangles(mesh));
                        var indices = data.indices[s];
                        if (indices == null) continue;
                        total[m] = total.GetValueOrDefault(m) + indices.Length / 3;
                        var hits = 0;
                        for (var t = 0; near.Length > 0 && t + 2 < indices.Length; t += 3)
                        {
                            var centre = toWorld.MultiplyPoint3x4((data.vertices[indices[t]] + data.vertices[indices[t + 1]] + data.vertices[indices[t + 2]]) / 3f);
                            foreach (var pad in near)
                            {
                                if (!pad.Contains(centre)) continue;
                                hits++;
                                break;
                            }
                        }
                        inside[m] = inside.GetValueOrDefault(m) + hits;
                    }
                }
                found = candidates.Where(m => total.GetValueOrDefault(m) > 0 && inside.GetValueOrDefault(m) > total[m] * PadShare)
                    .OrderBy(m => m.name, StringComparer.Ordinal).ToList();
            }
            Debug.Log(found.Count > 0
                ? $"[Variants] {trackId}: speed pad materials {string.Join(", ", found.Select(m => m.name))} ({pads.Count} speed pads)."
                : $"[Variants] {trackId}: no speed pad materials ({pads.Count} speed pads), no reverse overlay scene.");
            return found;
        }

        // Vertices and the indices of each triangle submesh (null for other topologies).
        static (Vector3[] vertices, int[][] indices) ReadTriangles(Mesh mesh)
        {
            using var readback = MeshReadback.Of(mesh);
            var indices = new int[readback.SubMeshCount][];
            for (var s = 0; s < indices.Length; s++)
                if (readback.SubMesh(s).topology == MeshTopology.Triangles) indices[s] = readback.Indices(s);
            return (readback.Vertices(), indices);
        }

        // The materials of the track scene's sky domes (FollowCameraPosition, WorldSkyDome). A material is a flare when every
        // part drawing it is one (WorldSkyDome.IsOverlay), ground when every part is flat and below the dome's middle.
        static List<SkyMaterial> SkyMaterials(Scene scene)
        {
            var byMaterial = new Dictionary<Material, SkyMaterial>();
            var domes = scene.GetRootGameObjects()
                .SelectMany(g => g.GetComponentsInChildren<FollowCameraPosition>(true).Select(f => f.gameObject)
                    .Concat(g.GetComponentsInChildren<WorldSkyDome>(true).Select(w => w.gameObject)))
                .Distinct().ToList();
            var seen = new HashSet<Renderer>();
            foreach (var dome in domes)
            {
                var parts = dome.GetComponentsInChildren<MeshRenderer>(true).Where(seen.Add).ToList();
                if (parts.Count == 0) continue;
                var shellWidth = parts.Max(r => Width(r.bounds));
                var bounds = parts[0].bounds;
                foreach (var r in parts) bounds.Encapsulate(r.bounds);
                foreach (var part in parts)
                {
                    var flare = WorldSkyDome.IsOverlay(part.bounds, shellWidth);
                    var ground = part.bounds.size.y <= GroundFlatness * Width(part.bounds) && part.bounds.center.y < bounds.center.y;
                    foreach (var material in part.sharedMaterials)
                    {
                        if (material == null) continue;
                        if (!byMaterial.TryGetValue(material, out var s))
                            byMaterial[material] = s = new SkyMaterial { material = material, texture = BaseTexture(material) };
                        s.flare &= flare;
                        s.ground &= ground;
                    }
                }
            }
            return byMaterial.Values.ToList();
        }

        static float Width(Bounds b) => Mathf.Max(b.size.x, b.size.z);

        static Texture BaseTexture(Material material) =>
            material.HasProperty(MHKLitIds.BaseMap) ? material.GetTexture(MHKLitIds.BaseMap) : material.mainTexture;

        // '<track folder>/Textures/Sky/<base texture name>_<variant>.png', imported like the base texture; null when missing.
        static Texture2D SkyVariant(SkyMaterial s, string folder, string variant)
        {
            if (s.texture == null) return null;
            var path = VariantPath(s, folder, variant);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) return null;
            MatchImport(path, AssetDatabase.GetAssetPath(s.texture));
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static string VariantPath(SkyMaterial s, string folder, string variant) =>
            $"{folder}/{SkyTextureFolder}/{Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(s.texture))}_{variant}.png";

        static void LogMissing(SkyMaterial s, string folder, string variant) =>
            Debug.Log(s.texture == null
                ? $"[Variants] Sky material '{s.material.name}' has no base texture: no {variant} sky for it."
                : $"[Variants] {VariantPath(s, folder, variant)} not found: sky material '{s.material.name}' stays as it is ({variant}).");

        // Gives the generated texture at path the import settings of the base texture at sourcePath.
        static void MatchImport(string path, string sourcePath)
        {
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer || AssetImporter.GetAtPath(sourcePath) is not TextureImporter source) return;
            var wanted = new TextureImporterSettings();
            source.ReadTextureSettings(wanted);
            var current = new TextureImporterSettings();
            importer.ReadTextureSettings(current);
            if (TextureImporterSettings.Equal(wanted, current) && importer.maxTextureSize == source.maxTextureSize &&
                importer.textureCompression == source.textureCompression) return;
            importer.SetTextureSettings(wanted);
            importer.maxTextureSize = source.maxTextureSize;
            importer.textureCompression = source.textureCompression;
            importer.SaveAndReimport();
        }

        // Workspace paths name the mod project first ("mhk-os-mhk2-mod/Packages/..."): the project path starts at "Packages/".
        static string ProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            path = path.Replace('\\', '/').TrimEnd('/');
            var i = path.IndexOf("Packages/", StringComparison.Ordinal);
            return i >= 0 ? path[i..] : path;
        }
    }
}
