using System;
using MHK.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class MusicScreen
    {
        void BindPlayer()
        {
            OnClick("previous", MusicPlayer.Previous);
            OnClick("next", MusicPlayer.Next);
            playPause = OnClick("play-pause", TogglePlay);
            playPauseIcon = Q<VisualElement>("play-pause-icon");
            nowTitle = Q<Label>("now-title");
            nowGame = Q<Label>("now-game");
            elapsed = Q<Label>("elapsed");
            total = Q<Label>("total");
            seek = Q<Slider>("seek");
            seek.RegisterValueChangedCallback(e =>
            {
                if (!syncing) MusicPlayer.Seek(e.newValue);
            });
            seek.RegisterCallback<PointerDownEvent>(_ => seeking = true, TrickleDown.TrickleDown);
            seek.RegisterCallback<PointerUpEvent>(_ => seeking = false, TrickleDown.TrickleDown);
            seek.RegisterCallback<PointerCaptureOutEvent>(_ => seeking = false);
            seeking = false;
            shownVersion = -1;
            shownSecond = -1;
            shownLength = -1f;
        }

        void TogglePlay()
        {
            if (MusicPlayer.CurrentPath == null) MusicPlayer.PlayMenu(Volume);
            else MusicPlayer.SetPause(!MusicPlayer.Paused);
        }

        void Update()
        {
            if (!IsBound || seek == null) return;
            var version = MusicPlayer.Version;
            if (version != shownVersion)
            {
                shownVersion = version;
                ShowSong();
            }
            var length = MusicPlayer.Length;
            if (length != shownLength)
            {
                shownLength = length;
                syncing = true;
                seek.SetValueWithoutNotify(0f);
                seek.highValue = Mathf.Max(0.01f, length);
                syncing = false;
                total.SetMinutesSeconds(length);
            }
            var time = MusicPlayer.Time;
            if (!seeking) seek.SetValueWithoutNotify(time);
            var second = (int)time;
            if (second == shownSecond) return;
            shownSecond = second;
            elapsed.SetMinutesSeconds(time);
        }

        void ShowSong()
        {
            var song = MusicLibrary.Find(MusicPlayer.CurrentPath);
            nowTitle.text = song != null ? song.title : "";
            nowGame.text = song != null ? UiText.GameName(song.packId) : "";
            var playing = song != null && !MusicPlayer.Paused;
            playPauseIcon.SetIcon(playing ? "pause" : "play_arrow");
            playPause.tooltip = playing ? pauseTip : playTip;
            MarkCurrent(songList);
            MarkCurrent(playlistList);
        }

        static void MarkCurrent(VisualElement list)
        {
            var path = MusicPlayer.CurrentPath;
            var paused = MusicPlayer.Paused;
            foreach (var row in list.Children())
            {
                if (row.userData is not MusicLibrary.Song song) continue;
                var current = path != null && string.Equals(song.path, path, StringComparison.OrdinalIgnoreCase);
                row.EnableInClassList("current", current);
                row.EnableInClassList("paused", current && paused);
            }
        }
    }
}
