using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Race
{
    /// <summary>
    /// Loads a track scene next to the Race scene and makes it the active scene, so its lighting settings (ambient,
    /// fog, skybox) apply. Players load from the build's scene list or a loaded mod scene bundle, the editor also loads
    /// any project scene, so tracks work in play mode without a build list entry.
    /// </summary>
    public static class TrackSceneLoader
    {
        /// <summary>Unity reports load progress 0..0.9 until the scene is activated.</summary>
        const float ActivationProgress = 0.9f;

        /// <summary>
        /// Loads <paramref name="path"/> additively. <paramref name="done"/> gets the loaded scene (invalid on failure),
        /// <paramref name="progress"/> (optional) the load progress 0..1 every frame until then.
        /// </summary>
        public static IEnumerator Load(string path, System.Action<Scene> done, System.Action<float> progress = null)
        {
            AsyncOperation op;
#if UNITY_EDITOR
            op = MHK.Modding.ModLoader.IsBundleScene(path)
                ? SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive)
                : UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
#else
            op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
#endif
            if (op == null)
            {
                Debug.LogError($"[Race] Track scene '{path}' cannot be loaded (not in the build or a loaded mod).");
                done(default);
                yield break;
            }
            while (!op.isDone)
            {
                progress?.Invoke(Mathf.Clamp01(op.progress / ActivationProgress));
                yield return null;
            }
            progress?.Invoke(1f);
            var scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid()) SceneManager.SetActiveScene(scene);
            done(scene);
        }

        /// <summary>The <see cref="TrackRuntime"/> of a loaded track scene, null when it has none.</summary>
        public static TrackRuntime Find(Scene scene)
        {
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var runtime = root.GetComponentInChildren<TrackRuntime>(true);
                if (runtime != null) return runtime;
            }
            return null;
        }
    }
}
