using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MHK.Ghosts
{
    /// <summary>File and folder names of <see cref="LocalGhostStore"/>. They are on players' disks: change them only with <see cref="GhostFile.FormatVersion"/>.</summary>
    static class GhostPaths
    {
        public const string SearchPattern = "*" + GhostFile.Extension;
        public const string ExportFolderName = "Export";
        public const string ImportFolderName = "Import";
        const string BestPrefix = "best-", LastPrefix = "last-", SavedPrefix = "saved-", ImportPrefix = "import-";
        const int ExportIdLength = 8;

        /// <summary>
        /// The personal best of an engine class, lap count and track variant (one per record slot of a track folder):
        /// <c>best-&lt;class&gt;-&lt;laps&gt;[-&lt;variant key&gt;]</c>.
        /// </summary>
        public static string Best(int engineClass, int laps, string variant) => BestPrefix + Slot(engineClass, laps, variant) + GhostFile.Extension;
        /// <summary>The last run that was no new best, named like <see cref="Best"/>.</summary>
        public static string Last(int engineClass, int laps, string variant) => LastPrefix + Slot(engineClass, laps, variant) + GhostFile.Extension;
        public static string Saved(string id) => SavedPrefix + Safe(id) + GhostFile.Extension;
        public static string Imported(string id) => ImportPrefix + Safe(id) + GhostFile.Extension;

        /// <summary>Folders under the store root that are not a track's: export, import and the download cache.</summary>
        public static bool IsStoreFolder(string folderName) =>
            folderName is ExportFolderName or ImportFolderName or MHK.Online.OnlineGhostCache.FolderName;

        public static GhostKind KindOf(string path)
        {
            var file = Path.GetFileNameWithoutExtension(path);
            return file.StartsWith(BestPrefix, StringComparison.Ordinal) ? GhostKind.Best
                : file.StartsWith(LastPrefix, StringComparison.Ordinal) ? GhostKind.LastRun
                : file.StartsWith(ImportPrefix, StringComparison.Ordinal) ? GhostKind.Imported
                : GhostKind.Saved;
        }

        /// <summary>Export file name: track, variant, laps, class, time, player and the start of the id.</summary>
        public static string ExportName(GhostHeader h) =>
            Safe($"{h.trackId}{(string.IsNullOrEmpty(h.variant) ? "" : "_" + h.variant)}_{h.laps}L_{h.engineClassLabel}_{FileTime(h.finishTime)}_{h.playerName}_" +
                 h.id.Substring(0, Math.Min(ExportIdLength, h.id.Length))) + GhostFile.Extension;

        /// <summary><paramref name="name"/> with spaces and characters not allowed in file names replaced by '_'.</summary>
        public static string Safe(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (name ?? "").Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
            return chars.Length > 0 ? new string(chars) : "_";
        }

        // Variant keys are lowercase [a-z0-9_.-] tokens joined by '+', all allowed in file names everywhere.
        static string Slot(int engineClass, int laps, string variant) =>
            string.Format(CultureInfo.InvariantCulture, "{0}-{1}", engineClass, laps) + (string.IsNullOrEmpty(variant) ? "" : "-" + Safe(variant));

        static string FileTime(float seconds)
        {
            var m = (int)(seconds / 60f);
            var s = seconds - m * 60;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}m{1:00.00}s", m, s).Replace('.', '-');
        }
    }
}
