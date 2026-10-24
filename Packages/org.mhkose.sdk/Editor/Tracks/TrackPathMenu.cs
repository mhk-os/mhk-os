using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    public static class TrackPathMenu
    {
        /// <summary>Validates the racing lines of every track scene (all TrackDefinitions) and logs the problems.</summary>
        [MenuItem("MHK/Tracks/Validate All Track Paths")]
        public static void ValidateAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var paths = TrackScenes.AllTracks().Where(t => t.scene.IsSet).Select(t => t.scene.Path).ToList();
            var results = TrackScenes.RunSingle(paths, "Validate track paths", ValidateScene, SceneSave.Never);
            Debug.Log($"[Tracks] Validated {results.Count(r => r.hasRuntime)} tracks: {results.Sum(r => r.errors)} errors, " +
                      $"{results.Sum(r => r.warnings)} warnings.");
        }

        static (bool hasRuntime, int errors, int warnings) ValidateScene(Scene scene)
        {
            var path = scene.path;
            var runtime = TrackScenes.FindRuntime(scene);
            if (runtime == null)
            {
                Debug.LogError($"[Tracks] {path}: no TrackRuntime in the scene.");
                return (false, 1, 0);
            }
            int errors = 0, warnings = 0;
            foreach (var issue in TrackPathTools.Validate(runtime))
            {
                if (issue.Severity == TrackIssueSeverity.Error) { errors++; Debug.LogError($"[Tracks] {path}: {issue}"); }
                else if (issue.Severity == TrackIssueSeverity.Warning) { warnings++; Debug.LogWarning($"[Tracks] {path}: {issue}"); }
            }
            return (true, errors, warnings);
        }
    }
}
