using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Modding;
using UnityEditor;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Which textures and sounds are replaceable and their virtual paths ("&lt;packId&gt;/&lt;path inside the pack's
    /// folder&gt;", the folder of the pack asset). Assets of the game outside every pack use the pack id "game".
    /// </summary>
    public static class PackAssetPaths
    {
        /// <summary>Pack id of the player's own assets (game UI, SDK shared assets).</summary>
        public const string GamePackId = "game";

        // Game project folders of the player's own assets and their virtual folder under "game/", most specific first.
        static readonly (string folder, string virtualFolder)[] GameRoots =
        {
            ("Assets/MHK/", ""),
            (SdkPaths.Root + "/", "SDK/"),
            ("Assets/", ""),
        };

        static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };
        static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3" };

        /// <summary>Folder of a pack whose sounds all ship (the music library), referenced or not.</summary>
        public const string MusicFolder = "Music";

        /// <summary>A pack folder (asset path ending in '/') and its pack id.</summary>
        public readonly struct PackFolder
        {
            public readonly string folder;
            public readonly string packId;

            public PackFolder(string folder, string packId)
            {
                this.folder = folder;
                this.packId = packId;
            }
        }

        /// <summary>The folders of <paramref name="packs"/> and the packs they include, deepest first.</summary>
        public static List<PackFolder> Folders(IEnumerable<ContentPack> packs) =>
            packs.SelectMany(p => p.SelfAndIncluded()).Distinct()
                .Select(p => new PackFolder(AssetPaths.DirectoryOf(p) + "/", p.packId.ToLowerInvariant()))
                .OrderByDescending(f => f.folder.Length).ToList();

        /// <summary>Asset paths of the sounds in the <see cref="MusicFolder"/> of each of <paramref name="packs"/>.</summary>
        public static IEnumerable<string> MusicFiles(IEnumerable<PackFolder> packs)
        {
            foreach (var p in packs)
            {
                var folder = p.folder + MusicFolder;
                if (!AssetDatabase.IsValidFolder(folder)) continue;
                foreach (var path in EditorAssets.FindPaths("t:AudioClip", folder)) yield return path;
            }
        }

        /// <summary>
        /// Whether the asset at <paramref name="assetPath"/> ships as a loose file: a texture imported from a PNG or JPG
        /// file that the runtime can decode, or a sound imported from a WAV, OGG or MP3 file. Generated textures (lightmaps,
        /// probes, atlases, render textures, sub-assets) never are.
        /// </summary>
        public static bool IsReplaceable(string assetPath)
        {
            var ext = Path.GetExtension(assetPath).ToLowerInvariant();
            if (Array.IndexOf(AudioExtensions, ext) >= 0) return AssetImporter.GetAtPath(assetPath) is AudioImporter;
            if (Array.IndexOf(ImageExtensions, ext) < 0) return false;
            return AssetImporter.GetAtPath(assetPath) is TextureImporter t && IsReplaceableTexture(t);
        }

        /// <summary>Whether <paramref name="assetPath"/> is a replaceable texture (else a sound).</summary>
        public static bool IsTexture(string assetPath) => Array.IndexOf(ImageExtensions, Path.GetExtension(assetPath).ToLowerInvariant()) >= 0;

        // Only plain 2D images reload from a file in place. Sprites keep rects in texture pixels, normal maps and single
        // channel textures are re-encoded by the importer, lightmaps and cookies come from bakes: those stay packed.
        static bool IsReplaceableTexture(TextureImporter t) =>
            t.textureShape == TextureImporterShape.Texture2D &&
            (t.textureType == TextureImporterType.Default || t.textureType == TextureImporterType.GUI);

        /// <summary>
        /// Virtual path of the asset at <paramref name="assetPath"/>: under the deepest of <paramref name="packs"/>, else
        /// (game project only) under "game/", else null (a package asset of a mod project, stays packed).
        /// </summary>
        public static string VirtualPath(string assetPath, IReadOnlyList<PackFolder> packs)
        {
            foreach (var p in packs)
                if (assetPath.StartsWith(p.folder, StringComparison.Ordinal))
                    return p.packId + "/" + assetPath.Substring(p.folder.Length);
            return GamePath(assetPath);
        }

        /// <summary>The "game/..." path of an asset of the player, null outside the game's folders.</summary>
        public static string GamePath(string assetPath)
        {
            foreach (var (folder, virtualFolder) in GameRoots)
            {
                if (!assetPath.StartsWith(folder, StringComparison.Ordinal)) continue;
                if (folder == "Assets/" && !SdkPaths.IsGameProject) return null;
                return GamePackId + "/" + virtualFolder + assetPath.Substring(folder.Length);
            }
            return null;
        }

        /// <summary>The folders of the project's packs that are not the game's own (shipped and project mods), deepest first.</summary>
        public static List<PackFolder> ProjectFolders() => Folders(ContentPackAssets.ShippedPacks().Concat(ProjectModPacks.All()));

        /// <summary>Virtual path of the project asset at <paramref name="assetPath"/> (play mode in the editor), null when it is not replaceable.</summary>
        public static string ProjectPath(string assetPath, IReadOnlyList<PackFolder> folders) =>
            IsReplaceable(assetPath) ? VirtualPath(assetPath, folders) : null;

        /// <summary>A replaceable asset of a build: its asset path, virtual path and the scenes that alone use it.</summary>
        public readonly struct Item
        {
            public readonly string assetPath;
            public readonly string path;
            public readonly string[] scenes;

            public Item(string assetPath, string path, string[] scenes)
            {
                this.assetPath = assetPath;
                this.path = path;
                this.scenes = scenes;
            }
        }

        /// <summary>
        /// The replaceable assets that <paramref name="sharedRoots"/> (pack assets, Resources) and <paramref name="scenes"/>
        /// use, sorted by virtual path. An asset only scenes use lists them. Two assets on the same path (case-insensitive)
        /// are an error: the first one is kept, the other stays packed.
        /// </summary>
        public static List<Item> Collect(IEnumerable<string> sharedRoots, IEnumerable<string> scenes, IReadOnlyList<PackFolder> packs)
        {
            var shared = WithoutScenes(sharedRoots);
            var sceneUsers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var scene in scenes)
            foreach (var dep in AssetDatabase.GetDependencies(scene, true))
            {
                if (shared.Contains(dep)) continue;
                if (!sceneUsers.TryGetValue(dep, out var users)) sceneUsers.Add(dep, users = new List<string>());
                users.Add(scene);
            }
            var byPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetPath in shared.Concat(sceneUsers.Keys))
            {
                if (!IsReplaceable(assetPath)) continue;
                var path = VirtualPath(assetPath, packs);
                if (path == null) continue;
                if (byPath.TryGetValue(path, out var other))
                    UnityEngine.Debug.LogError($"[Assets] '{assetPath}' and '{other.assetPath}' share the path {path}: '{assetPath}' stays packed.");
                else byPath.Add(path, new Item(assetPath, path, sceneUsers.TryGetValue(assetPath, out var users) ? users.ToArray() : Array.Empty<string>()));
            }
            return byPath.Values.OrderBy(e => e.path, StringComparer.Ordinal).ToList();
        }

        // Definitions refer to their scenes (track scene references), which would make every scene asset shared: the walk
        // stops at scene files, the scenes are walked on their own.
        static HashSet<string> WithoutScenes(IEnumerable<string> roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var open = new Stack<string>(roots);
            while (open.Count > 0)
            {
                var path = open.Pop();
                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || !seen.Add(path)) continue;
                foreach (var dep in AssetDatabase.GetDependencies(path, false)) open.Push(dep);
            }
            return seen;
        }
    }
}
