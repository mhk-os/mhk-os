using System;
using System.IO;
using System.Linq;
using MHK.Editor.Content;
using MHK.Modding;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// <b>MHK ▸ Modding ▸ Import Export Folder…</b>: imports a converter export (work/EXPORT_CONTRACT.md) into a mod pack.
    /// Scans tracks/, characters/ and karts/ and hands each folder to <see cref="TrackImporter"/> or
    /// <see cref="ExportCharacterImporter"/>. Run it with an <see cref="ExportImportSettings"/> asset selected to re-import.
    /// </summary>
    public static class ExportFolderImporter
    {
        const string MenuPath = "MHK/Modding/Import Export Folder…";

        [MenuItem(MenuPath, priority = 10)]
        static void ImportMenu()
        {
            if (Selection.activeObject is ExportImportSettings existing)
            {
                Import(existing);
                return;
            }
            var folder = EditorUtility.OpenFolderPanel("Export folder (tracks/, characters/, karts/)", "", "");
            if (string.IsNullOrEmpty(folder)) return;
            var pack = Selection.activeObject as ContentPack;
            if (pack == null) pack = LoadOrCreatePack(folder);
            if (pack == null) return;
            var settings = LoadOrCreateSettings(pack, folder, out var settingsPath);
            Selection.activeObject = settings;
            if (EditorUtility.DisplayDialog("Import export folder", $"Import {folder} into '{pack.packId}' now?\n\nThe settings are in " +
                                            $"{settingsPath}; adjust them and run the menu again with them selected to re-import.", "Import", "Later"))
                Import(settings);
        }

        static ContentPack LoadOrCreatePack(string folder)
        {
            var path = EditorUtility.SaveFilePanelInProject("Mod pack to import into", Path.GetFileName(folder), "asset",
                "Content pack (an existing one is reused)", "Assets/Mods");
            if (string.IsNullOrEmpty(path)) return null;
            var pack = AssetDatabase.LoadAssetAtPath<ContentPack>(path);
            return pack != null ? pack : ContentPackAssets.Create(path, AssetPaths.SafeFileName(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()));
        }

        static ExportImportSettings LoadOrCreateSettings(ContentPack pack, string folder, out string path)
        {
            path = $"{ImportAssets.PackFolder(pack)}/{AssetPaths.SafeFileName(pack.packId)}_import.asset";
            var settings = AssetPaths.LoadOrCreate<ExportImportSettings>(path);
            settings.exportFolder = folder;
            settings.pack = pack;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return settings;
        }

        /// <summary>Imports everything <paramref name="s"/> selects into its pack.</summary>
        public static void Import(ExportImportSettings s)
        {
            if (s.pack == null || !Directory.Exists(s.exportFolder))
            {
                Debug.LogError($"[Import] Settings {AssetDatabase.GetAssetPath(s)}: pack missing or export folder '{s.exportFolder}' not found.", s);
                return;
            }
            s.ApplyDefaultAssets();
            var errors = 0;
            if (s.karts) errors += ImportEach(s, "karts", "kart.json", "kart", d => ExportCharacterImporter.ImportKart(d, s));
            if (s.characters) errors += ImportEach(s, "characters", "character.json", "character", d => ExportCharacterImporter.ImportCharacter(d, s));
            if (s.tracks) errors += ImportEach(s, "tracks", "track.json", "track", d => TrackImporter.Import(d, s));
            AssetDatabase.SaveAssets();
            if (s.optimizeAfterImport) OptimizePack(s.pack);
            Debug.Log($"[Import] {s.exportFolder} → '{s.pack.packId}': {s.pack.content.Count} definitions" + (errors > 0 ? $", {errors} failed (see errors above)." : "."), s.pack);
        }

        static void OptimizePack(ContentPack pack)
        {
            var folder = ImportAssets.PackFolder(pack);
            Debug.Log($"[Import] '{pack.packId}' duplicates: {AssetDeduplicator.Deduplicate(folder)}");
            Debug.Log($"[Import] '{pack.packId}' model libraries: {ModelLibraryOptimizer.OptimizeLibraries(EditorAssets.FindAll<ModelLibrary>(folder).ToList())}");
        }

        static int ImportEach(ExportImportSettings s, string sub, string file, string label, Action<string> import)
        {
            var root = Path.Combine(s.exportFolder, sub);
            if (!Directory.Exists(root)) return 0;
            var dirs = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToArray();
            var errors = 0;
            using var progress = new EditorProgressScope($"Importing {label}", false);
            for (var i = 0; i < dirs.Length; i++)
            {
                if (!File.Exists(Path.Combine(dirs[i], file)) || !MatchesOnlyId(s.onlyId, dirs[i], file)) continue;
                progress.Step(i, dirs.Length, Path.GetFileName(dirs[i]));
                try { import(dirs[i]); }
                catch (Exception e)
                {
                    errors++;
                    Debug.LogError($"[Import] {label} {dirs[i]}: {e}");
                }
            }
            return errors;
        }

        static bool MatchesOnlyId(string onlyId, string dir, string file) =>
            string.IsNullOrEmpty(onlyId) || Path.GetFileName(dir) == onlyId ||
            ExportJson.Str(JObject.Parse(File.ReadAllText(Path.Combine(dir, file)))["id"]) == onlyId;
    }
}
