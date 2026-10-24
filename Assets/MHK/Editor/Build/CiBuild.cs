using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MHK.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MHK.Editor.Build
{
    /// <summary>
    /// Player builds of the release targets (CI and local). Command line: <c>-executeMethod MHK.Editor.Build.CiBuild.Build
    /// -mhkTarget macos-arm64 -mhkOut build/macos-arm64</c> plus the <see cref="BuildStamp"/> options, exit code 0 on success.
    /// </summary>
    public static class CiBuild
    {
        public const string TargetOption = "-mhkTarget";
        public const string OutOption = "-mhkOut";
        /// <summary>File name of the player executable (and of the .app and .apk).</summary>
        public const string ExecutableName = "MoorhuhnKart";

        /// <summary>A release target: its CI name, Unity build target, CPU architecture (null: the platform's only one) and player file extension.</summary>
        public readonly struct Target
        {
            public readonly string name;
            public readonly BuildTarget buildTarget;
            public readonly OSArchitecture? architecture;
            public readonly string extension;

            public Target(string name, BuildTarget buildTarget, OSArchitecture? architecture, string extension)
            {
                this.name = name;
                this.buildTarget = buildTarget;
                this.architecture = architecture;
                this.extension = extension;
            }
        }

        // Scripting backends come from the Player Settings: Mono on desktop (deterministic ghosts), IL2CPP ARM64 on Android.
        // Unity 6.6 has no desktop Linux Arm64 player (Arm64 Linux is Dedicated Server or Embedded Linux only), so no linux-arm64.
        public static readonly Target[] Targets =
        {
            new("android", BuildTarget.Android, null, ".apk"),
            new("macos-arm64", BuildTarget.StandaloneOSX, OSArchitecture.ARM64, ".app"),
            new("macos-x64", BuildTarget.StandaloneOSX, OSArchitecture.x64, ".app"),
            new("linux-x64", BuildTarget.StandaloneLinux64, null, ".x86_64"),
            new("windows-x64", BuildTarget.StandaloneWindows64, OSArchitecture.x64, ".exe"),
            new("windows-arm64", BuildTarget.StandaloneWindows64, OSArchitecture.ARM64, ".exe"),
        };

        /// <summary>Command-line entry point: builds <see cref="TargetOption"/> into <see cref="OutOption"/> and quits with the result.</summary>
        public static void Build()
        {
            var ok = false;
            try
            {
                ok = Run(CommandLine.Value(TargetOption), CommandLine.Value(OutOption));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Builds the target named <paramref name="targetName"/> into <paramref name="outFolder"/> (default Builds/&lt;target&gt;)
        /// with the global scene list. The editor's architecture setting gets its value back afterwards. A target other than
        /// the active one leaves the editor switched to it (Unity defers that switch past the build). True on success.
        /// </summary>
        public static bool Run(string targetName, string outFolder)
        {
            var target = Targets.FirstOrDefault(t => t.name == targetName);
            if (target.name == null)
            {
                Debug.LogError($"[Build] Unknown target '{targetName}' ({TargetOption}), use one of: {string.Join(", ", Targets.Select(t => t.name))}.");
                return false;
            }
            var architecture = ArchitectureSetting(target.buildTarget);
            if (target.architecture != null && architecture == null)
            {
                Debug.LogError($"[Build] {target.name}: the {target.buildTarget} build support module is not installed.");
                return false;
            }
            var previous = target.architecture != null ? architecture.GetValue(null) : null;
            try
            {
                if (previous != null) architecture.SetValue(null, target.architecture.Value);
                return BuildPlayer(target, Path.GetFullPath(string.IsNullOrEmpty(outFolder) ? Path.Combine("Builds", target.name) : outFolder));
            }
            finally
            {
                if (previous != null) architecture.SetValue(null, previous);
            }
        }

        static bool BuildPlayer(Target target, string outFolder)
        {
            Directory.CreateDirectory(outFolder);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                target = target.buildTarget,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target.buildTarget),
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = Path.Combine(outFolder, ExecutableName + target.extension),
            });
            var summary = report.summary;
            Debug.Log($"[Build] {target.name}: {summary.result} in {summary.totalTime.TotalSeconds:0} s, {summary.totalSize / 1048576f:0} MB, " +
                      $"{summary.totalErrors} errors, into {outFolder}.");
            return summary.result == BuildResult.Succeeded;
        }

        // The UserBuildSettings class of the platform's build support module, null when the module is not installed.
        static PropertyInfo ArchitectureSetting(BuildTarget target)
        {
            var typeName = target switch
            {
                BuildTarget.StandaloneOSX => "UnityEditor.OSXStandalone.UserBuildSettings",
                BuildTarget.StandaloneWindows64 => "UnityEditor.WindowsStandalone.UserBuildSettings",
                _ => null,
            };
            if (typeName == null) return null;
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            return type?.GetProperty("architecture", BindingFlags.Public | BindingFlags.Static);
        }
    }
}
