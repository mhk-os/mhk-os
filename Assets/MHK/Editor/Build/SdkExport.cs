using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MHK.Core;
using MHK.Editor.Modding;
using MHK.Editor.Sdk;
using MHK.Modding;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Build
{
    /// <summary>
    /// Game project only: refreshes what the SDK carries for mod projects. The project settings kit (copies of the game's
    /// settings), the core index (ids and GUIDs of the game's and the base content's definitions) and the core references
    /// package (org.mhkose.sdk.corerefs: one stub per base content definition with the real asset's GUID and fileID).
    /// </summary>
    public static class SdkExport
    {
        public const string CoreRefsPackage = "org.mhkose.sdk.corerefs";
        /// <summary>Folder of the generated core references package inside the SDK (not imported: the stubs share GUIDs with the game's assets).</summary>
        public const string CoreRefsFolder = "CoreRefs~/" + CoreRefsPackage;
        const string MenuRoot = "MHK/SDK/";
        const long MainObjectFileId = 11400000;

        [Serializable]
        class PackageManifest
        {
            public string name;
        }

        [MenuItem(MenuRoot + "Update Project Settings Kit", priority = 20)]
        static void UpdateKitMenu() => UpdateProjectSettingsKit();

        [MenuItem(MenuRoot + "Export Core References...", priority = 21)]
        static void ExportMenu()
        {
            var folder = EditorUtility.SaveFolderPanel("Core references package", Path.GetDirectoryName(DefaultCoreRefsFolder), CoreRefsPackage);
            if (!string.IsNullOrEmpty(folder) && ExportCoreReferences(folder)) EditorUtility.RevealInFinder(folder);
        }

        [MenuItem(MenuRoot + "Update Project Settings Kit", true)]
        [MenuItem(MenuRoot + "Export Core References...", true)]
        static bool GameOnly() => SdkPaths.IsGameProject;

        /// <summary>The core references package in the SDK (CoreRefs~/org.mhkose.sdk.corerefs).</summary>
        public static string DefaultCoreRefsFolder => Path.Combine(SdkPaths.ResolvedRoot, CoreRefsFolder);

        /// <summary>Copies the game's settings files, the kit's player settings and Assets/Settings (with metas) into the SDK's project settings kit.</summary>
        public static void UpdateProjectSettingsKit()
        {
            AssetDatabase.SaveAssets();
            var kit = ProjectSettingsKit.KitRoot;
            var settings = Path.Combine(kit, "ProjectSettings");
            Directory.CreateDirectory(settings);
            foreach (var file in ProjectSettingsKit.ProjectSettingsFiles)
                File.Copy(Path.Combine("ProjectSettings", file), Path.Combine(settings, file), true);
            File.WriteAllText(Path.Combine(kit, ProjectSettingsKit.PlayerSettingsFile), ProjectSettingsKit.ReadPlayerSettings().ToString(Formatting.Indented) + "\n");
            var assets = Path.Combine(kit, ProjectSettingsKit.SettingsAssetFolder);
            if (Directory.Exists(assets)) Directory.Delete(assets, true);
            Directory.CreateDirectory(assets);
            var copied = 0;
            foreach (var file in Directory.GetFiles(ProjectSettingsKit.SettingsAssetFolder).Where(f => !Path.GetFileName(f).StartsWith(".")))
            {
                File.Copy(file, Path.Combine(assets, Path.GetFileName(file)));
                copied++;
            }
            Debug.Log($"[SDK] Project settings kit updated in {kit}: {ProjectSettingsKit.ProjectSettingsFiles.Length} settings files, " +
                      $"{ProjectSettingsKit.PlayerSettingsKeys.Length} player settings, {copied} files of {ProjectSettingsKit.SettingsAssetFolder}.");
        }

        /// <summary>
        /// Writes the core references package into <paramref name="folder"/> (replacing an earlier export) and the SDK's
        /// core index. Refuses folders Unity imports into this project, where the stubs would clash with the real assets.
        /// </summary>
        public static bool ExportCoreReferences(string folder)
        {
            folder = Path.GetFullPath(folder);
            if (!PrepareFolder(folder)) return false;
            var packs = CorePacks();
            var entries = packs.SelectMany(p => p.pack.content.Where(d => d != null).Select(d => (p.pack, def: d, p.builtin))).ToList();
            var stubs = entries.Where(e => !e.builtin).ToList();
            var written = stubs.Count(e => WriteStub(folder, e.pack, e.def));
            WriteFolderMetas(folder);
            WritePackageJson(folder);
            WriteCoreIndex(packs, entries);
            Debug.Log($"[SDK] Core references: {written} stubs of {stubs.Count} base content definitions in {folder}, core index {SdkPaths.CoreIndex} " +
                      $"({entries.Count} definitions in {packs.Count} packs).");
            return written == stubs.Count;
        }

        static bool PrepareFolder(string folder)
        {
            var project = Path.GetFullPath(".") + Path.DirectorySeparatorChar;
            var imported = folder.StartsWith(project + "Assets") || folder.StartsWith(project + "Packages");
            if (imported && !folder.Split(Path.DirectorySeparatorChar).Any(s => s.EndsWith("~")))
            {
                Debug.LogError($"[SDK] {folder} is imported by this project: the stubs would clash with the real definitions. Pick a folder outside " +
                               "Assets and Packages or inside a folder whose name ends with '~'.");
                return false;
            }
            var manifest = Path.Combine(folder, "package.json");
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                if (!File.Exists(manifest) || JsonUtility.FromJson<PackageManifest>(File.ReadAllText(manifest)).name != CoreRefsPackage)
                {
                    Debug.LogError($"[SDK] {folder} is not empty and holds no {CoreRefsPackage} package: nothing written.");
                    return false;
                }
                Directory.Delete(folder, true);
            }
            Directory.CreateDirectory(folder);
            return true;
        }

        // The game's own packs (builtin) and the shipped base content, each with the id of its top-level pack.
        static List<(ContentPack pack, string root, bool builtin)> CorePacks()
        {
            var builtin = ContentPackAssets.TopLevel(ContentPackAssets.BuiltinPacks()).Select(r => (r, true));
            var shipped = ContentPackAssets.ShippedPacks().Select(r => (r, false));
            return builtin.Concat(shipped).SelectMany(x => x.r.SelfAndIncluded().Select(p => (p, x.r.packId, x.Item2))).ToList();
        }

        static bool WriteStub(string folder, ContentPack pack, ContentDefinition def)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(def, out var guid, out long fileId) || fileId != MainObjectFileId)
            {
                Debug.LogError($"[SDK] '{def.Id}' is not the main object of its asset file: no stub with the same GUID and fileID possible.", def);
                return false;
            }
            var name = AssetPaths.SafeFileName(def.Id);
            var dir = Path.Combine(folder, pack.packId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".asset");
            var stub = (ContentDefinition)ScriptableObject.CreateInstance(def.GetType());
            stub.name = name;
            using (var so = new SerializedObject(stub))
            {
                so.FindProperty("id").stringValue = def.Id;
                so.FindProperty("displayName").stringValue = def.DisplayName;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            InternalEditorUtility.SaveToSerializedFileAndForget(new Object[] { stub }, path, true);
            Object.DestroyImmediate(stub);
            // The serializer numbers the object 1, references to the real definition use the main object id.
            var yaml = File.ReadAllText(path);
            var header = yaml.IndexOf(" &1\n", StringComparison.Ordinal);
            if (header < 0) throw new IOException($"Unexpected stub layout in {path}");
            File.WriteAllText(path, yaml.Substring(0, header) + $" &{MainObjectFileId}\n" + yaml.Substring(header + 4));
            File.Copy(AssetDatabase.GUIDToAssetPath(guid) + ".meta", path + ".meta", true);
            return true;
        }

        static void WriteFolderMetas(string folder)
        {
            foreach (var dir in Directory.GetDirectories(folder))
                File.WriteAllText(dir + ".meta", Meta(folder, dir, "folderAsset: yes\nDefaultImporter"));
        }

        static void WritePackageJson(string folder)
        {
            var json = "{\n" +
                       $"  \"name\": \"{CoreRefsPackage}\",\n" +
                       $"  \"version\": \"{BuildInfo.Version}\",\n" +
                       "  \"displayName\": \"Moorhuhn Kart Open Source SDK Core References\",\n" +
                       "  \"description\": \"Stand-ins for the base content definitions with the GUIDs of the game's assets, so a mod can refer to core " +
                       "karts, tracks, characters, cups, powerups and effects. The game resolves them by id when the mod loads. Generated by the game " +
                       "project (MHK > SDK > Export Core References), never installed in it.\",\n" +
                       "  \"unity\": \"6000.6\"\n" +
                       "}\n";
            var path = Path.Combine(folder, "package.json");
            File.WriteAllText(path, json);
            File.WriteAllText(path + ".meta", Meta(folder, path, "PackageManifestImporter"));
        }

        // Metas of generated folders and files get a GUID derived from their path, so every export writes the same one.
        static string Meta(string root, string path, string importer)
        {
            var key = CoreRefsPackage + "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
            using var md5 = MD5.Create();
            var guid = string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(key)).Select(b => b.ToString("x2")));
            return $"fileFormatVersion: 2\nguid: {guid}\n{importer}:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
        }

        static void WriteCoreIndex(List<(ContentPack pack, string root, bool builtin)> packs, List<(ContentPack pack, ContentDefinition def, bool builtin)> entries)
        {
            var index = new CoreIndex();
            foreach (var (pack, root, builtin) in packs)
                index.packs.Add(new CoreIndex.Pack { packId = pack.packId, root = root, builtin = builtin });
            foreach (var (pack, def, _) in entries)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(def, out var guid, out long fileId);
                index.definitions.Add(new CoreIndex.Definition
                {
                    id = def.Id, type = def.GetType().FullName, pack = pack.packId, guid = guid, fileId = fileId, displayName = def.DisplayName,
                });
            }
            Directory.CreateDirectory(AssetPaths.DirectoryOf(SdkPaths.CoreIndex));
            File.WriteAllText(SdkPaths.CoreIndex, JsonUtility.ToJson(index, true) + "\n");
            AssetDatabase.ImportAsset(SdkPaths.CoreIndex);
        }
    }
}
