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
    /// The music channel: plays a playlist of music files (virtual paths, streamed through <see cref="GameAssets"/>),
    /// the next song when one ends, looping, optionally shuffled. Lives on the GameRoot object.
    /// </summary>
    public partial class MusicPlayer : MonoBehaviour
    {
        /// <summary>Playlist key of the main menu playlist (<see cref="PlayMenu"/>).</summary>
        public const string MenuKey = "menu";
        /// <summary>Playlist key of a track playlist: this prefix plus the track id (<see cref="PlayTrack"/>).</summary>
        public const string TrackKeyPrefix = "track:";
        const float RestartThreshold = 3f;
        const float SeekEndMargin = 0.05f;
        const float FadeOutSeconds = 1f;

        [AutoStaticsCleanup] static MusicPlayer instance;


        AudioSource source;
        readonly List<string> songs = new();
        readonly List<string> order = new();
        readonly System.Random random = new();
        string key;
        int index = -1;
        bool shuffle, paused, loading, started, heard, fading;
        float pendingTime;
        float clipVolume = 1f, fade = 1f;
        int failures, version;
        CancellationTokenSource loadCancel;
        Resume menuResume;

        // Where the menu playlist was when another playlist replaced it, so returning to the menu continues it.
        sealed class Resume
        {
            public readonly List<string> songs = new(), order = new();
            public bool shuffle;
            public int index;
            public float time;
        }

        static MusicPlayer Instance
        {
            get
            {
                if (instance == null)
                {
                    var root = GameRoot.Instance;
                    instance = root.GetComponent<MusicPlayer>();
                    if (instance == null) instance = root.gameObject.AddComponent<MusicPlayer>();
                }
                return instance;
            }
        }

        void Awake()
        {
            instance = this;
            source = AudioSources.Create2D(gameObject, false);
            source.ignoreListenerVolume = true;
        }

        void OnDestroy() => loadCancel?.Cancel();

        /// <summary>Changes whenever the song, the pause state or the playlist changes (players refresh their display).</summary>
        public static int Version => instance != null ? instance.version : 0;
        /// <summary>Key of the playlist that plays (<see cref="MenuKey"/>, a track key or a caller's own), null when stopped.</summary>
        public static string Key => instance != null ? instance.key : null;
        /// <summary>Virtual path of the current song, null when stopped.</summary>
        public static string CurrentPath => instance != null && instance.index >= 0 ? instance.order[instance.index] : null;
        /// <summary>Index of the current song in <see cref="Order"/>, -1 when stopped.</summary>
        public static int CurrentIndex => instance != null ? instance.index : -1;
        /// <summary>The songs in play order (shuffled when the playlist shuffles).</summary>
        public static IReadOnlyList<string> Order => Instance.order;
        public static bool Paused => instance != null && instance.paused;
        /// <summary>Playback position of the current song in seconds.</summary>
        public static float Time => HasClip ? instance.source.time : 0f;
        /// <summary>Length of the current song in seconds, 0 while none is loaded.</summary>
        public static float Length => HasClip ? instance.source.clip.length : 0f;

        static bool HasClip => instance != null && instance.index >= 0 && instance.source.clip != null && !instance.loading;

        /// <summary>
        /// Plays the main menu playlist (<see cref="MusicLibrary.MenuSongs"/>) at <paramref name="volume"/>: keeps it
        /// playing when it already does (an edited playlist goes on from the current song), continues where it stopped
        /// when another playlist replaced it.
        /// </summary>
        public static void PlayMenu(float volume)
        {
            var p = Instance;
            var menu = MusicLibrary.MenuSongs();
            var shuffled = GameRoot.Instance.Settings.menuMusicShuffle;
            p.clipVolume = volume;
            if (p.key == MenuKey)
            {
                if (!MusicLibrary.SameSongs(p.songs, menu) || p.shuffle != shuffled) p.Retarget(menu, shuffled);
                p.ApplyVolume();
                if (p.paused) p.SetPaused(false);
                return;
            }
            var r = p.menuResume;
            if (r != null && MusicLibrary.SameSongs(r.songs, menu) && r.shuffle == shuffled)
                p.Open(MenuKey, menu, shuffled, volume, r.order, r.index, r.time);
            else if (r != null && menu.Contains(r.order[r.index]))
                p.Open(MenuKey, menu, shuffled, volume, null, menu.IndexOf(r.order[r.index]), r.time, true);
            else p.Open(MenuKey, menu, shuffled, volume, null, 0, 0f);
        }

        /// <summary>Plays the playlist of <paramref name="track"/> (<see cref="MusicLibrary.TrackSongs"/>) at the track's music volume (an empty one fades the music out).</summary>
        public static void PlayTrack(TrackDefinition track)
        {
            if (track == null) return;
            var own = MusicLibrary.TrackSettings(track.Id, false);
            Play(TrackKeyPrefix + track.Id, MusicLibrary.TrackSongs(track), own != null && own.shuffle, track.musicVolume);
        }

        /// <summary>
        /// Plays <paramref name="playlist"/> under <paramref name="playlistKey"/> from song <paramref name="start"/> (an
        /// index into the playlist) at <paramref name="volume"/>. Without a start the playlist that already plays under
        /// the same key keeps playing (an edited one from its current song). An empty playlist fades the music out.
        /// </summary>
        public static void Play(string playlistKey, IReadOnlyList<string> playlist, bool shuffled, float volume, int start = -1)
        {
            var p = Instance;
            p.clipVolume = volume;
            if (start < 0 && p.key == playlistKey)
            {
                if (!MusicLibrary.SameSongs(p.songs, playlist) || p.shuffle != shuffled) p.Retarget(playlist, shuffled);
                p.ApplyVolume();
                return;
            }
            p.Open(playlistKey, playlist, shuffled, volume, null, Mathf.Max(0, start), 0f, start >= 0);
        }

        /// <summary>Stops the music (the menu playlist's place is kept for <see cref="PlayMenu"/>).</summary>
        public static void Stop()
        {
            if (instance == null) return;
            instance.Remember();
            instance.Halt();
            instance.version++;
        }

        /// <summary>Pauses or continues the current song.</summary>
        public static void SetPause(bool pause)
        {
            if (instance != null && instance.index >= 0) instance.SetPaused(pause);
        }

        /// <summary>Next song (wraps to the first).</summary>
        public static void Next()
        {
            if (instance != null && instance.index >= 0) instance.Advance(1);
        }

        /// <summary>Back to the start of the song, or to the previous song when it just started.</summary>
        public static void Previous()
        {
            if (instance == null || instance.index < 0) return;
            if (Time > RestartThreshold) Seek(0f);
            else instance.Advance(-1);
        }

        /// <summary>Moves the current song to <paramref name="seconds"/>.</summary>
        public static void Seek(float seconds)
        {
            var p = instance;
            if (p == null || p.loading || p.source.clip == null) return;
            p.source.time = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, p.source.clip.length - SeekEndMargin));
        }

        /// <summary>Applies master × music gain × music setting × the playlist's own volume. Called by <see cref="AudioManager.RefreshVolumes"/>.</summary>
        public static void RefreshVolume()
        {
            if (instance != null) instance.ApplyVolume();
        }

        void Open(string playlistKey, IReadOnlyList<string> playlist, bool shuffled, float volume, List<string> resumeOrder,
            int start, float time, bool startIsPlaylistIndex = false)
        {
            if (key == MenuKey && playlistKey != MenuKey) Remember();
            if (playlistKey == MenuKey) menuResume = null;
            Halt(playlist.Count == 0);
            key = playlistKey;
            shuffle = shuffled;
            clipVolume = volume;
            songs.AddRange(playlist);
            if (resumeOrder != null) order.AddRange(resumeOrder);
            else BuildOrder(startIsPlaylistIndex && start < songs.Count ? songs[start] : null);
            if (order.Count == 0)
            {
                version++;
                return;
            }
            if (resumeOrder != null) index = Mathf.Clamp(start, 0, order.Count - 1);
            else index = startIsPlaylistIndex && !shuffle ? Mathf.Min(start, order.Count - 1) : 0;
            ApplyVolume();
            Load(time);
        }

        // Shuffled orders start with first when given, else they avoid repeating the song that just ended.
        void BuildOrder(string first)
        {
            var last = index >= 0 && index < order.Count ? order[index] : null;
            order.Clear();
            order.AddRange(songs);
            if (!shuffle) return;
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            var pin = first != null ? order.IndexOf(first) : order.Count > 1 && order[0] == last ? 1 + random.Next(order.Count - 1) : -1;
            if (pin > 0) (order[0], order[pin]) = (order[pin], order[0]);
        }

        // The playlist changed while it plays: the current song goes on when it is still in it.
        void Retarget(IReadOnlyList<string> playlist, bool shuffled)
        {
            var current = index >= 0 ? order[index] : null;
            songs.Clear();
            songs.AddRange(playlist);
            shuffle = shuffled;
            BuildOrder(current);
            version++;
            if (order.Count == 0)
            {
                var keep = key;
                Halt(true);
                key = keep;
                return;
            }
            var at = current != null ? order.IndexOf(current) : -1;
            index = Mathf.Max(0, at);
            source.loop = order.Count == 1;
            if (at < 0) Load(0f);
        }

        void Remember()
        {
            if (key != MenuKey || index < 0) return;
            menuResume ??= new Resume();
            menuResume.songs.Clear();
            menuResume.songs.AddRange(songs);
            menuResume.order.Clear();
            menuResume.order.AddRange(order);
            menuResume.shuffle = shuffle;
            menuResume.index = index;
            menuResume.time = started ? source.time : pendingTime;
        }

        // Stops at once, or lets a song that plays fade out (Update ends it).
        void Halt(bool fadeOut = false)
        {
            loadCancel?.Cancel();
            loadCancel = null;
            fading = fadeOut && (fading || started && !paused && source.isPlaying);
            if (!fading) EndClip();
            songs.Clear();
            order.Clear();
            index = -1;
            key = null;
            paused = loading = started = false;
            failures = 0;
        }

        void EndClip()
        {
            fading = false;
            fade = 1f;
            source.Stop();
            source.clip = null;
            ApplyVolume();
        }

        void FadeStep()
        {
            fade -= UnityEngine.Time.unscaledDeltaTime / FadeOutSeconds;
            if (fade <= 0f) EndClip();
            else ApplyVolume();
        }

        void Advance(int step)
        {
            var next = index + step;
            if (next >= order.Count)
            {
                if (shuffle) BuildOrder(null);
                next = 0;
            }
            else if (next < 0) next = order.Count - 1;
            index = next;
            Load(0f);
        }

        void SetPaused(bool pause)
        {
            paused = pause;
            if (pause) source.Pause();
            else if (started) source.UnPause();
            else if (!loading && source.clip != null) StartClip(pendingTime);
            version++;
        }

        void ApplyVolume()
        {
            var root = GameRoot.Instance;
            source.volume = fade * Mathf.Clamp01(clipVolume * root.Config.musicMixGain * root.Settings.musicVolume * root.Settings.masterVolume);
        }

        async void Load(float time)
        {
            loadCancel?.Cancel();
            loadCancel = new CancellationTokenSource();
            var ct = loadCancel.Token;
            var path = order[index];
            loading = true;
            started = false;
            if (fading) EndClip();
            source.Stop();
            version++;
            AudioClip clip = null;
            try
            {
                clip = await GameAssets.LoadAudioAsync(path, true, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Music] {path}: {e.Message}");
            }
            if (ct.IsCancellationRequested || this == null) return;
            loading = false;
            if (clip == null)
            {
                Skip(path);
                return;
            }
            failures = 0;
            source.clip = clip;
            source.loop = order.Count == 1;
            pendingTime = time;
            if (!paused) StartClip(time);
            version++;
        }

        void Skip(string path)
        {
            Debug.LogWarning($"[Music] {path} could not be loaded.");
            if (++failures >= order.Count) Halt();
            else Advance(1);
            version++;
        }

        void StartClip(float time)
        {
            source.Play();
            started = true;
            heard = false;
            if (time > 0f) source.time = Mathf.Min(time, Mathf.Max(0f, source.clip.length - SeekEndMargin));
        }

        // A song has ended once it was heard playing and stopped (a streamed clip may start a few frames late).
        void Update()
        {
            if (fading) FadeStep();
            if (index < 0 || loading || paused || !started || source.loop || AudioListener.pause) return;
            if (source.isPlaying) heard = true;
            else if (heard || source.clip.loadState == AudioDataLoadState.Failed) Advance(1);
        }
    }
}
