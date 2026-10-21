using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Builds <see cref="ContentPack"/>s into per-platform "&lt;packId&gt;.mhkmod" AssetBundles (content plus a separate
    /// scene bundle) under Builds/Mods/&lt;platform&gt;. Copy both files to &lt;persistentDataPath&gt;/Mods to install.
    /// </summary>
    public static class ModBundleBuilder
    {
        internal const float BytesPerMB = 1048576f;
        /// <summary>Command-line option with the commit of the project the packs are built from (full or short SHA).</summary>
        public const string CommitOption = "-mhkCommit";
        /// <summary>Command-line option with the commit of the SDK the packs are built with (full or short SHA).</summary>
        public const string SdkCommitOption = "-mhkSdkCommit";

        /// <summary>
        /// Checks and builds <paramref name="packs"/> for <paramref name="target"/> into <paramref name="outDir"/> (default
        /// Builds/Mods/&lt;target&gt;). The built packs and the packs they include carry the commit of the project
        /// (<paramref name="commit"/>, else <see cref="ProjectCommit"/>) as their version and the build stamp (SDK commit,
        /// <paramref name="sdkCommit"/> else <see cref="SourceCommit.Sdk"/>, Unity version, target), the project's pack
        /// assets keep their own values. Returns the folder, or null when a check or the build failed (errors are logged).
        /// </summary>
        public static string Build(IList<ContentPack> packs, BuildTarget target, string outDir = null, string commit = null, string sdkCommit = null)
        {
            if (packs.Any(p => !ModPackValidator.Check(p))) return null;
            outDir ??= Path.Combine("Builds", "Mods", target.ToString());
            Directory.CreateDirectory(outDir);
            var builds = CreateBundleDefinitions(packs);
            foreach (var p in packs) ModDependencyReport.Log(p);
            commit = string.IsNullOrEmpty(commit) ? ProjectCommit() : SourceCommit.Shorten(commit);
            sdkCommit = string.IsNullOrEmpty(sdkCommit) ? SourceCommit.Sdk() ?? "" : SourceCommit.Shorten(sdkCommit);
            var stamped = Stamp(packs, commit, sdkCommit, target);
            try
            {
                if (!BuildBundles(outDir, builds, target)) return null;
            }
            finally
            {
                Unstamp(stamped);
            }
            LogBundleSizes(outDir, builds, target);
            Debug.Log($"[Mods] Built {packs.Count} mod(s) ({builds.Length} bundles) for {target}, commit {Label(commit)}, SDK {Label(sdkCommit)}, into {Path.GetFullPath(outDir)}");
            return outDir;
        }

        /// <summary>HEAD of the git checkout of this project ("-dirty" with changed tracked files), "" without git.</summary>
        public static string ProjectCommit() => SourceCommit.Head() ?? "";

        static string Label(string commit) => string.IsNullOrEmpty(commit) ? "unknown" : commit;

        static bool BuildBundles(string outDir, AssetBundleBuild[] builds, BuildTarget target)
        {
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

        // Bundles are built from the saved assets, so the stamp is saved for the build and the asset's values come back afterwards.
        static List<(string path, string version)> Stamp(IEnumerable<ContentPack> packs, string commit, string sdkCommit, BuildTarget target)
        {
            var stamped = new List<(string, string)>();
            foreach (var p in packs.SelectMany(x => x.SelfAndIncluded()).Distinct())
            {
                stamped.Add((AssetDatabase.GetAssetPath(p), p.version));
                SetStamp(p, commit, sdkCommit, Application.unityVersion, target.ToString());
            }
            return stamped;
        }

        // The build unloads the pack objects: they are loaded again by path.
        static void Unstamp(List<(string path, string version)> stamped)
        {
            foreach (var (path, version) in stamped)
            {
                var p = AssetDatabase.LoadAssetAtPath<ContentPack>(path);
                if (p != null) SetStamp(p, version, "", "", "");
            }
        }

        static void SetStamp(ContentPack p, string version, string sdkCommit, string unityVersion, string buildTarget)
        {
            p.version = version;
            p.sdkVersion = sdkCommit;
            p.unityVersion = unityVersion;
            p.buildTarget = buildTarget;
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
        }

        static AssetBundleBuild[] CreateBundleDefinitions(IEnumerable<ContentPack> packs)
        {
            var builds = new List<AssetBundleBuild>();
            foreach (var p in packs)
            {
                builds.Add(new AssetBundleBuild
                {
                    assetBundleName = p.packId + ModLoader.ModFileExtension,
                    assetNames = new[] { AssetDatabase.GetAssetPath(p) },
                });
                var scenes = ModPackValidator.Scenes(p);
                if (scenes.Length > 0)
                    builds.Add(new AssetBundleBuild { assetBundleName = p.packId + ModLoader.SceneBundleSuffix, assetNames = scenes });
            }
            return builds.ToArray();
        }

        static void LogBundleSizes(string outDir, IEnumerable<AssetBundleBuild> builds, BuildTarget target)
        {
            foreach (var b in builds)
            {
                var file = Path.Combine(outDir, b.assetBundleName);
                if (File.Exists(file)) Debug.Log($"[Mods] {target}: {b.assetBundleName} {new FileInfo(file).Length / BytesPerMB:0.0} MB");
            }
        }
    }
}
