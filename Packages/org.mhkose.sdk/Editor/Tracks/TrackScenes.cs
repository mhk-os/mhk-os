using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using MHK.Race;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>When a scene tool saves the scene it worked on.</summary>
    public enum SceneSave
    {
        Never,
        IfDirty,
        /// <summary>Only scenes the tool opened itself (an open scene keeps the user's unsaved state).</summary>
        IfDirtyAndOpenedHere,
        Always,
    }

    /// <summary>Track scene queries and the open, act, save, close loops of the editor tools.</summary>
    public static class TrackScenes
    {
        /// <summary>Every <see cref="TrackDefinition"/> asset in the project.</summary>
        public static IEnumerable<TrackDefinition> AllTracks() => EditorAssets.FindAll<TrackDefinition>();

        /// <summary>Scene paths of every track definition, distinct and sorted.</summary>
        public static List<string> AllPaths() =>
            AllTracks().Where(t => t.scene.IsSet).Select(t => t.scene.Path).Distinct().OrderBy(p => p).ToList();

        /// <summary>The <see cref="TrackRuntime"/> of a loaded scene, null when it has none.</summary>
        public static TrackRuntime FindRuntime(Scene scene) => TrackSceneLoader.Find(scene);

        /// <summary>Loaded scenes with a <see cref="TrackRuntime"/>.</summary>
        public static IEnumerable<Scene> Loaded()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && FindRuntime(scene) != null) yield return scene;
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on every loaded track scene, marks each dirty and logs its non-null result,
        /// then saves the assets. The scenes stay unsaved.
        /// </summary>
        public static List<T> ForEachLoaded<T>(Func<Scene, T> action)
        {
            var results = new List<T>();
            foreach (var scene in Loaded().ToList())
            {
                var result = action(scene);
                if (result == null) continue;
                EditorSceneManager.MarkSceneDirty(scene);
                Debug.Log(result);
                results.Add(result);
            }
            AssetDatabase.SaveAssets();
            return results;
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the scene at <paramref name="path"/>, opening it additively when it is not
        /// loaded and closing it again afterwards. <paramref name="save"/> decides whether the scene is saved.
        /// </summary>
        public static T WithAdditive<T>(string path, Func<Scene, T> action, SceneSave save)
        {
            var scene = SceneManager.GetSceneByPath(path);
            var openedHere = !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var result = action(scene);
                if (ShouldSave(scene, save, openedHere)) EditorSceneManager.SaveScene(scene);
                return result;
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// Opens each scene of <paramref name="paths"/> alone and runs <paramref name="action"/> on it, with a cancelable
        /// progress bar titled <paramref name="title"/>, then restores the previous scene setup (reloaded from disk).
        /// Unsaved changes in open scenes are discarded: menus ask the user to save first. Returns the results of the
        /// scenes processed before a cancel.
        /// </summary>
        public static List<T> RunSingle<T>(IList<string> paths, string title, Func<Scene, T> action, SceneSave save = SceneSave.Never)
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var results = new List<T>();
            try
            {
                using var progress = new EditorProgressScope(title);
                for (var i = 0; i < paths.Count; i++)
                {
                    if (progress.Step(i, paths.Count, paths[i])) break;
                    var scene = EditorSceneManager.OpenScene(paths[i], OpenSceneMode.Single);
                    results.Add(action(scene));
                    if (ShouldSave(scene, save, true)) EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            return results;
        }

        /// <summary>Runs <paramref name="action"/> with <paramref name="scene"/> as the active scene, then restores the previous one.</summary>
        public static void WithActive(Scene scene, Action action)
        {
            var active = SceneManager.GetActiveScene();
            if (active != scene) SceneManager.SetActiveScene(scene);
            try
            {
                action();
            }
            finally
            {
                if (active != scene && active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
            }
        }

        static bool ShouldSave(Scene scene, SceneSave save, bool openedHere) => save switch
        {
            SceneSave.Always => true,
            SceneSave.IfDirty => scene.isDirty,
            SceneSave.IfDirtyAndOpenedHere => openedHere && scene.isDirty,
            _ => false,
        };
    }
}
