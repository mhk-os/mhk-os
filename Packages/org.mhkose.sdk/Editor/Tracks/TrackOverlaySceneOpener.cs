using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Opening an overlay scene on its own (a double click in the Project window) starts editing it in the Track Variants
    /// window, with its track scene loaded behind it as the locked reference. On its own an overlay scene shows nothing of
    /// the track, so nothing could be placed where it belongs. Scenes the window opens itself, batch runs and play mode
    /// are left alone, and so is a scene that is no longer the only one open by the next editor update (a tool going
    /// through scenes one by one).
    /// </summary>
    [InitializeOnLoad]
    internal static class TrackOverlaySceneOpener
    {
        static TrackOverlaySceneOpener() => EditorSceneManager.sceneOpened += OnSceneOpened;

        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (mode != OpenSceneMode.Single || TrackVariantsWindow.Switching || Application.isBatchMode ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            var path = scene.path;
            if (string.IsNullOrEmpty(path)) return;
            var overlay = EditorAssets.FindAll<TrackOverlayDefinition>().FirstOrDefault(d => d.scene.IsSet && d.scene.Path == path);
            if (overlay == null) return;
            EditorApplication.delayCall += () =>
            {
                if (SceneManager.sceneCount != 1 || SceneManager.GetSceneAt(0).path != path) return;
                TrackVariantsWindow.EditOverlay(overlay);
            };
        }
    }
}
