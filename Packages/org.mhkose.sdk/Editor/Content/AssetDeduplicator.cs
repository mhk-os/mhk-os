using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MHK.Modding;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Merges duplicate assets inside one content pack folder: textures and audio clips with identical bytes and import
    /// settings, then materials that render the same (<see cref="MaterialKey"/>). References are repointed to the
    /// survivor, which keeps its GUID and moves to "&lt;pack&gt;/Shared/&lt;kind&gt;" when the copies lived in different folders.
    /// </summary>
    public static class AssetDeduplicator
    {
        /// <summary>Asset kinds <see cref="Deduplicate"/> merges.</summary>
        [Flags]
        public enum Kinds
        {
            Textures = 1,
            Audio = 2,
            Materials = 4,
            All = Textures | Audio | Materials,
        }

        /// <summary>Outcome of one run.</summary>
        public sealed class Result
        {
            public int groups, removed;
            public long bytesRemoved;
            public readonly List<string> changedFiles = new();
            public string error;

            public override string ToString() => error ?? $"{groups} groups, {removed} duplicates removed ({bytesRemoved / 1048576f:F1} MB), {changedFiles.Count} files repointed";
        }

        const string SharedFolder = "Shared";
        const string LightingDataFile = "LightingData.asset";
        static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".exr" };
        static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3", ".aif", ".aiff" };
        static readonly string[] MaterialExtensions = { ".mat" };
        static readonly Regex GuidLine = new(@"^guid: [0-9a-f]{32}\r?\n", RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex NameLine = new(@"^  m_Name: .*\r?\n", RegexOptions.Multiline | RegexOptions.Compiled);

        [MenuItem("MHK/Content/Deduplicate Selected Pack Assets")]
        static void DeduplicateSelected()
        {
            foreach (var pack in Selection.objects.OfType<ContentPack>())
                Debug.Log($"[Dedupe] {pack.packId}: {Deduplicate(AssetPaths.DirectoryOf(pack))}");
        }

        [MenuItem("MHK/Content/Deduplicate Selected Pack Assets", true)]
        static bool DeduplicateSelectedValidate() => Selection.objects.OfType<ContentPack>().Any();

        /// <summary>
        /// Merges the duplicate <paramref name="kinds"/> under <paramref name="packFolder"/> ("Assets/Mods/TINcan").
        /// Textures and audio go first, so materials that only differed by a texture copy merge too. With
        /// <paramref name="only"/> (asset paths with forward slashes, Unicode NFC: what an import added) only those copies
        /// go: another file of the group survives where it is when it sits in a copy's folder or
        /// <paramref name="keepElsewhere"/> accepts it (null: any), other files are left alone.
        /// </summary>
        public static Result Deduplicate(string packFolder, Kinds kinds = Kinds.All, IReadOnlyCollection<string> only = null,
                                         Func<string, bool> keepElsewhere = null)
        {
            var result = new Result();
            Func<List<string>, List<string>> scope = only == null ? g => g : g => Restrict(g, only, keepElsewhere);
            if (kinds.HasFlag(Kinds.Textures) && !Merge(packFolder, FileGroups(packFolder, TextureExtensions).Select(scope), "Textures", only, result)) return result;
            if (kinds.HasFlag(Kinds.Audio) && !Merge(packFolder, FileGroups(packFolder, AudioExtensions).Select(scope), "Audio", only, result)) return result;
            if (kinds.HasFlag(Kinds.Materials)) Merge(packFolder, MaterialGroups(packFolder).Select(scope), "Materials", only, result);
            return result;
        }

        /// <summary>
        /// Identity of what a material renders: shader, render state, keywords and property values (a property it does
        /// not store reads as the shader's default), not its name. Material variants and materials without a shader: their
        /// serialized text without the name.
        /// </summary>
        public static string MaterialKey(Material m)
        {
            var shader = m.shader;
            if (shader == null || m.parent != null) return Digest(NameLine.Replace(File.ReadAllText(AssetDatabase.GetAssetPath(m)), ""));
            var key = new StringBuilder();
            key.Append(ObjectKey(shader)).Append('|').Append(m.renderQueue).Append('|').Append(m.enableInstancing).Append('|')
                .Append(m.doubleSidedGI).Append('|').Append((int)m.globalIlluminationFlags).Append('|').Append(m.GetTag("RenderType", false))
                .Append('|').Append(string.Join(",", m.shaderKeywords.OrderBy(k => k, StringComparer.Ordinal)));
            for (var i = 0; i < m.passCount; i++)
                if (!m.GetShaderPassEnabled(m.GetPassName(i))) key.Append("|-").Append(m.GetPassName(i));
            for (var i = 0; i < shader.GetPropertyCount(); i++)
            {
                var id = shader.GetPropertyNameId(i);
                key.Append('|');
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: AppendVector(key, m.GetColor(id)); break;
                    case ShaderPropertyType.Vector: AppendVector(key, m.GetVector(id)); break;
                    case ShaderPropertyType.Float or ShaderPropertyType.Range: AppendFloat(key, m.GetFloat(id)); break;
                    case ShaderPropertyType.Int: key.Append(m.GetInteger(id)); break;
                    case ShaderPropertyType.Texture:
                        key.Append(ObjectKey(m.GetTexture(id)));
                        AppendVector(key, m.GetTextureScale(id));
                        AppendVector(key, m.GetTextureOffset(id));
                        break;
                }
            }
            return key.ToString();
        }

        static string ObjectKey(UnityEngine.Object o) =>
            o == null ? "-" : AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out var guid, out long fileId) ? $"{guid}:{fileId}" : o.GetEntityId().ToString();

        static void AppendVector(StringBuilder key, Vector4 v)
        {
            for (var i = 0; i < 4; i++) AppendFloat(key, v[i]);
        }

        static void AppendFloat(StringBuilder key, float f) => key.Append(f.ToString("R", CultureInfo.InvariantCulture)).Append(',');

        static IEnumerable<List<string>> FileGroups(string folder, string[] extensions) =>
            AssetFiles(folder, extensions)
                .GroupBy(p => Digest(File.ReadAllBytes(p)) + Digest(GuidLine.Replace(File.ReadAllText(p + ".meta"), "")))
                .Select(g => g.ToList())
                .Where(g => g.Count > 1);

        static IEnumerable<List<string>> MaterialGroups(string folder) =>
            AssetFiles(folder, MaterialExtensions)
                .GroupBy(p => AssetDatabase.LoadAssetAtPath<Material>(p) is var m && m != null ? MaterialKey(m) : Digest(NameLine.Replace(File.ReadAllText(p), "")))
                .Select(g => g.ToList())
                .Where(g => g.Count > 1);

        static IEnumerable<string> AssetFiles(string folder, string[] extensions) =>
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => f.Replace('\\', '/').Normalize(System.Text.NormalizationForm.FormC))
                .Where(f => !IsBakedLighting(f))
                .OrderBy(f => f, StringComparer.Ordinal);

        // Lightmaps and shadowmasks sit next to their scene's LightingData, which references them in binary.
        static bool IsBakedLighting(string path) =>
            File.Exists(AssetPaths.DirectoryOf(path) + "/" + LightingDataFile) ||
            AssetImporter.GetAtPath(path) is TextureImporter { textureType: TextureImporterType.Lightmap or TextureImporterType.Shadowmask };

        // The added copies of a group behind the file that survives: one that was there before (in a copy's folder or
        // accepted by keepElsewhere), else the first copy. Null when nothing goes.
        static List<string> Restrict(List<string> group, IReadOnlyCollection<string> only, Func<string, bool> keepElsewhere)
        {
            var copies = group.Where(only.Contains).ToList();
            var folders = copies.Select(AssetPaths.DirectoryOf).ToHashSet();
            var keep = group.FirstOrDefault(p => !only.Contains(p) && (folders.Contains(AssetPaths.DirectoryOf(p)) || keepElsewhere == null || keepElsewhere(p)))
                       ?? copies.FirstOrDefault();
            copies.Remove(keep);
            return copies.Count > 0 ? copies.Prepend(keep).ToList() : null;
        }

        static bool Merge(string packFolder, IEnumerable<List<string>> groups, string kind, IReadOnlyCollection<string> only, Result result)
        {
            var map = new Dictionary<AssetReferences.Ref, AssetReferences.Ref>();
            var plan = new List<List<string>>();
            foreach (var group in groups)
            {
                if (group == null || !AssetReferences.Ref.TryOf(AssetDatabase.LoadMainAssetAtPath(group[0]), out var keep)) continue;
                foreach (var copy in group.Skip(1))
                    if (AssetReferences.Ref.TryOf(AssetDatabase.LoadMainAssetAtPath(copy), out var dup)) map[dup] = keep;
                plan.Add(group);
            }
            if (plan.Count == 0) return true;
            // References to pack assets only live in the pack (mods are self-contained), which may sit in Packages/.
            if (!AssetReferences.Repoint(map, out var changed, packFolder))
            {
                result.error = $"{kind}: a scene that references duplicates is open in the editor; close it and run again";
                return false;
            }
            result.changedFiles.AddRange(changed);
            var failed = new List<string>();
            foreach (var group in plan)
            {
                result.groups++;
                result.removed += group.Count - 1;
                result.bytesRemoved += group.Skip(1).Sum(p => new FileInfo(p).Length);
                AssetDatabase.DeleteAssets(group.Skip(1).ToArray(), failed);
                // A file that was there before stays where the mod keeps it.
                if (only == null || only.Contains(group[0])) MoveToShared(packFolder, group, kind);
            }
            if (failed.Count > 0) Debug.LogWarning("[Dedupe] could not delete: " + string.Join(", ", failed));
            return true;
        }

        static void MoveToShared(string packFolder, List<string> group, string kind)
        {
            var survivor = group[0];
            if (group.All(p => AssetPaths.DirectoryOf(p) == AssetPaths.DirectoryOf(survivor))) return;
            var folder = $"{packFolder}/{SharedFolder}/{kind}";
            if (AssetPaths.DirectoryOf(survivor) == folder) return;
            AssetPaths.EnsureFolder(folder);
            var target = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Path.GetFileName(survivor)}");
            var error = AssetDatabase.MoveAsset(survivor, target);
            if (!string.IsNullOrEmpty(error)) Debug.LogWarning($"[Dedupe] {survivor}: {error}");
        }

        static string Digest(byte[] bytes)
        {
            using var md5 = MD5.Create();
            return Convert.ToBase64String(md5.ComputeHash(bytes));
        }

        static string Digest(string text) => Digest(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
