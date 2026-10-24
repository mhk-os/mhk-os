using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Assets
{
    /// <summary>Kind of a replaceable asset.</summary>
    public enum GameAssetKind { Texture, Audio }

    /// <summary>
    /// Replaceable textures and sounds by virtual path (&lt;packId&gt;/&lt;path inside the pack&gt;). The winning file
    /// comes from the enabled asset packs (highest first), else the owning mod's loose Assets folder, else the bundled
    /// asset. Textures are replaced in place during content load, sounds resolve through <see cref="Audio"/>.
    /// </summary>
    public static partial class GameAssets
    {
        const string MusicWord = "music";

        [AutoStaticsCleanup] static readonly Dictionary<string, List<string>> pathLists = new();

        /// <summary>
        /// Editor only: the project's replaceable assets (project packs and the game have no built index there). Null in
        /// players. Set by the editor, so it survives play-mode entry.
        /// </summary>
        [NoAutoStaticsCleanup] public static IEditorAssetSource EditorSource { get; set; }

        /// <summary>After a content (re)load applied the overrides of the asset packs and loose files.</summary>
        public static event Action Reloaded;

        /// <summary>Virtual path of <paramref name="asset"/>, null when it is not a replaceable pack asset.</summary>
        public static string PathOf(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            if (Catalog.TryPathOf(asset, out var path)) return path;
            return EditorSource != null ? EditorSource.PathOf(asset) : null;
        }

        /// <summary>
        /// Every virtual path of <paramref name="kind"/> in a loaded pack's <paramref name="folder"/> (relative to the pack,
        /// e.g. "Music/"), plus the new files asset packs add under any &lt;packId&gt;/<paramref name="folder"/>.
        /// </summary>
        public static IReadOnlyList<string> Paths(GameAssetKind kind, string folder)
        {
            folder ??= "";
            var key = (int)kind + "|" + folder.ToLowerInvariant();
            if (pathLists.TryGetValue(key, out var list)) return list;
            list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Catalog.AllPaths()) AddPath(list, seen, p, kind, folder);
            if (EditorSource != null)
                foreach (var p in EditorSource.Paths()) AddPath(list, seen, p, kind, folder);
            foreach (var p in AssetPacks.AddedPaths()) AddPath(list, seen, p, kind, folder);
            pathLists[key] = list;
            return list;
        }

        static void AddPath(List<string> list, HashSet<string> seen, string path, GameAssetKind kind, string folder)
        {
            if (KindOf(path) != kind || !InFolder(path, folder)) return;
            if (seen.Add(WithoutExtension(path))) list.Add(path);
        }

        /// <summary>The clip to play for <paramref name="clip"/>: its loaded replacement, else the clip itself. Call wherever a clip is played or assigned.</summary>
        public static AudioClip Audio(AudioClip clip) => AudioOverrides.Resolve(clip);

        /// <summary>
        /// Loads the sound at <paramref name="path"/>: the winning file (streamed when <paramref name="stream"/>, for music),
        /// else the packed clip. Null when nothing exists at the path. GameAssets owns the clip (do not destroy it): file
        /// clips stay cached per path until the next content reload, the oldest are released unless an AudioSource holds them.
        /// </summary>
        public static Awaitable<AudioClip> LoadAudioAsync(string path, bool stream, CancellationToken ct) =>
            AudioOverrides.LoadAsync(path, stream, ct);

        /// <summary>Length in seconds of the sound at <paramref name="path"/> (0 when none), without keeping the clip loaded.</summary>
        public static Awaitable<float> DurationAsync(string path, CancellationToken ct) => AudioOverrides.DurationAsync(path, ct);

        /// <summary>
        /// Readable title of <paramref name="path"/>: the file name without extension and without a leading
        /// "&lt;packId&gt;_" and a trailing "music" word, separators as spaces, words capitalised ("mhk2/Music/mhk2_egypt_music.ogg" is "Egypt").
        /// </summary>
        public static string Title(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var name = WithoutPackPrefix(Path.GetFileNameWithoutExtension(path), path).Replace('_', ' ').Replace('-', ' ');
            var sb = new StringBuilder(name.Length);
            var start = true;
            foreach (var c in name)
            {
                if (c == ' ')
                {
                    if (!start) sb.Append(' ');
                    start = true;
                    continue;
                }
                sb.Append(start ? char.ToUpper(c, CultureInfo.InvariantCulture) : c);
                start = false;
            }
            var title = sb.ToString().TrimEnd();
            var last = title.LastIndexOf(' ');
            return last > 0 && string.Equals(title.Substring(last + 1), MusicWord, StringComparison.OrdinalIgnoreCase) ? title.Substring(0, last) : title;
        }

        static string WithoutPackPrefix(string name, string path)
        {
            var slash = path.IndexOf('/');
            if (slash <= 0 || name.Length <= slash + 1) return name;
            var separator = name[slash];
            return (separator == '_' || separator == '-') && string.Compare(name, 0, path, 0, slash, StringComparison.OrdinalIgnoreCase) == 0
                ? name.Substring(slash + 1)
                : name;
        }

        /// <summary>Kind of the file at <paramref name="path"/> by its extension, null when it is neither an image nor a sound.</summary>
        public static GameAssetKind? KindOf(string path)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return null;
            ext = ext.ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") return GameAssetKind.Texture;
            if (ext == ".ogg" || ext == ".wav" || ext == ".mp3" || ext == ".aif" || ext == ".aiff") return GameAssetKind.Audio;
            return null;
        }

        static bool InFolder(string path, string folder)
        {
            if (folder.Length == 0) return true;
            var slash = path.IndexOf('/');
            return slash >= 0 && string.Compare(path, slash + 1, folder, 0, folder.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        internal static string WithoutExtension(string path)
        {
            var dot = path.LastIndexOf('.');
            return dot > path.LastIndexOf('/') ? path.Substring(0, dot) : path;
        }

        internal static void ClearPathLists() => pathLists.Clear();

        internal static void RaiseReloaded() => Reloaded?.Invoke();
    }

    /// <summary>Editor only: the project's replaceable assets, for packs that have no built <see cref="PackAssetIndex"/>.</summary>
    public interface IEditorAssetSource
    {
        /// <summary>Virtual path of a project asset, null when it is not replaceable.</summary>
        string PathOf(UnityEngine.Object asset);

        /// <summary>Virtual paths of every replaceable project texture and sound.</summary>
        IReadOnlyList<string> Paths();

        /// <summary>The project asset at a virtual path, null when none.</summary>
        UnityEngine.Object Load(string path);
    }
}
