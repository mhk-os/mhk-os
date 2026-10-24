using System;
using System.Collections.Generic;
using System.Threading;
using MHK.Assets;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Audio
{
    /// <summary>
    /// Every music file of the loaded packs and asset packs (<see cref="GameAssets.Paths"/> under Music/), grouped by
    /// pack, and the player's playlists resolved against it: missing files are skipped, an empty result is the default.
    /// </summary>
    public static partial class MusicLibrary
    {
        /// <summary>Folder of every pack's music, relative to the pack.</summary>
        public const string Folder = "Music/";

        /// <summary>One music file.</summary>
        public sealed class Song
        {
            public string path, title, packId;
            /// <summary>Seconds, negative until <see cref="DurationAsync"/> measured it.</summary>
            public float duration = -1f;
        }

        [AutoStaticsCleanup] static readonly List<Song> songs = new();
        [AutoStaticsCleanup] static readonly Dictionary<string, Song> byPath = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static int builtVersion = -1;
        [AutoStaticsCleanup] static bool stale = true;
        [AutoStaticsCleanup] static bool listening;

        static ContentRegistry Content => GameRoot.Instance.Content;
        static UserSettings Settings => GameRoot.Instance.Settings;

        /// <summary>Every song, grouped by pack in content order, by title inside a pack.</summary>
        public static IReadOnlyList<Song> Songs
        {
            get
            {
                Refresh();
                return songs;
            }
        }

        /// <summary>The song at <paramref name="path"/> (any letter case), null when no loaded pack or asset pack has it.</summary>
        public static Song Find(string path)
        {
            Refresh();
            return path != null && byPath.TryGetValue(path, out var song) ? song : null;
        }

        /// <summary>Song length in seconds, measured once per song and content load (cheap, the clip is not kept).</summary>
        public static async Awaitable<float> DurationAsync(Song song, CancellationToken ct)
        {
            if (song.duration < 0f) song.duration = await GameAssets.DurationAsync(song.path, ct);
            return song.duration;
        }

        static void Refresh()
        {
            if (!listening)
            {
                GameAssets.Reloaded += () => stale = true;
                listening = true;
            }
            var version = Content.Version;
            if (!stale && version == builtVersion) return;
            stale = false;
            builtVersion = version;
            songs.Clear();
            byPath.Clear();
            foreach (var path in GameAssets.Paths(GameAssetKind.Audio, Folder))
            {
                if (byPath.ContainsKey(path)) continue;
                var song = new Song { path = path, title = GameAssets.Title(path), packId = PackOf(path) };
                songs.Add(song);
                byPath.Add(path, song);
            }
            songs.Sort(CompareSongs);
        }

        static string PackOf(string path)
        {
            var slash = path.IndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : "";
        }

        static int CompareSongs(Song a, Song b)
        {
            var byPack = PackRank(a.packId).CompareTo(PackRank(b.packId));
            if (byPack != 0) return byPack;
            var byId = string.Compare(a.packId, b.packId, StringComparison.OrdinalIgnoreCase);
            return byId != 0 ? byId : string.Compare(a.title, b.title, StringComparison.OrdinalIgnoreCase);
        }

        // Content order of the pack, packs that are not loaded (music an asset pack adds to them) last.
        static int PackRank(string packId)
        {
            var packs = Content.Packs;
            for (var i = 0; i < packs.Count; i++)
                if (string.Equals(packs[i].packId, packId, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        /// <summary>Default main menu playlist: the menu music of every loaded pack, in content order.</summary>
        public static List<string> DefaultMenu()
        {
            var list = new List<string>();
            foreach (var pack in Content.Packs)
                if (pack.menuMusic != null) AddIfKnown(list, GameAssets.PathOf(pack.menuMusic));
            return list;
        }

        /// <summary>Default playlist of <paramref name="track"/>: its own music.</summary>
        public static List<string> DefaultFor(TrackDefinition track)
        {
            var list = new List<string>();
            if (track != null && track.music != null) AddIfKnown(list, GameAssets.PathOf(track.music));
            return list;
        }

        static void AddIfKnown(List<string> list, string path)
        {
            var song = Find(path);
            if (song != null && !list.Contains(song.path)) list.Add(song.path);
        }

        /// <summary>The main menu playlist to play: the player's songs that exist, else <see cref="DefaultMenu"/>.</summary>
        public static List<string> MenuSongs() => Resolve(Settings.menuMusic, DefaultMenu);

        /// <summary>The playlist of <paramref name="track"/> to play: the player's songs that exist, else its own music.</summary>
        public static List<string> TrackSongs(TrackDefinition track)
        {
            var own = track != null ? TrackSettings(track.Id, false) : null;
            return Resolve(own?.songs, () => DefaultFor(track));
        }

        /// <summary>The player's playlist entry of the track <paramref name="trackId"/>, created when <paramref name="create"/> is set.</summary>
        public static TrackMusic TrackSettings(string trackId, bool create)
        {
            var all = Settings.trackMusic;
            for (var i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].trackId == trackId) return all[i];
            if (!create) return null;
            var entry = new TrackMusic { trackId = trackId };
            all.Add(entry);
            return entry;
        }

        /// <summary>Drops track entries that equal the default (keeps the settings file short).</summary>
        public static void PruneTrackSettings() => Settings.trackMusic.RemoveAll(t => t == null || t.IsDefault);

        /// <summary>The two playlists hold the same songs in the same order (paths compared ignoring case).</summary>
        public static bool SameSongs(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static List<string> Resolve(List<string> own, Func<List<string>> fallback)
        {
            var list = new List<string>();
            if (own != null)
                foreach (var path in own)
                {
                    var song = Find(path);
                    if (song != null) list.Add(song.path);
                }
            return list.Count > 0 ? list : fallback();
        }
    }
}
