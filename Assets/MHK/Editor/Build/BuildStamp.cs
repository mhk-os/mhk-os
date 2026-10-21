using System;
using System.Diagnostics;
using System.Globalization;
using MHK.Core;
using UnityEditor;
using UnityEditor.Build;
using Debug = UnityEngine.Debug;

namespace MHK.Editor.Build
{
    /// <summary>
    /// Stamps every player build with the SDK version and the commit (<see cref="BuildInfo"/>: Application.version
    /// "0.1.0+abc1234") and with a store build code (Android versionCode, macOS CFBundleVersion). The project settings
    /// get their own values back after the build, so a build writes nothing into tracked files.
    /// </summary>
    public sealed class BuildStamp : IPreprocessBuildWithContext
    {
        /// <summary>Command-line option with the commit of a CI build (else GITHUB_SHA, else the checkout's HEAD).</summary>
        public const string CommitOption = "-mhkCommit";
        /// <summary>Command-line option with the store build code of a CI build (else the checkout's commit count).</summary>
        public const string BuildCodeOption = "-mhkBuildCode";
        const int ShortShaLength = 7;
        const int GitTimeoutMs = 20000;

        static (string version, int androidCode, string macBuild)? saved;

        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildCallbackContext context)
        {
            if (!context.IsPlayerBuild) return;
            Restore();
            var id = BuildInfo.Compose(BuildInfo.Version, Commit());
            var code = BuildCode();
            saved = (PlayerSettings.bundleVersion, PlayerSettings.Android.bundleVersionCode, PlayerSettings.macOS.buildNumber);
            PlayerSettings.bundleVersion = id;
            PlayerSettings.Android.bundleVersionCode = code;
            PlayerSettings.macOS.buildNumber = code.ToString(CultureInfo.InvariantCulture);
            Debug.Log($"[Build] {id}, build code {code} ({context.Report.summary.platform}).");
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

        /// <summary>The short commit this build is made from, with "-dirty" when the checkout has uncommitted changes.</summary>
        public static string Commit()
        {
            var given = CommandLine.Value(CommitOption);
            if (string.IsNullOrEmpty(given)) given = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (!string.IsNullOrEmpty(given)) return given.Length > ShortShaLength ? given.Substring(0, ShortShaLength) : given;
            var head = Git("rev-parse --short=" + ShortShaLength + " HEAD");
            if (string.IsNullOrEmpty(head)) return "local";
            return string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no")) ? head : head + "-dirty";
        }

        /// <summary>The store build code: <see cref="BuildCodeOption"/>, else the commit count of the checkout, else 1.</summary>
        public static int BuildCode()
        {
            if (CommandLine.TryInt(BuildCodeOption, out var code) && code > 0) return code;
            return int.TryParse(Git("rev-list --count HEAD"), NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0 ? code : 1;
        }

        // Trimmed standard output, null when git is missing, fails or hangs.
        static string Git(string arguments)
        {
            try
            {
                using var git = Process.Start(new ProcessStartInfo("git", arguments)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                });
                if (git == null) return null;
                var output = git.StandardOutput.ReadToEndAsync();
                if (!git.WaitForExit(GitTimeoutMs) || git.ExitCode != 0) return null;
                return output.Result.Trim();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
