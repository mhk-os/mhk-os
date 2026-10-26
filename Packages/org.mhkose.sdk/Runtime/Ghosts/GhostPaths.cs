using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MHK.Ghosts
{
    /// <summary>File and folder names of <see cref="LocalGhostStore"/>. They are on players' disks: never change them.</summary>
    static class GhostPaths
    {
        public const string SearchPattern = "*" + GhostFile.Extension;
        public const string ExportFolderName = "Export";
        public const string ImportFolderName = "Import";
        public const string TrackHashFile = "track.hash";
        const string BestPrefix = "best-", LastPrefix = "last-", SavedPrefix = "saved-", ImportPrefix = "import-";
        const int ExportIdLength = 8;

        public static string Best(int engineClass) => BestPrefix + engineClass + GhostFile.Extension;
        public static string Last(int engineClass) => LastPrefix + engineClass + GhostFile.Extension;
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

        /// <summary>Export file name: track, class, time, player and the start of the id.</summary>
        public static string ExportName(GhostHeader h) =>
            Safe($"{h.trackId}_{h.engineClassLabel}_{FileTime(h.finishTime)}_{h.playerName}_{h.id.Substring(0, Math.Min(ExportIdLength, h.id.Length))}") + GhostFile.Extension;

        /// <summary><paramref name="name"/> with spaces and characters not allowed in file names replaced by '_'.</summary>
        public static string Safe(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (name ?? "").Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
            return chars.Length > 0 ? new string(chars) : "_";
        }

        static string FileTime(float seconds)
        {
            var m = (int)(seconds / 60f);
            var s = seconds - m * 60;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}m{1:00.00}s", m, s).Replace('.', '-');
        }
    }
}
