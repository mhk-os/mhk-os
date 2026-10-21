using System;
using System.Diagnostics;
using UnityEditor.PackageManager;

namespace MHK.Editor
{
    /// <summary>
    /// Commits of the sources a build is made from, the only identity of the game, the SDK and every pack: a short git
    /// SHA, with "-dirty" when tracked files of the checkout have uncommitted changes.
    /// </summary>
    public static class SourceCommit
    {
        /// <summary>Characters of a short commit.</summary>
        public const int ShortLength = 7;
        const string DirtySuffix = "-dirty";
        const int GitTimeoutMs = 20000;

        /// <summary><paramref name="sha"/> (full or short, optionally with "-dirty") cut to <see cref="ShortLength"/> characters, "" for none.</summary>
        public static string Shorten(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha)) return "";
            sha = sha.Trim();
            var dirty = sha.EndsWith(DirtySuffix, StringComparison.Ordinal);
            var hex = dirty ? sha.Substring(0, sha.Length - DirtySuffix.Length) : sha;
            if (hex.Length > ShortLength) hex = hex.Substring(0, ShortLength);
            return dirty ? hex + DirtySuffix : hex;
        }

        /// <summary>HEAD of the git checkout holding <paramref name="folder"/> (null: the project folder), null without git.</summary>
        public static string Head(string folder = null)
        {
            var head = Git("rev-parse --short=" + ShortLength + " HEAD", folder);
            if (string.IsNullOrEmpty(head)) return null;
            return string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no", folder)) ? head : head + DirtySuffix;
        }

        /// <summary>
        /// Commit of the SDK package: the hash of a git package, else HEAD of the checkout holding an embedded or local
        /// package, else null. A package in the package cache sits inside the mod project's checkout, its HEAD says nothing.
        /// </summary>
        public static string Sdk()
        {
            var info = SdkPaths.Info;
            if (info == null) return null;
            if (info.source == PackageSource.Git) return string.IsNullOrEmpty(info.git?.hash) ? null : Shorten(info.git.hash);
            return info.source is PackageSource.Embedded or PackageSource.Local ? Head(info.resolvedPath) : null;
        }

        /// <summary>Trimmed standard output of git run in <paramref name="folder"/> (null: the project folder), null when git is missing, fails or hangs.</summary>
        public static string Git(string arguments, string folder = null)
        {
            try
            {
                using var git = Process.Start(new ProcessStartInfo("git", arguments)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    WorkingDirectory = folder ?? "",
                });
                if (git == null) return null;
                var output = git.StandardOutput.ReadToEndAsync();
                git.StandardError.ReadToEndAsync();
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
