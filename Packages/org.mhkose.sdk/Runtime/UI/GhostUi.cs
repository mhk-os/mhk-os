using System;
using System.IO;
using MHK.Ghosts;
using UnityEngine;
using Application = UnityEngine.Device.Application;

namespace MHK.UI
{
    /// <summary>Ghost actions shared by the results screen and the Records page (export, import, folder reveal).</summary>
    public static class GhostUi
    {
        /// <summary>
        /// Exports <paramref name="entry"/> with the game's store and, on desktop, opens the export folder. Returns the
        /// written location, or null (logged) when it failed.
        /// </summary>
        public static string Export(GhostEntry entry)
        {
            if (entry == null) return null;
            try
            {
                var path = GhostStores.Current.Export(entry);
                Reveal(Path.GetDirectoryName(path));
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ghosts] Export failed.");
                Debug.LogException(e);
                return null;
            }
        }

        /// <summary>Exports <paramref name="entry"/> and returns the status text: <paramref name="exportedFormat"/> ({0} = file) or <paramref name="failedText"/>.</summary>
        public static string ExportStatus(GhostEntry entry, string exportedFormat, string failedText)
        {
            var path = Export(entry);
            return path != null ? string.Format(exportedFormat, path) : failedText;
        }

        /// <summary>Opens <paramref name="folder"/> in the file manager (desktop only).</summary>
        public static void Reveal(string folder)
        {
            if (Application.isMobilePlatform || string.IsNullOrEmpty(folder) || Application.isBatchMode) return;
            Directory.CreateDirectory(folder);
            Application.OpenURL(new Uri(folder).AbsoluteUri);
        }

        /// <summary>The local store's import folder (where players drop .mhkghost files), null for other stores.</summary>
        public static string ImportFolder => GhostStores.Current is LocalGhostStore local ? local.ImportFolder : null;
    }
}
