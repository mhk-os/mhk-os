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
                var row = SpawnSong(playlistList, MusicLibrary.Find(songs[i]), i % 2 == 1);
                if (row == null) break;
                var index = i;
                row.Q<Button>("song-main").clicked += () => PlayOrPause(key, songs, shuffled, index);
                BindEdit(row.Q<Button>("song-up"), index > 0, () => Move(index, -1, "song-up"));
                BindEdit(row.Q<Button>("song-down"), index < songs.Count - 1, () => Move(index, 1, "song-down"));
                var remove = BindEdit(row.Q<Button>("song-remove"), songs.Count > 1, () => Remove(index));
                if (songs.Count == 1) remove.tooltip = lastSongTip;
                if (index != focusIndex) continue;
                var wanted = row.Q<Button>(focusButton);
                focus = wanted != null && wanted.enabledSelf ? wanted : row.Q<Button>("song-main");
            }
            ShowPlaylistState(songs);
            MarkCurrent(playlistList);
            FocusLater(focus);
        }

        Button BindEdit(Button button, bool enabled, Action edit)
        {
            button.SetEnabled(enabled);
            button.clicked += () =>
            {
                Click();
                edit();
            };
            return button;
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
            }, null, -1);
            playlistScroll.schedule.Execute(() => playlistScroll.scrollOffset = new UnityEngine.Vector2(0f, float.MaxValue));
        }

        void Move(int index, int step, string button) => Edit(songs =>
        {
            var to = index + step;
            (songs[index], songs[to]) = (songs[to], songs[index]);
        }, button, index + step);

        void Remove(int index)
        {
            var left = PlaylistSongs().Count - 1;
            Edit(songs => songs.RemoveAt(index), "song-main", Math.Min(index, left - 1));
        }

        // Edits the shown playlist and stores it (empty when it equals the default, so the default follows new packs).
        void Edit(Action<List<string>> change, string focusButton, int focusIndex)
        {
            var songs = PlaylistSongs();
            change(songs);
            var isDefault = MusicLibrary.SameSongs(songs, DefaultSongs());
            var stored = showTracks ? MusicLibrary.TrackSettings(trackId, true).songs : Settings.menuMusic;
            stored.Clear();
            if (!isDefault) stored.AddRange(songs);
            FillPlaylist(focusButton, focusIndex);
            FollowEdit();
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

        void ResetPlaylist()
        {
            if (showTracks)
            {
                var own = MusicLibrary.TrackSettings(trackId, true);
                own.songs.Clear();
                own.shuffle = false;
            }
            else
            {
                Settings.menuMusic.Clear();
                Settings.menuMusicShuffle = true;
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
