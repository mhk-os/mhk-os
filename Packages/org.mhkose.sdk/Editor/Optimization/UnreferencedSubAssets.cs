using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Optimization
{
    /// <summary>Removes chunked source meshes from their model libraries once no scene or prefab file references them.</summary>
    internal static class UnreferencedSubAssets
    {
        /// <summary>Checks and removes the source meshes of <paramref name="reports"/>, updating each entry's removal state and note.</summary>
        public static void RemoveSourceMeshes(IEnumerable<MeshChunkReport> reports)
        {
            var entries = reports.SelectMany(r => r.chunked).Where(e => !e.sourceMeshRemoved && e.sourceMesh != null).ToList();
            if (entries.Count == 0) return;
            var files = ReadSceneAndPrefabTexts();
            var libraries = new HashSet<string>();
            using (new AssetDatabase.AssetEditingScope())
            {
                foreach (var group in entries.GroupBy(e => e.sourceMesh))
                {
                    var removedFrom = TryRemoveSourceMesh(group.Key, files, out var note);
                    if (removedFrom != null)
                    {
                        libraries.Add(removedFrom);
                        foreach (var e in group) e.sourceMeshRemoved = true;
                    }
                    foreach (var e in group) e.sourceMeshNote = note;
                }
            }
            SaveLibraries(libraries);
        }

        static List<(string path, string text)> ReadSceneAndPrefabTexts() =>
            new[] { "Assets", SdkPaths.Root }.Where(Directory.Exists)
                .SelectMany(root => Directory.GetFiles(root, "*.unity", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(root, "*.prefab", SearchOption.AllDirectories)))
                .Select(f => (path: f.Replace('\\', '/'), text: File.ReadAllText(f)))
                .ToList();

        static string TryRemoveSourceMesh(Mesh mesh, List<(string path, string text)> files, out string note)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out var guid, out long localId))
            {
                note = "kept: not a persistent asset";
                return null;
            }
            // Binary files cannot be searched and count as references.
            var key = $"fileID: {localId}, guid: {guid}";
            var refs = files.Where(f => !f.text.StartsWith("%YAML") || f.text.Contains(key)).Select(f => f.path).ToList();
            if (refs.Count > 0)
            {
                note = "kept: referenced by " + string.Join(", ", refs.Take(3)) + (refs.Count > 3 ? $" and {refs.Count - 3} more" : "");
                return null;
            }
            var path = AssetDatabase.GetAssetPath(mesh);
            AssetDatabase.RemoveObjectFromAsset(mesh);
            Object.DestroyImmediate(mesh, true);
            note = "removed from " + path;
            return path;
        }

        static void SaveLibraries(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                var library = AssetDatabase.LoadMainAssetAtPath(path);
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssetIfDirty(library);
            }
        }
    }
}
