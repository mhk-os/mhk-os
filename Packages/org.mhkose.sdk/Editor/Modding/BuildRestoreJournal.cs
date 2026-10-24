using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Files a build changes for its duration (import settings, stamped packs, temporary assets): their bytes are saved
    /// to Library before the change and written back on dispose, so the project ends byte-identical. Journals left by a
    /// crashed or interrupted editor are restored when the next domain loads.
    /// </summary>
    [InitializeOnLoad]
    public sealed class BuildRestoreJournal : IDisposable
    {
        const string Root = "Library/MHKBuildRestore";
        const string JournalFile = "journal.tsv";

        readonly string folder;
        readonly HashSet<string> known = new(StringComparer.Ordinal);
        int count;

        static BuildRestoreJournal() => EditorApplication.delayCall += RestoreLeftovers;

        public BuildRestoreJournal()
        {
            folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        /// <summary>
        /// Saves the current bytes of <paramref name="file"/> (a project-relative path) once. A file or folder that does
        /// not exist yet is deleted on restore.
        /// </summary>
        public void Save(string file)
        {
            if (!known.Add(file)) return;
            var backup = "";
            if (File.Exists(file))
            {
                backup = Path.Combine(folder, count++ + ".bin");
                File.Copy(file, backup);
            }
            File.AppendAllText(Path.Combine(folder, JournalFile), file + "\t" + backup + "\n");
        }

        /// <summary>Writes every saved file back, deletes the created ones and reimports what changed.</summary>
        public void Dispose() => Restore(folder);

        static void RestoreLeftovers()
        {
            if (BuildPipeline.isBuildingPlayer || !Directory.Exists(Root)) return;
            foreach (var leftover in Directory.GetDirectories(Root))
            {
                Debug.LogWarning($"[Build] Restoring the files an interrupted build changed ({leftover}).");
                Restore(leftover);
            }
        }

        static void Restore(string folder)
        {
            var journal = Path.Combine(folder, JournalFile);
            var lines = File.Exists(journal) ? File.ReadAllLines(journal) : Array.Empty<string>();
            var reimport = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    var parts = lines[i].Split('\t');
                    if (parts.Length == 2) RestoreFile(parts[0], parts[1], reimport);
                }
            }
            finally
            {
                foreach (var path in reimport) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.StopAssetEditing();
            }
            Directory.Delete(folder, true);
        }

        static void RestoreFile(string file, string backup, List<string> reimport)
        {
            var assetPath = file.EndsWith(".meta", StringComparison.Ordinal) ? file.Substring(0, file.Length - ".meta".Length) : file;
            if (backup.Length == 0)
            {
                if (!AssetDatabase.DeleteAsset(file))
                {
                    if (File.Exists(file)) File.Delete(file);
                    else if (Directory.Exists(file)) Directory.Delete(file, true);
                }
                return;
            }
            var bytes = File.ReadAllBytes(backup);
            if (File.Exists(file) && BytesEqual(File.ReadAllBytes(file), bytes)) return;
            File.WriteAllBytes(file, bytes);
            reimport.Add(assetPath);
        }

        static bool BytesEqual(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    }
}
