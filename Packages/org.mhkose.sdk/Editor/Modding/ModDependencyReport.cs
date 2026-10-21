using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Lists the assets a mod pack pulls in from outside its own folder (copied into the bundle, definitions among them
    /// are resolved to the game's own by id when the mod loads) and warns about base content assets that are not
    /// definitions, which mod projects do not have.
    /// </summary>
    static class ModDependencyReport
    {
        const int MaxReportedFolders = 25;
        const float BytesPerKB = 1024f;

        /// <summary>Logs the foreign dependencies of <paramref name="pack"/> grouped by folder with their size on disk.</summary>
        public static void Log(ContentPack pack)
        {
            var packPath = AssetDatabase.GetAssetPath(pack);
            var folder = AssetPaths.DirectoryOf(packPath) + "/";
            var roots = new[] { packPath }.Concat(ModPackValidator.Scenes(pack)).ToArray();
            var foreign = AssetDatabase.GetDependencies(roots, true)
                .Where(p => !p.StartsWith(folder) && !p.EndsWith(".cs") && !p.EndsWith(".dll") && !p.EndsWith(".asmdef"))
                .ToList();
            if (foreign.Count == 0) return;
            var defs = foreign.Select(AssetDatabase.LoadMainAssetAtPath).OfType<ContentDefinition>().Select(d => d.Id).ToList();
            var groups = foreign.GroupBy(p => AssetPaths.DirectoryOf(p))
                .Select(g => (folder: g.Key, count: g.Count(), bytes: g.Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0L)))
                .OrderByDescending(g => g.bytes).ToList();
            var total = groups.Sum(g => g.bytes);
            var text = $"[Mods] '{pack.packId}' copies {foreign.Count} assets from outside {folder} into its bundles ({total / ModBundleBuilder.BytesPerMB:0.0} MB on disk)." +
                       (defs.Count > 0 ? $"\nDefinitions of other packs (resolved by id at load): {string.Join(", ", defs)}" : "") +
                       "\n" + string.Join("\n", groups.Take(MaxReportedFolders).Select(g => $"  {g.folder}: {g.count} ({g.bytes / BytesPerKB:0} KB)"));
            Debug.Log(text, pack);
            WarnCoreAssets(pack, roots, folder);
        }

        // Walks the references without entering foreign definitions: what those pull in is not the mod's own reference.
        static void WarnCoreAssets(ContentPack pack, string[] roots, string folder)
        {
            var shipped = ContentPackAssets.ShippedFolder + "/";
            var seen = new HashSet<string>();
            var open = new Stack<string>(roots);
            var core = new List<string>();
            while (open.Count > 0)
            {
                var path = open.Pop();
                if (!seen.Add(path) || path.EndsWith(".cs")) continue;
                var foreign = !path.StartsWith(folder);
                if (foreign && typeof(ContentDefinition).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path))) continue;
                if (foreign && path.StartsWith(shipped)) core.Add(path);
                foreach (var d in AssetDatabase.GetDependencies(path, false)) open.Push(d);
            }
            if (core.Count == 0) return;
            core.Sort(StringComparer.Ordinal);
            Debug.LogWarning($"[Mods] '{pack.packId}' refers to {core.Count} base content assets that are not definitions. Mod projects only have " +
                             "stubs of the definitions (org.mhkose.sdk.corerefs): copy these into the mod or refer to them by id.\n  " +
                             string.Join("\n  ", core.Take(MaxReportedFolders)), pack);
        }
    }
}
