using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using UnityEditor;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Keeps track scene references valid: refreshes every <see cref="TrackDefinition"/>'s scene path after scenes are
    /// moved or renamed, and keeps track scenes out of the build's scene list (they ship in their mod's scene bundle).
    /// </summary>
    public class TrackSceneSync : AssetPostprocessor
    {
        /// <summary>Refreshes the track scene paths and the build scene list. Runs after asset changes and before player builds.</summary>
        [MenuItem("MHK/Tracks/Sync Track Scenes")]
        public static void Sync()
        {
            var trackScenes = TrackScenePaths();
            var scenes = EditorBuildSettings.scenes
                .Where(s => !trackScenes.Contains(s.path) && AssetDatabase.LoadAssetAtPath<SceneAsset>(s.path) != null)
                .ToArray();
            if (!scenes.Select(s => s.path).SequenceEqual(EditorBuildSettings.scenes.Select(s => s.path)))
                EditorBuildSettings.scenes = scenes;
        }

        /// <summary>Scene paths of every track in the project, after refreshing them.</summary>
        public static HashSet<string> TrackScenePaths()
        {
            var paths = new HashSet<string>();
            foreach (var track in TrackScenes.AllTracks())
            {
                if (track.scene.Sync()) EditorUtility.SetDirty(track);
                if (track.scene.IsSet) paths.Add(track.scene.Path);
            }
            return paths;
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!imported.Concat(deleted).Concat(moved).Any(Relevant)) return;
            EditorApplication.delayCall -= Sync;
            EditorApplication.delayCall += Sync;
        }

        static bool Relevant(string path)
        {
            if (path.EndsWith(".unity", StringComparison.Ordinal)) return true;
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type == typeof(TrackDefinition) || (type != null && typeof(ContentPack).IsAssignableFrom(type));
        }
    }
}
