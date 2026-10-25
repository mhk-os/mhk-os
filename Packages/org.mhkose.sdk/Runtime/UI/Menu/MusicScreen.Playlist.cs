using System;
using System.Collections.Generic;
using MHK.Audio;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class MusicScreen
    {
        void FillPlaylist(string focusButton, int focusIndex)
        {
            playlistList.Clear();
            var songs = PlaylistSongs();
            var key = PlaylistKey;
            var shuffled = PlaylistShuffle;
            VisualElement focus = null;
            for (var i = 0; i < songs.Count; i++)
            {
                var song = MusicLibrary.Find(songs[i]);
                var row = SpawnSong(playlistList, song, i % 2 == 1);
                if (row == null) break;
                var index = i;
                row.Q<Button>("song-main").clicked += () => PlayOrPause(key, songs, shuffled, index);
                var remove = row.Q<Button>("song-remove");
                remove.clicked += () => AskRemove(remove, song, index);
                if (index == focusIndex) focus = row.Q<Button>(focusButton) ?? row.Q<Button>("song-main");
            }
            playlistScroll.SetVisible(songs.Count > 0);
            SetVisible("playlist-empty", songs.Count == 0);
            ShowPlaylistState(songs);
            MarkCurrent(playlistList);
            FocusLater(focus);
        }

        void ShowPlaylistState(List<string> songs)
        {
            var shuffled = PlaylistShuffle;
            shuffle.EnableInClassList("selected", shuffled);
            shuffle.tooltip = shuffled ? shuffleOnTip : shuffleOffTip;
            var isDefault = PlaylistIsDefault;
            reset.SetEnabled(!isDefault);
            reset.tooltip = isDefault ? isDefaultTip : resetTip;
            var title = playlistTitle.text;
            foreach (var row in songList.Children())
            {
                if (row.userData is not MusicLibrary.Song song) continue;
                var added = songs.Contains(song.path);
                var add = row.Q<Button>("song-add");
                add.SetEnabled(!added);
                add.tooltip = string.Format(added ? alreadyAddedFormat : addTipFormat, title);
            }
        }

        void Add(string path)
        {
            Click();
            Edit(songs =>
            {
                if (!songs.Contains(path)) songs.Add(path);
            });
            playlistScroll.schedule.Execute(() => playlistScroll.scrollOffset = new UnityEngine.Vector2(0f, float.MaxValue));
        }

        // DragReorder already shows the new order and focuses the moved row's handle after a gamepad move.
        void MoveSong(int from, int to) => Edit(songs =>
        {
            var song = songs[from];
            songs.RemoveAt(from);
            songs.Insert(to, song);
        });

        void AskRemove(Button from, MusicLibrary.Song song, int index)
        {
            Click();
            ConfirmPrompt.Show(from, string.Format(removePromptFormat, song.title, playlistTitle.text), removeConfirm, () =>
            {
                var left = PlaylistSongs().Count - 1;
                Edit(songs => songs.RemoveAt(index), "song-remove", Math.Min(index, left - 1));
                if (left == 0) FocusLater(shuffle);
            });
        }

        void Edit(Action<List<string>> change, string focusButton = null, int focusIndex = -1)
        {
            var songs = PlaylistSongs();
            change(songs);
            Store(songs);
            FillPlaylist(focusButton, focusIndex);
            FollowEdit();
        }

        // Nothing is stored when the playlist equals the default (so the default follows new packs). No song left is
        // stored as emptied, which plays nothing instead of the default.
        void Store(List<string> songs)
        {
            var keep = songs.Count > 0 && !MusicLibrary.SameSongs(songs, DefaultSongs());
            var own = showTracks ? MusicLibrary.TrackSettings(trackId, true) : null;
            var stored = own != null ? own.songs : Settings.menuMusic;
            stored.Clear();
            if (keep) stored.AddRange(songs);
            if (own != null) own.empty = songs.Count == 0;
            else Settings.menuMusicEmpty = songs.Count == 0;
        }

        void ToggleShuffle()
        {
            if (showTracks)
            {
                var own = MusicLibrary.TrackSettings(trackId, true);
                own.shuffle = !own.shuffle;
            }
            else Settings.menuMusicShuffle = !Settings.menuMusicShuffle;
            ShowPlaylistState(PlaylistSongs());
            FollowEdit();
        }

        void AskReset() => ConfirmPrompt.Show(reset, string.Format(resetPromptFormat, playlistTitle.text), resetConfirm, ResetPlaylist);

        void ResetPlaylist()
        {
            if (showTracks)
            {
                var own = MusicLibrary.TrackSettings(trackId, true);
                own.songs.Clear();
                own.shuffle = own.empty = false;
            }
            else
            {
                Settings.menuMusic.Clear();
                Settings.menuMusicShuffle = true;
                Settings.menuMusicEmpty = false;
            }
            FillPlaylist(null, -1);
            FollowEdit();
            FocusLater(shuffle);
        }

        // The edited playlist goes on from its current song when it is the one that plays.
        void FollowEdit()
        {
            if (MusicPlayer.Key != PlaylistKey) return;
            if (showTracks) MusicPlayer.Play(PlaylistKey, PlaylistSongs(), PlaylistShuffle, Volume);
            else MusicPlayer.PlayMenu(Volume);
        }
    }
}
