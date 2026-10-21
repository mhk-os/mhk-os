using System;
using System.Linq;
using MHK.Editor.Modding;
using MHK.Modding;
using UnityEditor.Build;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Fails a player build early when a track scene is among the scenes being built (tracks ship in their mod's scene
    /// bundle, a copy in the player would only duplicate them) or a track of the shipped base content has no scene.
    /// </summary>
    sealed class BuiltinTrackBuildCheck : BuildPlayerProcessor
    {
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            TrackSceneSync.Sync();
            var trackScenes = TrackSceneSync.TrackScenePaths();
            var inPlayer = (context.BuildPlayerOptions.scenes ?? Array.Empty<string>()).Where(trackScenes.Contains).ToList();
            if (inPlayer.Count > 0)
                throw new BuildFailedException("Track scenes in the player's scene list: " + string.Join(", ", inPlayer) +
                                               ". Tracks ship in their mod's scene bundle: remove them from the scene list (MHK ▸ Tracks ▸ " +
                                               "Sync Track Scenes fixes the global list, build profiles with their own list need it by hand).");
            var missing = ContentPackAssets.ShippedPacks().SelectMany(p => p.GetAllIncluded<TrackDefinition>())
                .Where(t => !t.scene.IsSet).Select(t => t.Id).ToList();
            if (missing.Count > 0)
                throw new BuildFailedException("Base content tracks without a scene: " + string.Join(", ", missing) + ". Assign their scenes and build again.");
        }
    }
}
