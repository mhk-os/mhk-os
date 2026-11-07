using System.Collections.Generic;
using System.Linq;
using MHK.Effects;
using MHK.Powerups;
using MHK.Race;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Scales an imported track scene uniformly about the world origin, so its proportions to the (fixed-size) karts
    /// change: geometry, collision, props, zones, start grid, racing lines with their widths, lights, sound ranges, fog
    /// (optional: a game-wide fog in metres stays as it is), far clip and kill height. Item boxes keep their size and their
    /// height above the ground, racing lines stay unscaled (their nodes move instead, <see cref="TrackPathTools"/> assumes
    /// unscaled paths), and a camera-centred sky dome (<see cref="FollowCameraPosition"/>) is left alone. Particle systems
    /// in local scaling mode keep their size.
    /// Lighting must be baked again afterwards (probes and lightmap texel density follow the new size).
    /// </summary>
    public static class TrackScaler
    {
        const string UndoLabel = "Scale track";
        // The ground of an item box is searched from this far (m) above its root (box roots may sit below the road surface,
        // ItemBoxAppearance.trackHeight) down to this far below it. Boxes without ground are scaled like any other point.
        const float BoxGroundProbeUp = 1f;
        const float BoxGroundProbeDown = 20f;

        // Serialized distances (m) of track components, scaled with the track. CollapsingBridge.fallAcceleration scales too,
        // so its pieces fall the scaled depth in the same time.
        static readonly (System.Type type, string[] properties)[] Distances =
        {
            (typeof(TrackRuntime), new[] { "killHeight", "fallDepth", "farClip" }),
            (typeof(TrackZone), new[] { "pushFalloffMinDistance" }),
            (typeof(CollapsingBridge), new[] { "sideMargin", "heightRange", "fallDepth", "fallAcceleration",
                                                "soundRange.fullVolumeDistance", "soundRange.fadeDistance" }),
            (typeof(TrackEventSound), new[] { "range.fullVolumeDistance", "range.fadeDistance" }),
            (typeof(EffectLoopSound), new[] { "minDistance", "maxDistance" }),
        };

        /// <summary>
        /// Scales the track in <paramref name="scene"/> by <paramref name="factor"/> (undoable, the scene is marked dirty
        /// and left unsaved) and returns a summary for the log. <paramref name="scaleFog"/> off keeps the fog distances and
        /// density (a fog shared by all tracks of a game).
        /// </summary>
        public static string Scale(Scene scene, float factor, bool scaleFog)
        {
            var runtime = TrackScenes.FindRuntime(scene);
            if (runtime == null) throw new System.ArgumentException($"{scene.path}: no TrackRuntime in the scene.");
            if (factor <= 0f || float.IsNaN(factor) || float.IsInfinity(factor))
                throw new System.ArgumentOutOfRangeException(nameof(factor), factor, "The scale must be finite and positive.");
            Undo.SetCurrentGroupName(UndoLabel);
            var group = Undo.GetCurrentGroup();

            var roots = TrackSceneLayout.ContentObjects(scene).Where(g => !g.TryGetComponent<FollowCameraPosition>(out _)).ToList();
            var boxes = MeasureBoxes(roots, runtime.ObstacleMask);
            foreach (var root in roots)
                TrackPathUndo.Record(root.transform, UndoLabel, t =>
                {
                    t.localPosition *= factor;
                    t.localScale *= factor;
                });
            foreach (var (box, position, clearance) in boxes)
                TrackPathUndo.Record(box, UndoLabel, t =>
                {
                    t.position = position * factor + Vector3.up * ((clearance ?? 0f) * (1f - factor));
                    t.localScale /= factor;
                });

            var components = roots.SelectMany(r => r.GetComponentsInChildren<Component>(true))
                .Where(c => c != null && c.GetComponentInParent<ItemBox>(true) == null).ToList();
            var paths = components.OfType<TrackPath>().ToList();
            foreach (var path in paths) ScalePath(path, factor);
            foreach (var c in components) ScaleDistances(c, factor);
            foreach (var zone in components.OfType<TrackZone>().Where(z => z.pushFalloff))
                TrackPathUndo.Record(zone, UndoLabel, z => z.value *= factor); // value / distance is the push speed
            foreach (var light in components.OfType<Light>())
                TrackPathUndo.Record(light, UndoLabel, l =>
                {
                    l.range *= factor;
                    l.areaSize *= factor;
                });
            foreach (var source in components.OfType<AudioSource>())
                TrackPathUndo.Record(source, UndoLabel, a =>
                {
                    a.minDistance *= factor;
                    a.maxDistance *= factor;
                });
            if (scaleFog) TrackScenes.WithActive(scene, () => ScaleFog(factor));

            foreach (var path in paths) TrackPathUndo.Invalidate(path);
            TrackRuntime.MarkLinesEdited();
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);
            return $"[Tracks] {scene.path}: scaled ×{factor:0.###} ({roots.Count} track and environment objects, {paths.Count} racing lines, " +
                   $"{boxes.Count} item boxes kept at their size, {boxes.Count(b => b.clearance == null)} of them without ground " +
                   $"below, fog {(scaleFog ? "scaled" : "kept")}). Bake its lighting again.";
        }

        // Item boxes below the scaled roots with their world position and height above the ground (null = none found).
        static List<(Transform box, Vector3 position, float? clearance)> MeasureBoxes(List<GameObject> roots, int groundMask)
        {
            Physics.SyncTransforms();
            var boxes = new List<(Transform, Vector3, float?)>();
            foreach (var box in roots.SelectMany(r => r.GetComponentsInChildren<ItemBox>(true)))
            {
                var p = box.transform.position;
                float? clearance = ItemPhysics.TryGroundBelow(p, BoxGroundProbeUp, BoxGroundProbeDown, out var hit, groundMask)
                    ? p.y - hit.point.y
                    : null;
                boxes.Add((box.transform, p, clearance));
            }
            return boxes;
        }

        // A racing line keeps unit scale: the outermost path below the scaled roots cancels the scale and every transform
        // below it (the routes under the main line) moves out instead; the nodes of every path move out and widen.
        static void ScalePath(TrackPath path, float factor)
        {
            var parent = path.transform.parent;
            if (parent == null || parent.GetComponentInParent<TrackPath>(true) == null)
            {
                TrackPathUndo.Record(path.transform, UndoLabel, t => t.localScale /= factor);
                foreach (var below in path.GetComponentsInChildren<Transform>(true).Where(t => t != path.transform))
                    TrackPathUndo.Record(below, UndoLabel, t => t.localPosition *= factor);
            }
            var nodes = path.Nodes.Select(n => new TrackPath.Node { position = n.position * factor, width = n.width * factor, flags = n.flags }).ToList();
            TrackPathUndo.Record(path, UndoLabel, p => p.SetNodes(nodes));
        }

        static void ScaleDistances(Component c, float factor)
        {
            foreach (var (type, properties) in Distances)
            {
                if (!type.IsInstanceOfType(c)) continue;
                var so = new SerializedObject(c);
                foreach (var name in properties) so.FindProperty(name).floatValue *= factor;
                so.ApplyModifiedProperties();
            }
        }

        // Fog of the active scene: linear distances scale, exponential densities shrink.
        static void ScaleFog(float factor)
        {
            Undo.RecordObject(Unsupported.GetRenderSettings(), UndoLabel);
            RenderSettings.fogStartDistance *= factor;
            RenderSettings.fogEndDistance *= factor;
            RenderSettings.fogDensity /= factor;
        }
    }
}
