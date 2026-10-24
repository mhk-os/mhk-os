using System;
using System.Collections.Generic;
using System.Threading;
using MHK.Audio;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Music (Music.uxml): every song by game with a player (play, pause, seek, previous, next), and the editors of the
    /// main menu playlist and of each track's playlist (add, remove, reorder, shuffle, back to the default). Leaving the
    /// page continues the main menu playlist.
    /// </summary>
    public partial class MusicScreen : MenuScreen
    {
        const string LibraryKey = "library";

        [SerializeField, Tooltip("Song row (Templates/SongRow.uxml).")]
        VisualTreeAsset songRowTemplate;
        [SerializeField, Tooltip("Playlist tab (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Card of the track strip (Templates/TrackCard.uxml).")]
        VisualTreeAsset trackCardTemplate;

        [Header("Texts")]
        [SerializeField, Tooltip("Tab and title of the main menu playlist.")]
        string menuTitle = "Main Menu";
        [SerializeField, Tooltip("Tab of the track playlists.")]
        string tracksTitle = "Tracks";
        [SerializeField, Tooltip("Appended to a track card's game when the track has a variant. {0} = variant.")]
        string variantFormat = " | {0}";
        [SerializeField, Tooltip("Tooltip of a library song's add button. {0} = playlist title.")]
        string addTipFormat = "Add to {0}";
        [SerializeField, Tooltip("Tooltip of the add button of a song that is already in the playlist. {0} = playlist title.")]
        string alreadyAddedFormat = "Already in {0}";
        [SerializeField, Tooltip("Tooltip of Remove on the only song of a playlist.")]
        string lastSongTip = "A playlist keeps at least one song.";
        [SerializeField, Tooltip("Tooltip of the shuffle button while it is on.")]
        string shuffleOnTip = "Shuffle: on";
        [SerializeField, Tooltip("Tooltip of the shuffle button while it is off.")]
        string shuffleOffTip = "Shuffle: off";
        [SerializeField, Tooltip("Tooltip of the reset button of an own playlist.")]
        string resetTip = "Back to the default playlist";
        [SerializeField, Tooltip("Tooltip of the reset button while the playlist is the default one.")]
        string isDefaultTip = "This is the default playlist.";
        [SerializeField, Tooltip("Tooltip of the play button while music plays.")]
        string pauseTip = "Pause";
        [SerializeField, Tooltip("Tooltip of the play button while the music is paused or stopped.")]
        string playTip = "Play";

        bool showTracks;
        string trackId;
        readonly List<string> libraryPaths = new();
        VisualElement songList, playlistList, trackList, playPauseIcon, lastListFocus, lastLibraryFocus;
        Label playlistTitle, nowTitle, nowGame, elapsed, total;
        Button shuffle, reset, playPause;
        ScrollView playlistScroll;
        Slider seek;
        bool seeking, syncing;
        int shownVersion, shownSecond;
        float shownLength;
        CancellationTokenSource probes;

        void Reset() => showcaseVisible = false;

        string PlaylistKey => showTracks ? MusicPlayer.TrackKeyPrefix + trackId : MusicPlayer.MenuKey;
        TrackDefinition Track => Content.Get<TrackDefinition>(trackId);
        float Volume => Flow.MenuMusicVolume;

        protected override void Bind()
        {
            songList = Q<VisualElement>("song-list");
            playlistList = Q<VisualElement>("playlist-list");
            trackList = Q<VisualElement>("track-list");
            playlistTitle = Q<Label>("playlist-title");
            playlistScroll = Q<ScrollView>("playlist-scroll");
            shuffle = OnClick("shuffle", ToggleShuffle);
            reset = OnClick("reset", ResetPlaylist);
            KeepRowsInView(Q<ScrollView>("song-scroll"), songList);
            KeepRowsInView(playlistScroll, playlistList);
            KeepRowsInView(Q<ScrollView>("track-scroll"), trackList);
            songList.RegisterCallback<NavigationMoveEvent>(e => OnListNavigate(e, true));
            playlistList.RegisterCallback<NavigationMoveEvent>(e => OnListNavigate(e, false));
            songList.RegisterCallback<FocusInEvent>(RememberListFocus);
            playlistList.RegisterCallback<FocusInEvent>(RememberListFocus);
            Q<VisualElement>("player").RegisterCallback<NavigationMoveEvent>(OnPlayerNavigate);
            lastListFocus = lastLibraryFocus = null;
            ChipGroup.Fill(Q<VisualElement>("playlist-tabs"), chipTemplate, new[] { menuTitle, tracksTitle }, showTracks ? 1 : 0, ShowTab);
            BindPlayer();
            FocusTarget = FillLibrary();
            FillTracks();
            ShowPlaylist();
            probes = new CancellationTokenSource();
            ProbeLengths(probes.Token);
        }

        protected override void Unbind()
        {
            base.Unbind();
            probes?.Cancel();
            probes = null;
            MusicLibrary.PruneTrackSettings();
            GameRoot.Instance.SaveSettings();
            if (MusicPlayer.Key != null) MusicPlayer.PlayMenu(Volume);
            songList = playlistList = trackList = playPauseIcon = lastListFocus = lastLibraryFocus = null;
            playlistTitle = nowTitle = nowGame = elapsed = total = null;
            shuffle = reset = playPause = null;
            playlistScroll = null;
            seek = null;
        }

        // The focused button sits inside a row: scroll to the row, the direct child of the list.
        static void KeepRowsInView(ScrollView scroll, VisualElement list) =>
            list.RegisterCallback<FocusInEvent>(e =>
            {
                var v = e.target as VisualElement;
                while (v != null && v.parent != list) v = v.parent;
                if (v != null) scroll.ScrollTo(v);
            });

        VisualElement FillLibrary()
        {
            songList.Clear();
            libraryPaths.Clear();
            string group = null;
            VisualElement first = null;
            var alt = false;
            foreach (var song in MusicLibrary.Songs)
            {
                if (song.packId != group)
                {
                    group = song.packId;
                    var header = new Label(UiText.GameName(group));
                    header.AddToClassList("music-group");
                    songList.Add(header);
                    alt = false;
                }
                var index = libraryPaths.Count;
                libraryPaths.Add(song.path);
                var row = SpawnSong(songList, song, alt);
                if (row == null) break;
                alt = !alt;
                var main = row.Q<Button>("song-main");
                main.clicked += () => PlayOrPause(LibraryKey, libraryPaths, false, index);
                row.Q<Button>("song-add").clicked += () => Add(song.path);
                first ??= main;
            }
            SetVisible("song-empty", libraryPaths.Count == 0);
            return first;
        }

        VisualElement SpawnSong(VisualElement list, MusicLibrary.Song song, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(songRowTemplate, list);
            if (row == null) return null;
            row.userData = song;
            row.EnableInClassList("alt", alt);
            row.SetText("song-title", song.title);
            row.SetText("song-game", UiText.GameName(song.packId));
            ShowLength(row, song);
            return row;
        }

        static void ShowLength(VisualElement row, MusicLibrary.Song song)
        {
            var label = row.Q<Label>("song-length");
            if (song.duration >= 0f) label.SetMinutesSeconds(song.duration);
            else label.text = "";
        }

        // Lengths are measured one song at a time while the page is open, each once per content load.
        async void ProbeLengths(CancellationToken ct)
        {
            var all = new List<MusicLibrary.Song>(MusicLibrary.Songs);
            foreach (var song in all)
            {
                if (song.duration >= 0f) continue;
                try
                {
                    await MusicLibrary.DurationAsync(song, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Music] {song.path}: {e.Message}");
                    continue;
                }
                if (ct.IsCancellationRequested || !IsBound) return;
                ShowLengthIn(songList, song);
                ShowLengthIn(playlistList, song);
            }
        }

        static void ShowLengthIn(VisualElement list, MusicLibrary.Song song)
        {
            foreach (var row in list.Children())
                if (row.userData == song) ShowLength(row, song);
        }

        void PlayOrPause(string key, IReadOnlyList<string> songs, bool shuffled, int index)
        {
            Click();
            if (string.Equals(MusicPlayer.CurrentPath, songs[index], StringComparison.OrdinalIgnoreCase))
                MusicPlayer.SetPause(!MusicPlayer.Paused);
            else MusicPlayer.Play(key, songs, shuffled, Volume, index);
        }

        void ShowTab(int tab)
        {
            Click();
            showTracks = tab == 1;
            ShowPlaylist();
        }

        void FillTracks()
        {
            trackList.Clear();
            var tracks = Content.All<TrackDefinition>();
            if (Track == null) trackId = tracks.Count > 0 ? tracks[0].Id : null;
            foreach (var t in tracks)
            {
                var card = UiTree.Spawn<Button>(trackCardTemplate, trackList);
                if (card == null) break;
                card.Q("card-image")?.SetImage(t.Picture);
                card.SetText("card-title", t.DisplayName);
                card.SetText("card-tag", UiText.GameWithVariant(t, variantFormat));
                card.userData = t.Id;
                card.EnableInClassList("selected", t.Id == trackId);
                var id = t.Id;
                card.clicked += () => PickTrack(id);
            }
        }

        void PickTrack(string id)
        {
            Click();
            trackId = id;
            foreach (var card in trackList.Children()) card.EnableInClassList("selected", (string)card.userData == id);
            ShowPlaylist();
        }

        void ShowPlaylist()
        {
            Q<VisualElement>("track-scroll").SetVisible(showTracks);
            var track = Track;
            playlistTitle.text = showTracks ? track != null ? track.DisplayName : "" : menuTitle;
            FillPlaylist(null, -1);
            if (showTracks) FocusSelectedTrackLater();
        }

        void FocusSelectedTrackLater()
        {
            foreach (var card in trackList.Children())
                if ((string)card.userData == trackId) trackList.schedule.Execute(() => Q<ScrollView>("track-scroll").ScrollTo(card));
        }

        List<string> PlaylistSongs() => showTracks ? MusicLibrary.TrackSongs(Track) : MusicLibrary.MenuSongs();

        List<string> DefaultSongs() => showTracks ? MusicLibrary.DefaultFor(Track) : MusicLibrary.DefaultMenu();

        bool PlaylistShuffle
        {
            get
            {
                if (!showTracks) return Settings.menuMusicShuffle;
                var own = MusicLibrary.TrackSettings(trackId, false);
                return own != null && own.shuffle;
            }
        }

        bool PlaylistIsDefault
        {
            get
            {
                if (!showTracks) return Settings.menuMusic.Count == 0 && Settings.menuMusicShuffle;
                var own = MusicLibrary.TrackSettings(trackId, false);
                return own == null || own.IsDefault;
            }
        }
    }
}
