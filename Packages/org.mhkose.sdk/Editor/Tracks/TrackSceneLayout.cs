using System.Collections.Generic;
using System.Linq;
using MHK.Race;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// The object layout of track and overlay scenes. A track scene has one root 'Shared' (identity) holding 'Track' (the
    /// <see cref="TrackRuntime"/> with geometry, collision, props, zones and racing lines) and 'Environment' (identity: the
    /// lighting with sun and fill lights, the sky dome, ambient effects). An overlay scene has one root 'Overlay' with the
    /// <see cref="TrackOverlay"/> (identity) and the children 'Environment' and 'Track'. <see cref="Migrate"/> moves older
    /// track scenes (track, Lighting and SkyDome side by side at the root) into the layout.
    /// </summary>
    public static class TrackSceneLayout
    {
        /// <summary>Name of a track scene's root object.</summary>
        public const string SharedName = "Shared";
        /// <summary>Name of the track (the <see cref="TrackRuntime"/>) under 'Shared', and of an overlay's geometry group.</summary>
        public const string TrackName = "Track";
        /// <summary>Name of the environment group under 'Shared' or 'Overlay'.</summary>
        public const string EnvironmentName = "Environment";
        /// <summary>Name of the environment's lighting object (sun, fill lights, lamps).</summary>
        public const string LightingName = "Lighting";

        const string MenuRoot = "MHK/Tracks/Scene Layout/";
        const string UndoLabel = "Track scene layout";

        [MenuItem(MenuRoot + "Migrate Open Track Scenes")]
        static void MigrateOpen() =>
            TrackScenes.ForEachLoaded(scene => Migrate(scene) ? $"[Tracks] {scene.path}: moved into the Shared/Track/Environment layout." : null);

        [MenuItem(MenuRoot + "Migrate All Track Scenes")]
        static void MigrateAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var changed = TrackScenes.RunSingle(TrackScenes.AllPaths(), "Migrate track scene layout", scene => Migrate(scene), SceneSave.IfDirty).Count(c => c);
            Debug.Log($"[Tracks] Track scene layout: {changed} scenes migrated, the others already had it.");
        }

        /// <summary>
        /// Moves the track scene <paramref name="scene"/> into the layout (undoable, the scene is marked dirty and left
        /// unsaved): the <see cref="TrackRuntime"/> becomes 'Shared/Track' and every other root object goes into
        /// 'Shared/Environment', all keeping their world placement. A scene already in the layout stays as it is. Returns
        /// whether anything changed; a scene without a TrackRuntime is left alone with a warning.
        /// </summary>
        public static bool Migrate(Scene scene)
        {
            var runtime = TrackScenes.FindRuntime(scene);
            if (runtime == null)
            {
                Debug.LogWarning($"[Tracks] {scene.path}: no TrackRuntime, the scene layout is left as it is.");
                return false;
            }
            var changed = false;
            var shared = FindShared(scene);
            if (shared == null)
            {
                shared = CreateRoot(scene, SharedName);
                changed = true;
            }
            var environment = shared.Find(EnvironmentName);
            if (environment == null)
            {
                environment = CreateChild(shared, EnvironmentName);
                changed = true;
            }
            var track = runtime.transform;
            if (track.parent != shared)
            {
                Undo.SetTransformParent(track, shared, UndoLabel);
                changed = true;
            }
            if (track.name != TrackName)
            {
                Undo.RecordObject(track.gameObject, UndoLabel);
                track.name = TrackName;
                changed = true;
            }
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.transform == shared) continue;
                Undo.SetTransformParent(root.transform, environment, UndoLabel);
                changed = true;
            }
            changed |= SetSiblingIndex(track, 0);
            changed |= SetSiblingIndex(environment, 1);
            if (changed) EditorSceneManager.MarkSceneDirty(scene);
            return changed;
        }

        /// <summary>The root 'Shared' of a track scene, null when the scene is not in the layout.</summary>
        public static Transform FindShared(Scene scene) =>
            scene.GetRootGameObjects().Where(g => g.name == SharedName && !g.TryGetComponent<TrackRuntime>(out _))
                .Select(g => g.transform).FirstOrDefault();

        /// <summary>'Shared/Environment' of a track scene, null when the scene is not in the layout.</summary>
        public static Transform FindEnvironment(Scene scene)
        {
            var shared = FindShared(scene);
            return shared != null ? shared.Find(EnvironmentName) : null;
        }

        /// <summary>The environment's lighting object 'Shared/Environment/Lighting', null when the scene has none.</summary>
        public static Transform FindLighting(Scene scene)
        {
            var environment = FindEnvironment(scene);
            return environment != null ? environment.Find(LightingName) : null;
        }

        /// <summary>
        /// The objects a track scene is made of: 'Track' and every object in 'Environment' of a scene in the layout (plus
        /// any other child of 'Shared'), else the scene's root objects.
        /// </summary>
        public static List<GameObject> ContentObjects(Scene scene)
        {
            var result = new List<GameObject>();
            var shared = FindShared(scene);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.transform != shared)
                {
                    result.Add(root);
                    continue;
                }
                foreach (Transform child in shared)
                {
                    if (child.name != EnvironmentName)
                    {
                        result.Add(child.gameObject);
                        continue;
                    }
                    foreach (Transform item in child) result.Add(item.gameObject);
                }
            }
            return result;
        }

        /// <summary>The <see cref="TrackOverlay"/> on a root object of <paramref name="scene"/>, null when there is none.</summary>
        public static TrackOverlay FindOverlay(Scene scene) =>
            scene.GetRootGameObjects().Select(g => g.GetComponent<TrackOverlay>()).FirstOrDefault(o => o != null);

        /// <summary>
        /// The overlay scene's root (<see cref="TrackOverlay.RootName"/> with its <see cref="TrackOverlay"/>) with the children
        /// 'Environment' and 'Track', each created where missing (undoable).
        /// </summary>
        public static TrackOverlay EnsureOverlayLayout(Scene scene)
        {
            var overlay = FindOverlay(scene);
            if (overlay == null)
            {
                var root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == TrackOverlay.RootName)?.transform
                           ?? CreateRoot(scene, TrackOverlay.RootName);
                overlay = Undo.AddComponent<TrackOverlay>(root.gameObject);
            }
            if (overlay.transform.Find(EnvironmentName) == null) CreateChild(overlay.transform, EnvironmentName);
            if (overlay.transform.Find(TrackName) == null) CreateChild(overlay.transform, TrackName);
            SetSiblingIndex(overlay.transform.Find(EnvironmentName), 0);
            SetSiblingIndex(overlay.transform.Find(TrackName), 1);
            return overlay;
        }

        static Transform CreateRoot(Scene scene, string name)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            Undo.RegisterCreatedObjectUndo(go, UndoLabel);
            return go.transform;
        }

        static Transform CreateChild(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(t.gameObject, UndoLabel);
            return t;
        }

        static bool SetSiblingIndex(Transform t, int index)
        {
            if (t.GetSiblingIndex() == index) return false;
            Undo.SetSiblingIndex(t, index, UndoLabel);
            return true;
        }
    }
}
