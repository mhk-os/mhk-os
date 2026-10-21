using System.Collections.Generic;
using System.IO;
using MHK.Core;
using MHK.Editor.Modding;
using MHK.Modding;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MHK.Editor.Build
{
    /// <summary>
    /// Player builds ship the base content as mods: builds the shipped packs (<see cref="ContentPackAssets.ShippedPacks"/>)
    /// into <c>.mhkmod</c> bundles for the build target and adds them to the build's StreamingAssets/Mods, where
    /// <see cref="ModLoader"/> finds them next to the user's mods.
    /// </summary>
    sealed class ShippedModsBuild : BuildPlayerProcessor
    {
        // Unity refuses AssetBundle output inside Library, so the incremental cache lives in the ignored Builds folder.
        const string CacheRoot = "Builds/Cache/ShippedMods";

        public override int callbackOrder => 10;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var options = context.BuildPlayerOptions;
            if ((options.options & BuildOptions.BuildScriptsOnly) != 0) return;
            var packs = ContentPackAssets.ShippedPacks();
            if (packs.Count == 0)
            {
                Debug.LogWarning($"[Build] No shipped mods under {ContentPackAssets.ShippedFolder}: the build has no base content.");
                return;
            }
            var started = EditorApplication.timeSinceStartup;
            var cache = Path.Combine(CacheRoot, options.target.ToString());
            var files = BundleFiles(packs);
            if (ModBundleBuilder.Build(packs, options.target, cache) == null)
                throw new BuildFailedException("Building the shipped mods failed (see the errors above).");
            var stage = Stage(cache, files);
            context.AddAdditionalPathToStreamingAssets(stage, DataPaths.ModsFolder);
            Debug.Log($"[Build] Shipped mods ({packs.Count}) built in {EditorApplication.timeSinceStartup - started:0} s, " +
                      $"added to StreamingAssets/{DataPaths.ModsFolder} from {Path.GetFullPath(stage)}: {string.Join(", ", files)}.");
        }

        // Read before the bundle build: it unloads the pack objects, the stale ones then list no content.
        static List<string> BundleFiles(List<ContentPack> packs)
        {
            var files = new List<string>();
            foreach (var p in packs)
            {
                var id = p.packId.ToLowerInvariant();
                files.Add(id + ModLoader.ModFileExtension);
                if (ModPackValidator.Scenes(p).Length > 0) files.Add(id + ModLoader.SceneBundleSuffix);
            }
            return files;
        }

        // The bundle cache also holds manifests and bundles of packs no longer shipped: only the mods themselves ship.
        static string Stage(string cache, List<string> files)
        {
            var stage = cache + "_ship";
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            foreach (var file in files) Copy(cache, stage, file);
            return stage;
        }

        static void Copy(string from, string to, string file)
        {
            var source = Path.Combine(from, file);
            if (!File.Exists(source)) throw new BuildFailedException($"Shipped mod bundle '{source}' is missing after the bundle build.");
            File.Copy(source, Path.Combine(to, file));
        }
    }
}
