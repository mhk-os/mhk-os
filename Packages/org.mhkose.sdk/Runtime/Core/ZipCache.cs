using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// Extracts zips (mods, asset packs) into a cache folder keyed by the zip's name, size and date, so an unchanged zip
    /// is extracted once and a changed one again.
    /// </summary>
    public static class ZipCache
    {
        const string KeySeparator = "-";

        /// <summary>
        /// The extracted folder of <paramref name="zipPath"/> inside <paramref name="cacheRoot"/>, extracting it when its key
        /// changed (older extractions of the same zip are removed). Null when the zip cannot be read.
        /// </summary>
        public static string Extract(string zipPath, string cacheRoot)
        {
            try
            {
                var info = new FileInfo(zipPath);
                var name = Path.GetFileNameWithoutExtension(zipPath);
                var target = Path.Combine(cacheRoot, name + KeySeparator + info.Length.ToString(CultureInfo.InvariantCulture) +
                                                     KeySeparator + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                RemoveStale(cacheRoot, name, target);
                if (Directory.Exists(target)) return target;
                var temp = target + ".partial";
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                ZipFile.ExtractToDirectory(zipPath, temp);
                Directory.Move(temp, target);
                Debug.Log($"[Zip] Extracted '{zipPath}' to '{target}'.");
                return target;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zip] Cannot extract '{zipPath}': {e.Message}");
                return null;
            }
        }

        /// <summary>Removes every extraction in <paramref name="cacheRoot"/> that is not one of <paramref name="keepFolders"/> (zips that are gone).</summary>
        public static void Prune(string cacheRoot, ICollection<string> keepFolders)
        {
            if (!Directory.Exists(cacheRoot)) return;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keepFolders) keep.Add(Path.GetFullPath(k));
            foreach (var dir in Directory.GetDirectories(cacheRoot))
                if (!keep.Contains(Path.GetFullPath(dir))) Delete(dir);
        }

        static void RemoveStale(string cacheRoot, string name, string target)
        {
            if (!Directory.Exists(cacheRoot)) return;
            foreach (var dir in Directory.GetDirectories(cacheRoot, name + KeySeparator + "*"))
                if (dir != target && IsKeyOf(Path.GetFileName(dir), name)) Delete(dir);
        }

        // "name-size-ticks": a zip named "name-2" must not remove the extraction of "name".
        static bool IsKeyOf(string folder, string name)
        {
            var rest = folder.Substring(name.Length + KeySeparator.Length);
            if (rest.EndsWith(".partial", StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - ".partial".Length);
            var parts = rest.Split(KeySeparator[0]);
            return parts.Length == 2 && long.TryParse(parts[0], out _) && long.TryParse(parts[1], out _);
        }

        static void Delete(string dir)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zip] Cannot remove '{dir}': {e.Message}");
            }
        }
    }
}
