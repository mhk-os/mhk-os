using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MHK.Editor.Tracks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace MHK.Editor.Lighting
{
    /// <summary>
    /// Baked sun shadowmasks for track scenes. Prepare sets up lights, lightmap UVs, static flags and racing-line light
    /// probes, Bake opens each track alone, prepares, bakes and post-processes it.
    /// </summary>
    public static class TrackLightBaker
    {
        const string MenuRoot = "MHK/Lighting/";
        const string LightmapFilter = "Lightmap- t:Texture2D";

        [MenuItem(MenuRoot + "Prepare Open Track Scenes")]
        static void PrepareOpenMenu() => TrackScenes.ForEachLoaded(s => PrepareScene(s));

        [MenuItem(MenuRoot + "Bake Open Track Scenes")]
        static void BakeOpenMenu()
        {
            var paths = TrackScenes.Loaded().Select(s => s.path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count == 0 || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BakeTracks(paths);
        }

        [MenuItem(MenuRoot + "Prepare All Tracks")]
        static void PrepareAllMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            PrepareAllTracks();
        }

        [MenuItem(MenuRoot + "Bake All Tracks")]
        static void BakeAllMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BakeAllTracks();
        }

        [MenuItem(MenuRoot + "Apply Shadow Strength to All Tracks")]
        static void StrengthAllMenu() => ApplyShadowStrengthToAllTracks();

        [MenuItem(MenuRoot + "Shrink Unused Lightmaps of All Tracks")]
        static void ShrinkAllMenu() => ShrinkUnusedLightmapsOfAllTracks();

        /// <summary>Prepares and bakes every track. Unsaved changes in open scenes are discarded (the menu item asks first).</summary>
        public static List<TrackBakeReport> BakeAllTracks() => BakeTracks(TrackScenes.AllPaths());

        /// <summary>Prepares every track without baking (each opened additively, saved and closed).</summary>
        public static List<TrackBakeReport> PrepareAllTracks()
        {
            var reports = new List<TrackBakeReport>();
            var paths = TrackScenes.AllPaths();
            using (var progress = new EditorProgressScope("Prepare track lighting"))
                for (var i = 0; i < paths.Count; i++)
                {
                    if (progress.Step(i, paths.Count, paths[i])) break;
                    reports.Add(TrackScenes.WithAdditive(paths[i], s => PrepareScene(s), SceneSave.Always));
                }
            AssetDatabase.SaveAssets();
            foreach (var r in reports) Debug.Log(r);
            return reports;
        }

        /// <summary>
        /// Sets the shadow strength (and baked shadow angle) of every shadowed light in every track scene, nothing else
        /// (the project-wide strength, or the scene's BakedIndirectLighting value). Returns the number of scenes changed.
        /// </summary>
        public static int ApplyShadowStrengthToAllTracks()
        {
            var settings = TrackBakeSettings.Load();
            var changed = TrackScenes.AllPaths()
                .Count(path => TrackScenes.WithAdditive(path, scene => ApplyShadowStrength(scene, settings), SceneSave.IfDirtyAndOpenedHere));
            Debug.Log($"[Lighting] Shadow strength {settings.ShadowStrength} (or the scene's BakedIndirectLighting value) applied: {changed} track scenes changed.");
            return changed;
        }

        /// <summary>
        /// Opens each scene alone, prepares, bakes, post-processes and saves it, then restores the previous scene setup
        /// (reloaded from disk). Logs one line per track (bake time, lightmap memory) and a total.
        /// <paramref name="regenerateLightmapUVs"/> re-unwraps meshes that already have lightmap UVs.
        /// </summary>
        public static List<TrackBakeReport> BakeTracks(IList<string> scenePaths, bool regenerateLightmapUVs = false)
        {
            var total = Stopwatch.StartNew();
            var reports = TrackScenes.RunSingle(scenePaths, "Bake track lighting", scene =>
            {
                var report = BakeScene(scene, regenerateLightmapUVs);
                Debug.Log(report);
                return report;
            });
            var baked = reports.Where(r => r.baked).ToList();
            Debug.Log($"[Lighting] Baked {baked.Count}/{reports.Count} tracks in {total.Elapsed.TotalMinutes:0.0} min " +
                      $"(bake {baked.Sum(r => r.bakeSeconds) / 60.0:0.0} min); lightmaps {baked.Sum(r => r.lightmapCount)}, " +
                      $"shadowmask {baked.Sum(r => r.shadowmaskBytes) / TrackBakeReport.BytesPerMB:0.0} MB (Android ≈ {baked.Sum(r => r.shadowmaskAndroidBytes) / TrackBakeReport.BytesPerMB:0.0} MB), " +
                      $"colour {baked.Sum(r => r.colorBytes) / TrackBakeReport.BytesPerMB:0.0} MB.");
            return reports;
        }

        /// <summary>
        /// Opens <paramref name="scenePath"/> alone (lighting bakes every loaded scene together), prepares and bakes it.
        /// Prepare rewrites meshes in the track's model library, so the collision meshes are made double-sided again.
        /// </summary>
        public static TrackBakeReport BakeTrack(string scenePath, bool regenerateLightmapUVs = false) =>
            BakeScene(EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single), regenerateLightmapUVs);

        /// <summary>
        /// Rewrites the colour and directional lightmap files of every baked track tiny without re-baking, except in
        /// scenes with BakedIndirectLighting (their colour lightmaps are used). Returns the bytes on disk before and after.
        /// </summary>
        public static (long before, long after) ShrinkUnusedLightmapsOfAllTracks()
        {
            var settings = TrackBakeSettings.Load();
            long before = 0, after = 0;
            foreach (var scenePath in TrackScenes.AllPaths())
            {
                var folder = Path.ChangeExtension(scenePath, null);
                if (!AssetDatabase.IsValidFolder(folder) || TrackBakePreparer.UsesBakedIndirect(scenePath)) continue;
                foreach (var path in EditorAssets.FindPaths(LightmapFilter, folder))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!name.EndsWith("_light") && !name.EndsWith("_dir")) continue;
                    before += new FileInfo(path).Length;
                    LightmapPostProcess.ShrinkUnusedLightmap(path, settings.ColorLightmapMaxSize);
                    after += new FileInfo(path).Length;
                }
            }
            Debug.Log($"[Lighting] Unused lightmaps rewritten at ≤{settings.ColorLightmapMaxSize} px: {before / TrackBakeReport.BytesPerMB:0.0} MB → {after / TrackBakeReport.BytesPerMB:0.00} MB on disk.");
            return (before, after);
        }

        /// <summary>
        /// Applies everything a bake needs to <paramref name="scene"/>, starting with the scene's
        /// <see cref="TrackLightingProfile"/> entry. Idempotent. Lightmap UVs are generated only where missing unless
        /// <paramref name="regenerateLightmapUVs"/>.
        /// </summary>
        public static TrackBakeReport PrepareScene(Scene scene, bool regenerateLightmapUVs = false) =>
            TrackBakePreparer.Prepare(scene, regenerateLightmapUVs);

        static bool ApplyShadowStrength(Scene scene, TrackBakeSettings settings)
        {
            var n = 0;
            var strength = TrackBakePreparer.ShadowStrength(scene, settings);
            foreach (var light in TrackBakePreparer.SceneLights(scene).Where(l => l.shadows != LightShadows.None))
            {
                if (Mathf.Approximately(light.shadowStrength, strength)) continue;
                Undo.RecordObject(light, "Shadow strength");
                light.shadowStrength = strength;
                n++;
            }
            if (n > 0) EditorSceneManager.MarkSceneDirty(scene);
            return n > 0;
        }

        static TrackBakeReport BakeScene(Scene scene, bool regenerateLightmapUVs)
        {
            var report = PrepareScene(scene, regenerateLightmapUVs);
            var faces = CollisionWinding.FixScene(scene);
            if (faces > 0) report.warnings.Add($"collision made double-sided again ({faces} faces added)");
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            var watch = Stopwatch.StartNew();
            // Bake() also returns true when the lightmapper refused to start (e.g. "atlassing failed", logged only).
            if (!Lightmapping.Bake() || (report.staticRenderers > 0 && LightmapSettings.lightmaps.Length == 0))
            {
                report.warnings.Add("bake failed, was cancelled or produced no lightmaps (see the console)");
                return report;
            }
            report.bakeSeconds = watch.Elapsed.TotalSeconds;
            report.baked = true;
            LightmapPostProcess.Run(report, scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            return report;
        }
    }
}
