using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MHK.Editor.Content;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// One export import into a pack. Remembers the pack's files beforehand, so the importers reuse assets the mod
    /// already has (wherever it keeps them) instead of adding copies, and <see cref="Created"/> and <see cref="Written"/>
    /// limit the pack-wide passes after the import to what it added or wrote. Only files outside the folders the
    /// importers write to are reused: those can still be overwritten later in the same import.
    /// </summary>
    public sealed class PackImportScope : IDisposable
    {
        const string MetaExtension = ".meta";

        /// <summary>The open scope, null outside an export import.</summary>
        public static PackImportScope Current { get; private set; }

        readonly string folder;
        readonly IReadOnlyCollection<string> importerFolders;
        readonly Dictionary<string, (long length, DateTime time)> before;
        readonly Dictionary<long, List<string>> reusableBySize = new();
        Dictionary<string, List<Material>> reusableMaterials;
        readonly Dictionary<string, (string key, Material material)> claims = new(StringComparer.Ordinal);

        /// <param name="packFolder">Pack folder ("Packages/org.mhkose.mod.tincan").</param>
        /// <param name="importerFolders">Folders the importers write to (their files are not reused).</param>
        public PackImportScope(string packFolder, IReadOnlyCollection<string> importerFolders)
        {
            folder = packFolder;
            this.importerFolders = importerFolders;
            before = Stamps(packFolder);
            foreach (var (path, (length, _)) in before.Where(f => !f.Key.EndsWith(MetaExtension) && IsReusable(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                if (!reusableBySize.TryGetValue(length, out var list)) reusableBySize[length] = list = new List<string>();
                list.Add(path);
            }
            Current = this;
        }

        public void Dispose()
        {
            if (Current == this) Current = null;
        }

        /// <summary>The asset path the way <see cref="Written"/> lists it (forward slashes, Unicode NFC).</summary>
        public static string Key(string path) => path.Replace('\\', '/').Normalize(NormalizationForm.FormC);

        /// <summary>Whether a pack file lies outside the folders the importers write to.</summary>
        public bool IsReusable(string path) => !importerFolders.Any(f => path.StartsWith(f + "/", StringComparison.Ordinal));

        /// <summary>Asset paths (see <see cref="Key"/>) whose file or .meta the import created or changed.</summary>
        public HashSet<string> Written()
        {
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, stamp) in Stamps(folder))
                if (!before.TryGetValue(path, out var old) || old != stamp)
                    written.Add(path.EndsWith(MetaExtension) ? path[..^MetaExtension.Length] : path);
            return written;
        }

        /// <summary>Asset paths (see <see cref="Key"/>) of the files the import added.</summary>
        public HashSet<string> Created() =>
            Stamps(folder).Keys.Where(p => !p.EndsWith(MetaExtension) && !before.ContainsKey(p)).ToHashSet(StringComparer.Ordinal);

        /// <summary>The first reusable pack file with <paramref name="extension"/> that <paramref name="accept"/>s. Null when there is none.</summary>
        public string FindFile(string extension, Func<string, bool> accept) =>
            reusableBySize.Values.SelectMany(p => p)
                .Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault(accept);

        /// <summary>
        /// A reusable pack file with exactly <paramref name="bytes"/> and the extension of <paramref name="fileName"/>
        /// that <paramref name="accept"/>s (one named <paramref name="fileName"/> first). Null when there is none.
        /// </summary>
        public string FindFile(byte[] bytes, string fileName, Func<string, bool> accept)
        {
            if (!reusableBySize.TryGetValue(bytes.LongLength, out var paths)) return null;
            var extension = Path.GetExtension(fileName);
            return paths.Where(p => string.Equals(Path.GetExtension(p), extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p) == fileName ? 0 : 1)
                .FirstOrDefault(p => File.ReadAllBytes(p).AsSpan().SequenceEqual(bytes) && accept(p));
        }

        /// <summary>
        /// A reusable pack material that renders like <paramref name="m"/> (<see cref="AssetDeduplicator.MaterialKey"/>,
        /// one with its name first). Null when there is none.
        /// </summary>
        public Material FindMaterial(Material m)
        {
            reusableMaterials ??= reusableBySize.Values.SelectMany(p => p)
                .Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<Material>)
                .Where(mat => mat != null)
                .GroupBy(AssetDeduplicator.MaterialKey)
                .ToDictionary(g => g.Key, g => g.ToList());
            return reusableMaterials.TryGetValue(AssetDeduplicator.MaterialKey(m), out var list)
                ? list.OrderBy(c => c.name == m.name ? 0 : 1).First()
                : null;
        }

        /// <summary>
        /// The asset path of a material named <paramref name="name"/> in <paramref name="folder"/> that renders as
        /// <paramref name="key"/> (<see cref="AssetDeduplicator.MaterialKey"/>): the first of name, name_2, … this import
        /// has not given other content, one whose stored asset already renders so first (it keeps its GUID).
        /// <paramref name="claimed"/>: the material this import already made for that path, else null.
        /// </summary>
        public string MaterialPath(string folder, string name, string key, out Material claimed)
        {
            claimed = null;
            string free = null;
            for (var i = 1;; i++)
            {
                var path = i == 1 ? $"{folder}/{name}.mat" : $"{folder}/{name}_{i}.mat";
                if (claims.TryGetValue(path, out var claim))
                {
                    if (claim.key != key) continue;
                    claimed = claim.material;
                    return path;
                }
                free ??= path;
                var stored = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (stored == null) return free;
                if (AssetDeduplicator.MaterialKey(stored) == key) return path;
            }
        }

        /// <summary>Records that this import gave <paramref name="path"/> the content <paramref name="key"/>.</summary>
        public void Claim(string path, string key, Material material) => claims[path] = (key, material);

        static Dictionary<string, (long, DateTime)> Stamps(string folder)
        {
            var stamps = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                stamps[Key(path)] = (info.Length, info.LastWriteTimeUtc);
            }
            return stamps;
        }
    }
}
