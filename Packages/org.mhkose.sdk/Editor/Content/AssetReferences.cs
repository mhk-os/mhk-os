using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Object references in the project's text-serialized assets (scenes, prefabs, materials, ScriptableObjects):
    /// finds them and rewrites them without loading the files.
    /// </summary>
    public static class AssetReferences
    {
        static readonly string[] TextExtensions = { ".unity", ".prefab", ".mat", ".asset", ".controller", ".anim", ".lighting" };
        static readonly Regex ReferencePattern = new(@"\{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: (\d+)\}", RegexOptions.Compiled);
        static readonly Regex DocumentHeader = new(@"^--- !u!(\d+) &", RegexOptions.Compiled | RegexOptions.Multiline);
        const string YamlHeader = "%YAML";

        /// <summary>Unity class ids whose mesh references need the mesh's CPU copy at runtime.</summary>
        static readonly HashSet<int> CpuMeshUsers = new()
        {
            64,  // MeshCollider
            137, // SkinnedMeshRenderer (non-readable meshes take another skinning path with slightly different vertices)
            198, // ParticleSystem (shape module)
            199, // ParticleSystemRenderer (mesh particles)
        };

        /// <summary>An object as text assets reference it: asset GUID plus local file id.</summary>
        public readonly struct Ref : IEquatable<Ref>
        {
            public readonly string guid;
            public readonly long fileId;

            public Ref(string guid, long fileId)
            {
                this.guid = guid;
                this.fileId = fileId;
            }

            /// <summary>Reference of a persistent object, false for scene or transient objects.</summary>
            public static bool TryOf(Object obj, out Ref reference)
            {
                reference = default;
                if (obj == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out var guid, out long fileId)) return false;
                reference = new Ref(guid, fileId);
                return true;
            }

            public bool Equals(Ref other) => fileId == other.fileId && guid == other.guid;
            public override bool Equals(object obj) => obj is Ref other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(guid, fileId);
            public override string ToString() => $"{guid}:{fileId}";
        }

        /// <summary>Paths of the text YAML assets under <paramref name="root"/> (binary assets are skipped).</summary>
        public static IEnumerable<string> TextAssetPaths(string root = "Assets") =>
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => f.Replace('\\', '/'))
                .Where(IsYaml);

        /// <summary>
        /// Replaces every reference listed in <paramref name="map"/> by its value in the text assets under
        /// <paramref name="root"/>, then reimports the changed files. Refuses (returns false, changes nothing) when a
        /// changed file is a scene that is open in the editor. <paramref name="changed"/> lists the rewritten files.
        /// </summary>
        public static bool Repoint(IReadOnlyDictionary<Ref, Ref> map, out List<string> changed, string root = "Assets")
        {
            changed = new List<string>();
            if (map.Count == 0) return true;
            var guids = new HashSet<string>(map.Keys.Select(k => k.guid));
            var rewrites = new List<(string path, string text)>();
            foreach (var path in TextAssetPaths(root))
            {
                var text = File.ReadAllText(path);
                if (!guids.Any(text.Contains)) continue;
                var replaced = ReferencePattern.Replace(text, m =>
                    map.TryGetValue(new Ref(m.Groups[2].Value, long.Parse(m.Groups[1].Value)), out var to)
                        ? $"{{fileID: {to.fileId}, guid: {to.guid}, type: {m.Groups[3].Value}}}"
                        : m.Value);
                if (replaced != text) rewrites.Add((path, replaced));
            }
            if (rewrites.Any(r => IsOpenScene(r.path))) return false;
            AssetDatabase.SaveAssets();
            foreach (var (path, text) in rewrites)
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
                changed.Add(path);
            }
            foreach (var path in changed) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return true;
        }

        /// <summary>
        /// Meshes referenced by components that need mesh data on the CPU at runtime (MeshCollider, SkinnedMeshRenderer,
        /// ParticleSystem, ParticleSystemRenderer) in the scenes and prefabs under <paramref name="root"/>. Prefab overrides of a
        /// "m_Mesh" property count too.
        /// </summary>
        public static HashSet<Ref> CpuMeshReferences(string root = "Assets")
        {
            var result = new HashSet<Ref>();
            foreach (var path in TextAssetPaths(root).Where(p => p.EndsWith(".unity") || p.EndsWith(".prefab")))
            {
                var text = File.ReadAllText(path);
                var headers = DocumentHeader.Matches(text);
                for (var i = 0; i < headers.Count; i++)
                {
                    var start = headers[i].Index;
                    var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
                    var classId = int.Parse(headers[i].Groups[1].Value);
                    var body = text.Substring(start, end - start);
                    if (CpuMeshUsers.Contains(classId)) AddMeshReferences(body, result);
                    else if (classId == 1001) AddOverriddenMeshes(body, result);
                }
            }
            return result;
        }

        static void AddMeshReferences(string document, HashSet<Ref> result)
        {
            foreach (var line in document.Split('\n'))
            {
                if (!line.Contains("m_Mesh")) continue;
                var m = ReferencePattern.Match(line);
                if (m.Success) result.Add(new Ref(m.Groups[2].Value, long.Parse(m.Groups[1].Value)));
            }
        }

        static void AddOverriddenMeshes(string prefabInstance, HashSet<Ref> result)
        {
            var lines = prefabInstance.Split('\n');
            for (var i = 0; i + 2 < lines.Length; i++)
            {
                if (!lines[i].TrimEnd().EndsWith("propertyPath: m_Mesh")) continue;
                var m = ReferencePattern.Match(lines[i + 2]);
                if (m.Success) result.Add(new Ref(m.Groups[2].Value, long.Parse(m.Groups[1].Value)));
            }
        }

        static bool IsYaml(string path)
        {
            using var stream = File.OpenRead(path);
            var head = new byte[YamlHeader.Length];
            return stream.Read(head, 0, head.Length) == head.Length && Encoding.ASCII.GetString(head) == YamlHeader;
        }

        static bool IsOpenScene(string path)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).path == path) return true;
            return false;
        }
    }
}
