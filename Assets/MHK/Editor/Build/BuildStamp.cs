using System;
using System.Globalization;
using MHK.Core;
using MHK.Editor.Modding;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MHK.Editor.Build
{
    /// <summary>
    /// Stamps every player build with its commit (<see cref="BuildInfo.Commit"/>: Application.version "abc1234") and with
    /// a store build code (Android versionCode, macOS CFBundleVersion). The project settings get their own values back
    /// after the build, so a build writes nothing into tracked files.
    /// </summary>
    public sealed class BuildStamp : IPreprocessBuildWithContext
    {
        /// <summary>Command-line option with the commit of a CI build (else GITHUB_SHA, else the checkout's HEAD). The shipped mods get it too.</summary>
        public const string CommitOption = ModBundleBuilder.CommitOption;
        /// <summary>Command-line option with the store build code of a CI build (else the checkout's commit count).</summary>
        public const string BuildCodeOption = "-mhkBuildCode";

        static (string version, int androidCode, string macBuild)? saved;

        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildCallbackContext context)
        {
            if (context.IsPlayerBuild) Apply(context.Report.summary.platform);
        }

        // The stamp of a build for target, undone by Restore.
        static void Apply(BuildTarget target)
        {
            Restore();
            var id = Commit();
            var code = BuildCode();
            saved = (PlayerSettings.bundleVersion, PlayerSettings.Android.bundleVersionCode, PlayerSettings.macOS.buildNumber);
            PlayerSettings.bundleVersion = id;
            PlayerSettings.Android.bundleVersionCode = code;
            PlayerSettings.macOS.buildNumber = code.ToString(CultureInfo.InvariantCulture);
            Debug.Log($"[Build] Commit {id}, build code {code} ({target}).");
        }

        // The build saves the project settings with the stamp in them. This runs after every other build callback (the
        // Input System's preloaded asset too) has put its values back and saves them again.
        sealed class Unstamp : IPostprocessBuildWithContext
        {
            public int callbackOrder => int.MaxValue;

            public void OnPostprocessBuild(BuildCallbackContext context)
            {
                if (!context.IsPlayerBuild || saved == null) return;
                Restore();
                AssetDatabase.SaveAssets();
            }
        }

        static void Restore()
        {
            if (saved == null) return;
            var (version, androidCode, macBuild) = saved.Value;
            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode = androidCode;
            PlayerSettings.macOS.buildNumber = macBuild;
            saved = null;
        }

        /// <summary>The short commit this build is made from, with "-dirty" when the checkout has uncommitted changes, "local" without git.</summary>
        public static string Commit()
        {
            var given = CommandLine.Value(CommitOption);
            if (string.IsNullOrEmpty(given)) given = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (!string.IsNullOrEmpty(given)) return SourceCommit.Shorten(given);
            return SourceCommit.Head() ?? "local";
        }

        /// <summary>The store build code: <see cref="BuildCodeOption"/>, else the commit count of the checkout, else 1.</summary>
        public static int BuildCode()
        {
            if (CommandLine.TryInt(BuildCodeOption, out var code) && code > 0) return code;
            return int.TryParse(SourceCommit.Git("rev-list --count HEAD"), NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0 ? code : 1;
        }
    }
}
