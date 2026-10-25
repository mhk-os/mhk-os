using System;
using System.IO;
using System.Linq;
using MHK.Core;
using MHK.Editor.Optimization;
using MHK.Editor.Tracks;
using MHK.Powerups;
using MHK.Race;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Builds a track scene from an export track folder (work/EXPORT_CONTRACT.md: track.json plus .glb models) with the
    /// standard groups of the built-in tracks, then writes its <see cref="MHK.Modding.TrackDefinition"/>.
    /// </summary>
    public static class TrackImporter
    {
        const int NonGroundLayers = Physics.IgnoreRaycastLayer | GameLayers.UIMask | GameLayers.KartAndItemMask | 1 << GameLayers.Hidden;

        /// <summary>Imports the track in <paramref name="dir"/> into the settings' pack, replacing an earlier import.</summary>
        public static void Import(string dir, ExportImportSettings s)
        {
            var c = new TrackImportContext(dir, s);
            var open = SceneManager.GetSceneByPath(c.ScenePath);
            if (open.IsValid() && open.isLoaded)
                throw new InvalidOperationException($"{c.ScenePath} is open: close it before re-importing.");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                TrackScenes.WithActive(scene, () => BuildAndSave(c, scene));
            }
            finally
            {
                c.DestroyModels();
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        static void BuildAndSave(TrackImportContext c, Scene scene)
        {
            var root = new GameObject(c.BaseName).transform;
            var runtime = root.gameObject.AddComponent<TrackRuntime>();
            var visual = BuildVisual(c, root);
            TrackCollisionBuilder.Build(c, root);
            TrackPropBuilder.Build(c, root);
            TrackAudioBuilder.BuildSounds(c, root);
            var zones = ImportHierarchy.CreateChild(root, "Zones");
            TrackZoneBuilder.BuildZones(c, zones);
            var path = TrackPathBuilder.Build(c, root, runtime);
            TrackBridgeBuilder.Build(c, root, visual, runtime);
            runtime.groundMask = ~NonGroundLayers;
            TrackZoneBuilder.BuildBoostPads(c, zones, path);
            var slots = BuildStartGrid(c, root, runtime);
            var items = BuildItemBoxes(c, root);
            ApplyKillHeight(c, runtime, visual);
            TrackEnvironmentBuilder.Apply(c, runtime);
            foreach (var issue in TrackPathTools.Validate(runtime).Where(i => i.Severity != TrackIssueSeverity.Info))
                Debug.LogWarning($"[Import] Track {c.Id}: {issue}");

            c.DestroyModels();
            c.Library.Save();
            if (!EditorSceneManager.SaveScene(scene, c.ScenePath)) throw new IOException("cannot save " + c.ScenePath);
            if (c.Settings.chunkLargeMeshes && MeshChunker.ChunkScene(scene).chunked.Count > 0) EditorSceneManager.SaveScene(scene);
            TrackDefinitionWriter.Write(c);
            Debug.Log($"[Import] Track {c.Id}: {path.Count} path nodes ({path.Length:0} m), {runtime.alternatePaths.Count} routes, " +
                      $"{slots} grid slots, {items} item boxes, kill height {runtime.killHeight:0.0} → {c.ScenePath}");
        }

        static Transform BuildVisual(TrackImportContext c, Transform root)
        {
            var visual = ImportHierarchy.CreateChild(root, TrackRuntime.VisualGroup);
            foreach (var f in ExportJson.Items(c.Json["visual"]).Select(t => (string)t))
            {
                var m = c.Model(f, Path.GetFileName(f).StartsWith("billboard") ? GlbLightingStyle.Object : GlbLightingStyle.World);
                if (m == null) continue;
                var inst = c.Instance(m, visual, Path.GetFileNameWithoutExtension(f));
                ImportHierarchy.StripAnimation(inst);
                ImportHierarchy.SetLayer(inst, GameLayers.Track);
                foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
            }
            return visual;
        }

        static int BuildStartGrid(TrackImportContext c, Transform root, TrackRuntime runtime)
        {
            var grid = ImportHierarchy.CreateChild(root, "Grid");
            var slots = ExportJson.Items(c.Json["startGrid"]);
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = ImportHierarchy.CreateChild(grid, "Slot " + i);
                slot.SetLocalPositionAndRotation(ExportJson.Pos(slots[i]["pos"], c.Scale), ExportJson.Rot(slots[i]["rot"]));
                runtime.startGrid.Add(slot);
            }
            return slots.Count;
        }

        static int BuildItemBoxes(TrackImportContext c, Transform root)
        {
            var s = c.Settings;
            var items = ImportHierarchy.CreateChild(root, "Items");
            var count = 0;
            foreach (var it in ExportJson.Items(c.Json["itemSpawns"]))
            {
                var type = ExportJson.Str(it["type"], "random");
                var fixedType = type.StartsWith("fixed:") ? type[6..] : null;
                var mapping = fixedType != null ? s.fixedItems.FirstOrDefault(f => f != null && f.type == fixedType) : null;
                var prefab = mapping != null && mapping.prefab != null ? mapping.prefab : s.itemBoxPrefab;
                if (prefab == null) continue;
                var box = (GameObject)PrefabUtility.InstantiatePrefab(prefab, items);
                box.transform.SetLocalPositionAndRotation(ExportJson.Pos(it["pos"], c.Scale), ExportJson.Rot(it["rot"]));
                if (fixedType != null && box.TryGetComponent(out ItemBox itemBox))
                    itemBox.fixedPowerupId = mapping?.contentId ?? fixedType;
                count++;
            }
            return count;
        }

        static void ApplyKillHeight(TrackImportContext c, TrackRuntime runtime, Transform visual)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>();
            var minY = renderers.Length > 0 ? renderers.Min(r => r.bounds.min.y) : 0f;
            runtime.killHeight = c.Json["respawnHeight"] != null ? ExportJson.F(c.Json["respawnHeight"]) * c.Scale : minY - c.Settings.killDepth;
            runtime.fallDepth = c.Settings.fallDepth * c.Scale;
        }
    }
}
