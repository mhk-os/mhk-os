using System;
using System.IO;
using System.Linq;
using MHK.Assets;
using MHK.Editor.Modding;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MHK.Editor.Build
{
    /// <summary>
    /// The player's own replaceable textures and sounds (pack id "game"): every player build gets their
    /// <see cref="PackAssetIndex"/> in Resources. A loose desktop build (<see cref="ShippedModsBuild.Loose"/>) writes them
    /// to &lt;install&gt;/Mods/core/Assets/ and builds the player with placeholders. Runs after the shipped mods.
    /// </summary>
    sealed class GameAssetsBuild : BuildPlayerProcessor
    {
        /// <summary>Asset path of the temporary index.</summary>
        public const string IndexPath = "Assets/Resources/" + PackAssetIndex.GameResource + ".asset";
        /// <summary>The base content's folder (shipped like a mod) holds the player's loose files.</summary>
        public const string LooseModId = MHK.Modding.ModLoader.CorePackId;

        static BuildRestoreJournal journal;
        static PlaceholderImportScope placeholders;

        public override int callbackOrder => 20;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            Finish();
            var options = context.BuildPlayerOptions;
            if ((options.options & BuildOptions.BuildScriptsOnly) != 0) return;
            var loose = ShippedModsBuild.Loose && ModBundleBuilder.IsDesktop(options.target);
            var items = PackAssetPaths.Collect(PlayerRoots(), options.scenes, PackAssetPaths.Folders(ContentPackAssets.ShippedPacks()));
            if (loose)
            {
                var folder = Path.Combine(ShippedModsBuild.ModsFolder(options.locationPathName), LooseModId, ModBundleBuilder.LooseFolder);
                LooseAssetExport.Export(items, options.target, folder, false);
            }
            journal = new BuildRestoreJournal();
            try
            {
                if (loose) placeholders = new PlaceholderImportScope(items.Select(i => i.assetPath), options.target);
                PackAssetIndexBuild.Create(IndexPath, PackAssetPaths.GamePackId, loose, items, false, journal);
            }
            catch
            {
                Finish();
                throw;
            }
            Debug.Log($"[Build] Player assets: {items.Count} replaceable ({(loose ? "loose" : "packed")}), index {IndexPath}.");
        }

        // Only a successful build gets here: a failed one is finished by the next build or by CiBuild.
        sealed class Cleanup : IPostprocessBuildWithContext
        {
            public int callbackOrder => 0;

            public void OnPostprocessBuild(BuildCallbackContext context)
            {
                if (context.IsPlayerBuild) Finish();
            }
        }

        /// <summary>Restores the import settings and removes the index of the last build (also after a failed build).</summary>
        public static void Finish()
        {
            placeholders?.Dispose();
            placeholders = null;
            journal?.Dispose();
            journal = null;
        }

        // What the player holds besides its scenes: Resources folders (not the editor's) and the preloaded assets.
        static string[] PlayerRoots() =>
            AssetDatabase.GetAllAssetPaths()
                .Where(p => p.Contains("/Resources/", StringComparison.Ordinal) && !p.Contains("/Editor/", StringComparison.Ordinal) &&
                            !AssetDatabase.IsValidFolder(p) && p != IndexPath)
                .Concat(PlayerSettings.GetPreloadedAssets().Where(a => a != null).Select(AssetDatabase.GetAssetPath))
                .Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
    }
}
