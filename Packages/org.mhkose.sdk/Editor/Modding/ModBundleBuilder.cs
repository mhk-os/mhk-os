using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Builds <see cref="ContentPack"/>s into mod folders: &lt;out&gt;/&lt;packid&gt;/ with the "&lt;packid&gt;.mhkmod" AssetBundle
    /// (content and a <see cref="MHK.Assets.PackAssetIndex"/>), the optional scene bundle and, for a loose desktop build,
    /// Assets/ with the textures and sounds as files (the bundles then hold placeholders). Copy the folder into the Mods
    /// folder to install.
    /// </summary>
    public static class ModBundleBuilder
    {
        internal const float BytesPerMB = 1048576f;
        /// <summary>Command-line option with the commit of the project the packs are built from (full or short SHA).</summary>
        public const string CommitOption = "-mhkCommit";
        /// <summary>Command-line option with the commit of the SDK the packs are built with (full or short SHA).</summary>
        public const string SdkCommitOption = "-mhkSdkCommit";
        /// <summary>Folder of a mod with the loose files of its textures and sounds.</summary>
        public const string LooseFolder = "Assets";

        // Unity refuses AssetBundle output inside Library, so the incremental cache lives in the ignored Builds folder.
        const string CacheRoot = "Builds/Cache/Bundles";

        sealed class Plan
        {
            public string id, path, indexPath;
            public string[] scenes;
            public List<PackAssetPaths.Item> items;
        }

        /// <summary>Whether <paramref name="target"/> is a desktop platform, where builds can ship loose files.</summary>
        public static bool IsDesktop(BuildTarget target) =>
            target is BuildTarget.StandaloneOSX or BuildTarget.StandaloneWindows64 or BuildTarget.StandaloneLinux64;

        /// <summary>
        /// Checks and builds <paramref name="packs"/> for <paramref name="target"/> into one folder per pack under
        /// <paramref name="outDir"/> (default Builds/Mods/&lt;target&gt;). <paramref name="loose"/> (desktop targets only)
        /// writes the textures and sounds as files and bundles placeholders. The built packs carry the commit of the
        /// project (<paramref name="commit"/>, else <see cref="ProjectCommit"/>) as their version and the build stamp (mod
        /// API level <see cref="ModSdk.ApiLevel"/>, SDK commit, <paramref name="sdkCommit"/> else <see cref="SourceCommit.Sdk"/>, Unity version, target). Every project
        /// file the build changes comes back byte for byte. Returns the folder, or null when a check or the build failed.
        /// </summary>
        public static string Build(IList<ContentPack> packs, BuildTarget target, string outDir = null, string commit = null,
                                   string sdkCommit = null, bool loose = false)
        {
            if (packs.Any(p => !ModPackValidator.Check(p))) return null;
            outDir ??= Path.Combine("Builds", "Mods", target.ToString());
            loose &= IsDesktop(target);
            var started = EditorApplication.timeSinceStartup;
            foreach (var p in packs) ModDependencyReport.Log(p);
            commit = string.IsNullOrEmpty(commit) ? ProjectCommit() : SourceCommit.Shorten(commit);
            sdkCommit = string.IsNullOrEmpty(sdkCommit) ? SourceCommit.Sdk() ?? "" : SourceCommit.Shorten(sdkCommit);
            var plans = packs.Select(CreatePlan).ToList();
            foreach (var plan in plans) ResetFolder(Path.Combine(outDir, plan.id));
            if (loose)
                foreach (var plan in plans) LooseAssetExport.Export(plan.items, target, Path.Combine(outDir, plan.id, LooseFolder));
            var cache = Path.Combine(CacheRoot, target.ToString());
            using (var journal = new BuildRestoreJournal())
            using (loose ? new PlaceholderImportScope(plans.SelectMany(p => p.items).Select(i => i.assetPath), target) : null)
            {
                foreach (var plan in plans) Stamp(plan, commit, sdkCommit, target, loose, journal);
                foreach (var plan in plans)
                    if (!BuildBundles(Path.Combine(cache, plan.id), CreateBundleDefinitions(plan), target)) return null;
            }
            foreach (var plan in plans) CopyBundles(Path.Combine(cache, plan.id), plan, outDir);
            LogSizes(outDir, plans, target);
            Debug.Log($"[Mods] Built {packs.Count} mod(s) for {target} ({(loose ? "loose" : "packed")}) in {EditorApplication.timeSinceStartup - started:0} s, " +
                      $"commit {Label(commit)}, SDK {Label(sdkCommit)}, into {Path.GetFullPath(outDir)}");
            return outDir;
        }

        /// <summary>HEAD of the git checkout of this project ("-dirty" with changed tracked files), "" without git.</summary>
        public static string ProjectCommit() => SourceCommit.Head() ?? "";

        static string Label(string commit) => string.IsNullOrEmpty(commit) ? "unknown" : commit;

        // Read before the bundle build: it unloads the pack objects, the stale ones then list no content. Every sound in a
        // pack's Music folder ships, also those no definition uses (the music library lists them).
        static Plan CreatePlan(ContentPack pack)
        {
            var path = AssetDatabase.GetAssetPath(pack);
            var scenes = ModPackValidator.Scenes(pack);
            var id = pack.packId.ToLowerInvariant();
            var folders = PackAssetPaths.Folders(new[] { pack });
            return new Plan
            {
                id = id,
                path = path,
                indexPath = $"{PackAssetIndexBuild.TempFolder}/{id}.asset",
                scenes = scenes,
                items = PackAssetPaths.Collect(PackAssetPaths.MusicFiles(folders).Prepend(path), scenes, folders),
            };
        }

        static void ResetFolder(string folder)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
        }

        static bool BuildBundles(string outDir, AssetBundleBuild[] builds, BuildTarget target)
        {
            Directory.CreateDirectory(outDir);
            using (new LightmapFogVariantScope())
            {
                var manifest = BuildPipeline.BuildAssetBundles(new BuildAssetBundlesParameters
                {
                    outputPath = outDir,
                    bundleDefinitions = builds,
                    options = BuildAssetBundleOptions.ChunkBasedCompression,
                    targetPlatform = target,
                });
                if (manifest != null) return true;
            }
            Debug.LogError($"[Mods] AssetBundle build for {target} failed.");
            return false;
        }

        // Bundles are built from the saved assets: the stamp and the index are saved for the build, the journal brings the files back.
        static void Stamp(Plan plan, string commit, string sdkCommit, BuildTarget target, bool loose, BuildRestoreJournal journal)
        {
            var index = PackAssetIndexBuild.Create(plan.indexPath, plan.id, loose, plan.items, true, journal);
            var pack = AssetDatabase.LoadAssetAtPath<ContentPack>(plan.path);
            foreach (var p in pack.SelfAndIncluded().Distinct())
            {
                journal.Save(AssetDatabase.GetAssetPath(p));
                p.version = commit;
                p.sdkVersion = sdkCommit;
                p.sdkApi = ModSdk.ApiLevel;
                p.unityVersion = Application.unityVersion;
                p.buildTarget = target.ToString();
                p.assetIndex = p == pack ? index : null;
                EditorUtility.SetDirty(p);
                AssetDatabase.SaveAssetIfDirty(p);
            }
        }

        // Every indexed asset is assigned to the content bundle, so the scene bundle uses the same object as the index.
        static AssetBundleBuild[] CreateBundleDefinitions(Plan p)
        {
            var builds = new List<AssetBundleBuild>
            {
                new()
                {
                    assetBundleName = p.id + ModLoader.ModFileExtension,
                    assetNames = new[] { p.path }.Concat(p.items.Select(i => i.assetPath)).ToArray(),
                },
            };
            if (p.scenes.Length > 0) builds.Add(new AssetBundleBuild { assetBundleName = p.id + ModLoader.SceneBundleSuffix, assetNames = p.scenes });
            return builds.ToArray();
        }

        static void CopyBundles(string cache, Plan plan, string outDir)
        {
            foreach (var file in BundleFiles(plan))
            {
                var source = Path.Combine(cache, file);
                if (File.Exists(source)) File.Copy(source, Path.Combine(outDir, plan.id, file), true);
                else Debug.LogError($"[Mods] Bundle '{source}' is missing after the build.");
            }
        }

        static IEnumerable<string> BundleFiles(Plan plan)
        {
            yield return plan.id + ModLoader.ModFileExtension;
            if (plan.scenes.Length > 0) yield return plan.id + ModLoader.SceneBundleSuffix;
        }

        static void LogSizes(string outDir, IEnumerable<Plan> plans, BuildTarget target)
        {
            foreach (var plan in plans)
            {
                var folder = Path.Combine(outDir, plan.id);
                var bundles = BundleFiles(plan).Select(f => Path.Combine(folder, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
                var loose = Path.Combine(folder, LooseFolder);
                var files = Directory.Exists(loose) ? Directory.GetFiles(loose, "*", SearchOption.AllDirectories) : new string[0];
                Debug.Log($"[Mods] {target}: {plan.id} bundles {bundles / BytesPerMB:0.0} MB, {plan.items.Count} indexed assets " +
                          $"({plan.items.Count(i => i.scenes.Length > 0)} only in scenes), " +
                          $"{files.Length} loose files {files.Sum(f => new FileInfo(f).Length) / BytesPerMB:0.0} MB");
            }
        }
    }
}
