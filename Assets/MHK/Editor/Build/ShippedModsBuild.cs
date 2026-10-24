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
    /// for the build target and adds the mods of <see cref="ExtraMods"/>. Desktop: into &lt;install&gt;/Mods/&lt;id&gt;/ next to
    /// the executable or .app (loose with <see cref="Loose"/>). Android: packed into StreamingAssets/Mods. Their pack
    /// and SDK commit is the game's (<see cref="BuildStamp.Commit"/>).
    /// </summary>
    sealed class ShippedModsBuild : BuildPlayerProcessor
    {
        /// <summary>Command-line option with a folder of built mods (&lt;id&gt;/ folders as ModBuildCli writes them) that ship too.</summary>
        public const string ExtraModsOption = "-mhkExtraMods";

        // Unity refuses AssetBundle output inside Library, so the Android stage lives in the ignored Builds folder.
        const string CacheRoot = "Builds/Cache/ShippedMods";

        /// <summary>Desktop builds ship loose textures and sounds (set by <see cref="CiBuild"/>, editor builds are packed).</summary>
        public static bool Loose { get; set; }

        /// <summary>Folder of extra built mods for the build target (<see cref="ExtraModsOption"/> by default), null for none.</summary>
        public static string ExtraMods { get; set; }

        public override int callbackOrder => 10;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var options = context.BuildPlayerOptions;
            if ((options.options & BuildOptions.BuildScriptsOnly) != 0) return;
            var packs = ContentPackAssets.ShippedPacks();
            if (packs.Count == 0) Debug.LogWarning($"[Build] No shipped mods under {ContentPackAssets.ShippedFolder}: the build has no base content.");
            var desktop = ModBundleBuilder.IsDesktop(options.target);
            var mods = desktop ? ModsFolder(options.locationPathName) : Path.Combine(CacheRoot, options.target.ToString());
            if (!desktop && Directory.Exists(mods)) Directory.Delete(mods, true);
            var commit = BuildStamp.Commit();
            if (packs.Count > 0 && ModBundleBuilder.Build(packs, options.target, mods, commit, commit, Loose && desktop) == null)
                throw new BuildFailedException("Building the shipped mods failed (see the errors above).");
            var extra = ExtraMods ?? CommandLine.Value(ExtraModsOption);
            if (!string.IsNullOrEmpty(extra)) CopyExtraMods(extra, mods, desktop);
            if (desktop) return;
            var stage = Flatten(mods);
            context.AddAdditionalPathToStreamingAssets(stage, DataPaths.ModsFolder);
            Debug.Log($"[Build] Shipped mods added to StreamingAssets/{DataPaths.ModsFolder}: {string.Join(", ", Directory.GetFiles(stage))}.");
        }

        /// <summary>The shipped mods folder of a desktop player at <paramref name="locationPathName"/>: Mods next to the executable or the .app.</summary>
        public static string ModsFolder(string locationPathName) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(locationPathName)), DataPaths.ModsFolder);

        // Desktop keeps each mod's folder (bundles and Assets/), Android gets only the bundles.
        static void CopyExtraMods(string from, string mods, bool desktop)
        {
            if (!Directory.Exists(from)) throw new BuildFailedException($"{ExtraModsOption}: folder '{from}' does not exist.");
            foreach (var source in Directory.GetDirectories(from))
            {
                var target = Path.Combine(mods, Path.GetFileName(source));
                if (Directory.Exists(target)) Directory.Delete(target, true);
                if (desktop) CopyFolder(source, target);
                else foreach (var file in Directory.GetFiles(source, "*" + ModLoader.ModFileExtension)) CopyFile(file, Path.Combine(target, Path.GetFileName(file)));
                Debug.Log($"[Build] Extra shipped mod '{Path.GetFileName(source)}' from {Path.GetFullPath(source)}.");
            }
        }

        static void CopyFolder(string from, string to)
        {
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                CopyFile(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }

        static void CopyFile(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
        }

        // ModLoader lists StreamingAssets/Mods of an APK flat: the bundles of every mod folder go into one stage folder.
        static string Flatten(string mods)
        {
            var stage = mods + "_ship";
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(mods);
            foreach (var file in Directory.GetFiles(mods, "*" + ModLoader.ModFileExtension, SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(stage, Path.GetFileName(file)), true);
            return stage;
        }
    }
}
