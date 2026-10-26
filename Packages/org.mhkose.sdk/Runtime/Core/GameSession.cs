using System;
using System.Collections.Generic;
using MHK.Modding;

namespace MHK.Core
{
    /// <summary>Race kinds. Keep the numeric values when adding or renaming one.</summary>
    public enum GameMode { SingleRace = 0, Championship = 1, TimeTrial = 2, Battle = 3 }

    /// <summary>Engine class (50cc, 100cc, 150cc) and computer driver skill.</summary>
    public enum Difficulty { Easy = 0, Medium = 1, Hard = 2 }

    /// <summary>One racer of a session: driver, kart, human or computer, and championship standing.</summary>
    [Serializable]
    public class RacerSetup
    {
        public string characterId;
        public string kartId;
        public bool isHuman;
        public int playerIndex = -1;
        public string displayName;

        /// <summary>Championship points (persist across races of a cup).</summary>
        public int points;
        public float totalTime;

        /// <summary>Online races: the racer's grid slot (<c>RaceSlot.slot</c>), -1 offline and for local AI.</summary>
        public int onlineSlot = -1;
        /// <summary>Online races: the racer's country (ISO 3166-1 alpha-2, "" unknown) for its name tag.</summary>
        public string country = "";
        /// <summary>Online races: the racer's device (<c>RaceSlot.platform</c>), 0 unknown.</summary>
        public int platform;
    }

    /// <summary>Everything the Race scene needs to know about the next race(s). Created by the menus, kept by <see cref="GameRoot"/>.</summary>
    [Serializable]
    public class GameSession
    {
        public const int DefaultOpponents = 5;

        public GameMode mode = GameMode.SingleRace;
        public Difficulty difficulty = Difficulty.Medium;
        public int laps = 3;
        public bool lapsFromTrack = true;
        public List<RacerSetup> racers = new();
        public List<string> trackIds = new();
        public string cupId;
        public int raceIndex;
        /// <summary>Item boxes give powerups (all off in time trials, or when every single race odd is 0 %).</summary>
        public bool powerupsEnabled = true;
        /// <summary>
        /// Single race and battle: the player's own item odds (<see cref="Powerups.ItemOdds"/>), empty = the default
        /// odds of the track's item family. Championships ignore it and give the cup's family.
        /// </summary>
        public List<PowerupOdds> powerupOdds = new();
        /// <summary>Time trial: race the personal best of the track and engine class (looked up when the race loads).</summary>
        public bool raceOwnBest = true;
        /// <summary>Time trial: ids of further ghosts to race (<see cref="Ghosts.IGhostStore"/>), in the order picked.</summary>
        public List<string> ghostIds = new();
        /// <summary>Time trial replay: id of the ghost to watch, no kart races (<see cref="IsReplay"/>).</summary>
        public string replayGhostId;
        /// <summary>
        /// An online race (<see cref="Online.OnlineRace"/> holds its setup): the grid is <see cref="racers"/> in order,
        /// the server gives GO, finishes and results.
        /// </summary>
        public bool online;

        /// <summary>A time-trial replay: the race shows a recorded ghost instead of racing.</summary>
        public bool IsReplay => mode == GameMode.TimeTrial && !string.IsNullOrEmpty(replayGhostId);

        public string CurrentTrackId => raceIndex >= 0 && raceIndex < trackIds.Count ? trackIds[raceIndex] : null;
        public bool HasNextRace => raceIndex + 1 < trackIds.Count;

        public void ResetStandings()
        {
            foreach (var r in racers) { r.points = 0; r.totalTime = 0; }
            raceIndex = 0;
        }

        /// <summary>Builds a default session (first character vs. AI on the first track) for direct play-mode testing.</summary>
        public static GameSession CreateQuickRace(ContentRegistry registry, string trackId = null, int opponents = DefaultOpponents)
        {
            var s = new GameSession();
            var chars = registry.All<CharacterDefinition>();
            var tracks = registry.All<TrackDefinition>();
            if (chars.Count == 0 || tracks.Count == 0) return s;
            s.trackIds.Add(trackId ?? tracks[0].Id);
            for (var i = 0; i <= opponents; i++)
            {
                var c = chars[i % chars.Count];
                s.racers.Add(new RacerSetup
                {
                    characterId = c.Id,
                    kartId = c.defaultKart != null ? c.defaultKart.Id : null,
                    isHuman = i == 0,
                    playerIndex = i == 0 ? 0 : -1,
                    displayName = c.DisplayName,
                });
            }
            s.ResetStandings();
            return s;
        }
    }
}
