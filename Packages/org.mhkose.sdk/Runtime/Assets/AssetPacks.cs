using System;
using System.Collections.Generic;
using System.IO;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Assets
{
    /// <summary>
    /// Asset packs: folders or zips in <see cref="UserFolder"/> with a pack.json, an optional icon.png and replacement
    /// files at Assets/&lt;virtual path&gt;. Enabled packs apply in the order of <see cref="UserSettings.assetPackOrder"/>, the
    /// first wins. They replace textures and sounds of the game and of any mod and may add music.
    /// </summary>
    public static partial class AssetPacks
    {
        /// <summary>An asset pack found in <see cref="UserFolder"/>.</summary>
        public sealed class Info
        {
            public string id, title, description, author, location;
            public Texture2D icon;
            public bool enabled;
            /// <summary>Folder holding pack.json and Assets/ (the cache folder for a zip).</summary>
            public string folder;
        }

        [Serializable]
        sealed class PackJson
        {
            public string id, title, description, author;
        }

        const string PackFile = "pack.json";
        const string IconFile = "icon.png";
        const string AssetsFolder = "Assets";
        const string ZipExtension = ".zip";
        const string MusicFolder = "Music/";

        [AutoStaticsCleanup] static readonly List<Info> available = new();
        [AutoStaticsCleanup] static readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly List<string> added = new();

        /// <summary>Every asset pack found by the last content load in priority order (first = highest), enabled and disabled.</summary>
        public static IReadOnlyList<Info> Available => available;

        /// <summary>Folder for the player's asset packs, next to the user Mods folder.</summary>
        public static string UserFolder => DataPaths.AssetPacks;

        /// <summary>Extraction cache of zipped asset packs.</summary>
        public static string CacheFolder => Path.Combine(DataPaths.Root, "Cache", DataPaths.AssetPacksFolder);

        /// <summary>Scans <see cref="UserFolder"/> and collects the files of the enabled packs (the first pack wins a path).</summary>
        internal static void Refresh(UserSettings settings)
        {
            DestroyIcons();
            available.Clear();
            files.Clear();
            added.Clear();
            var found = Scan();
            Order(found, settings != null ? settings.assetPackOrder : null);
            foreach (var info in found)
            {
                info.enabled = settings == null || settings.disabledAssetPacks == null || !settings.disabledAssetPacks.Contains(info.id);
                available.Add(info);
                if (info.enabled) CollectFiles(info);
            }
            if (available.Count > 0)
                Debug.Log($"[Assets] {available.Count} asset packs ({files.Count} files from the enabled ones): {string.Join(", ", Describe())}.");
        }

        static IEnumerable<string> Describe()
        {
            foreach (var p in available) yield return p.enabled ? p.id : p.id + " (off)";
        }

        internal static bool HasFiles => files.Count > 0;

        /// <summary>The winning asset pack file for <paramref name="path"/> (any extension of the same kind), null when no enabled pack has one.</summary>
        internal static string FileFor(string path)
        {
            if (files.Count == 0 || !files.TryGetValue(GameAssets.WithoutExtension(path), out var file)) return null;
            return GameAssets.KindOf(file) == GameAssets.KindOf(path) ? file : null;
        }

        /// <summary>Virtual paths of the music files the enabled packs add (not replacing an indexed asset).</summary>
        internal static IEnumerable<string> AddedPaths()
        {
            foreach (var path in added)
                if (!Catalog.TryFind(path, out _)) yield return path;
        }

        static List<Info> Scan()
        {
            var found = new List<Info>();
            var root = UserFolder;
            var keep = new List<string>();
            try
            {
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                foreach (var dir in Directory.GetDirectories(root))
                    if (!Path.GetFileName(dir).StartsWith(".", StringComparison.Ordinal)) AddPack(found, dir, dir);
                foreach (var zip in Directory.GetFiles(root, "*" + ZipExtension))
                {
                    var dir = ZipCache.Extract(zip, CacheFolder);
                    if (dir == null) continue;
                    keep.Add(dir);
                    AddPack(found, PackRoot(dir), zip);
                }
                ZipCache.Prune(CacheFolder, keep);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Assets] Cannot scan asset packs in '{root}': {e.Message}");
            }
            return found;
        }

        // A zip made by compressing the pack folder holds that folder at its top.
        static string PackRoot(string dir)
        {
            if (File.Exists(Path.Combine(dir, PackFile)) || Directory.Exists(Path.Combine(dir, AssetsFolder))) return dir;
            var subs = Directory.GetDirectories(dir);
            return subs.Length == 1 ? subs[0] : dir;
        }

        static void AddPack(List<Info> found, string folder, string location)
        {
            var json = ReadJson(Path.Combine(folder, PackFile));
            var id = json != null && !string.IsNullOrWhiteSpace(json.id) ? json.id.Trim() : Path.GetFileNameWithoutExtension(location);
            if (found.Exists(p => string.Equals(p.id, id, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogWarning($"[Assets] Asset pack '{id}' exists twice: '{location}' is ignored.");
                return;
            }
            found.Add(new Info
            {
                id = id,
                title = json != null && !string.IsNullOrWhiteSpace(json.title) ? json.title : id,
                description = json != null ? json.description ?? "" : "",
                author = json != null ? json.author ?? "" : "",
                location = location,
                folder = folder,
                icon = LoadIcon(Path.Combine(folder, IconFile), id),
            });
        }

        static PackJson ReadJson(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                return JsonUtility.FromJson<PackJson>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Assets] Unreadable '{file}': {e.Message}");
                return null;
            }
        }

        static Texture2D LoadIcon(string file, string id)
        {
            if (!File.Exists(file)) return null;
            var icon = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = id + " icon", hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (icon.LoadImage(File.ReadAllBytes(file), true)) return icon;
            UnityEngine.Object.Destroy(icon);
            return null;
        }

        static void DestroyIcons()
        {
            foreach (var p in available)
                if (p.icon != null) UnityEngine.Object.Destroy(p.icon);
        }

        // Listed ids first in their order, packs new to the list after them by id.
        static void Order(List<Info> found, List<string> order)
        {
            found.Sort((a, b) =>
            {
                var ia = order != null ? order.IndexOf(a.id) : -1;
                var ib = order != null ? order.IndexOf(b.id) : -1;
                if (ia >= 0 && ib >= 0) return ia.CompareTo(ib);
                if (ia >= 0 || ib >= 0) return ia >= 0 ? -1 : 1;
                return string.Compare(a.id, b.id, StringComparison.OrdinalIgnoreCase);
            });
        }

        static void CollectFiles(Info info)
        {
            var root = Path.Combine(info.folder, AssetsFolder);
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var path = file.Substring(root.Length + 1).Replace('\\', '/');
                if (GameAssets.KindOf(path) == null || path.IndexOf('/') <= 0) continue;
                var key = GameAssets.WithoutExtension(path);
                if (files.ContainsKey(key)) continue;
                files[key] = file;
                if (IsMusic(path)) added.Add(path);
            }
        }

        static bool IsMusic(string path)
        {
            var slash = path.IndexOf('/');
            return GameAssets.KindOf(path) == GameAssetKind.Audio &&
                   string.Compare(path, slash + 1, MusicFolder, 0, MusicFolder.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }
}
