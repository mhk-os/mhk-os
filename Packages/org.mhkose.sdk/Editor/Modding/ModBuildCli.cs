using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Command-line mod build for CI: <c>-executeMethod MHK.Editor.Modding.ModBuildCli.Build -mhkPacks &lt;pack asset
    /// paths&gt; -mhkOut &lt;folder&gt; [-mhkLoose true|false] [-mhkCommit &lt;sha&gt;] [-mhkSdkCommit &lt;sha&gt;]</c> (with
    /// <c>-batchmode -quit -buildTarget</c>). Builds for the active build target into &lt;folder&gt;/&lt;target&gt;/&lt;packid&gt;/
    /// (loose by default on desktop targets) and exits with a non-zero code on failure. The commits default to the git
    /// checkouts of the project and the SDK, which git in a CI container may refuse to read.
    /// </summary>
    public static class ModBuildCli
    {
        public const string PacksOption = "-mhkPacks";
        public const string OutOption = "-mhkOut";
        /// <summary>Command-line option: "false" bundles the textures and sounds also on desktop targets.</summary>
        public const string LooseOption = "-mhkLoose";
        const string DefaultOut = "Builds/Mods";
        const int FailureExitCode = 1;

        /// <summary>The <c>-executeMethod</c> entry: reads the options, builds and exits (batch mode only, else it only logs the result).</summary>
        public static void Build()
        {
            var ok = false;
            try
            {
                var args = Environment.GetCommandLineArgs();
                var outRoot = OptionValues(args, OutOption).FirstOrDefault() ?? DefaultOut;
                var commit = OptionValues(args, ModBundleBuilder.CommitOption).FirstOrDefault();
                var sdkCommit = OptionValues(args, ModBundleBuilder.SdkCommitOption).FirstOrDefault();
                var loose = OptionValues(args, LooseOption).FirstOrDefault() != "false";
                ok = Run(OptionValues(args, PacksOption), outRoot, commit, sdkCommit, loose) != null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : FailureExitCode);
        }

        /// <summary>
        /// Builds the packs at <paramref name="packPaths"/> (asset paths, entries may hold several joined by ',' or ';') for
        /// the active build target into <paramref name="outRoot"/>/&lt;target&gt;/&lt;packid&gt;/, stamped with <paramref name="commit"/>
        /// and <paramref name="sdkCommit"/> (null: from git), <paramref name="loose"/> on desktop targets. Returns the output folder, null on failure.
        /// </summary>
        public static string Run(IEnumerable<string> packPaths, string outRoot, string commit = null, string sdkCommit = null, bool loose = true)
        {
            var paths = packPaths.SelectMany(p => p.Split(',', ';')).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (paths.Count == 0)
            {
                Debug.LogError($"[Mods] {PacksOption}: no pack asset paths given.");
                return null;
            }
            var packs = new List<ContentPack>();
            foreach (var path in paths)
            {
                var pack = AssetDatabase.LoadAssetAtPath<ContentPack>(path);
                if (pack == null)
                {
                    Debug.LogError($"[Mods] {PacksOption}: '{path}' is not a ContentPack asset.");
                    return null;
                }
                packs.Add(pack);
            }
            var target = EditorUserBuildSettings.activeBuildTarget;
            return ModBundleBuilder.Build(packs, target, Path.Combine(outRoot, target.ToString()), commit, sdkCommit, loose);
        }

        // The arguments after an option up to the next option (one starting with '-').
        static IEnumerable<string> OptionValues(string[] args, string option)
        {
            var i = Array.IndexOf(args, option);
            if (i < 0) yield break;
            for (i++; i < args.Length && !args[i].StartsWith("-", StringComparison.Ordinal); i++) yield return args[i];
        }
    }
}
