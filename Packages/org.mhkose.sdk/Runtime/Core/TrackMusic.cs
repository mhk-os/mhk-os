using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>The player's own playlist of one track (<see cref="UserSettings.trackMusic"/>, Music screen).</summary>
    [Serializable]
    public class TrackMusic
    {
        public string trackId;
        [Tooltip("Virtual music paths in play order. None = the track's own music, or silence when empty is on.")]
        public List<string> songs = new();
        [Tooltip("The playlist plays in random order (a new order each round).")]
        public bool shuffle;
        [Tooltip("The player removed every song: the track races without music. Off with no songs = the track's own music.")]
        public bool empty;

        /// <summary>Nothing differs from the track's default playlist (the entry can be dropped).</summary>
        public bool IsDefault => songs.Count == 0 && !shuffle && !empty;
    }
}
