using System;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Ghost metadata: who drove what where, the result, and the versions of the rules, track variant and kart it was
    /// recorded with (ghosts of other versions never count as records).
    /// </summary>
    [Serializable]
    public sealed class GhostHeader
    {
        /// <summary>Unique id of this run.</summary>
        public string id;
        /// <summary>Commit of the recording game (<see cref="Core.BuildInfo.Commit"/>, version plus commit or a build number in older ghosts): a label only.</summary>
        public string gameBuild;
        /// <summary>Rules version of the recording build (<see cref="Core.RulesVersion.Current"/>).</summary>
        public int rulesVersion;
        public string playerName;
        /// <summary>Stable id of the recording player (<see cref="Core.UserSettings.playerId"/>).</summary>
        public string playerId;
        /// <summary>Recording date, UTC ticks.</summary>
        public long dateUtcTicks;
        public string trackId;
        public string trackPackId;
        /// <summary>Commit of the track's pack (<see cref="Modding.ContentPack.version"/>, a version in older ghosts): a label only.</summary>
        public string trackPackVersion;
        /// <summary>Content version of the driven track variant (<see cref="Modding.TrackVariant.ContentVersion"/>).</summary>
        public int trackVersion;
        /// <summary>Key of the driven track variant (<see cref="Modding.TrackVariant.Key"/>, "" = the track's default).</summary>
        public string variant = "";
        public string kartId;
        /// <summary>Content version of the kart (<see cref="Modding.KartDefinition.contentVersion"/>).</summary>
        public int kartVersion;
        public string characterId;
        /// <summary><see cref="Core.Difficulty"/> (engine class) index.</summary>
        public int engineClass;
        /// <summary>Its label at recording time (e.g. "100cc").</summary>
        public string engineClassLabel;
        public int laps;
        /// <summary>Physics tick of the recording (s).</summary>
        public float tickSeconds;
        /// <summary>The kart's speed scale (forward speed in original MHK2 units = forward speed ÷ this).</summary>
        public float speedScale = 1f;
        /// <summary>Race time at the finish line (s from GO).</summary>
        public float finishTime;
        /// <summary>Lap times (s), in order.</summary>
        public float[] lapTimes = Array.Empty<float>();
        /// <summary>Recorded ticks (filled when written / read).</summary>
        public int frameCount;

        public DateTime Date => new(dateUtcTicks, DateTimeKind.Utc);

        public float BestLap
        {
            get
            {
                var best = -1f;
                foreach (var t in lapTimes)
                    if (best < 0f || t < best) best = t;
                return best;
            }
        }

        /// <summary>
        /// Race time (s from GO) at which the kart crossed the line ending lap <paramref name="lap"/> (1-based), from the
        /// finish time and the lap times (the first lap starts at the first crossing, after the run-up from the grid).
        /// </summary>
        public float LapEndTime(int lap)
        {
            if (lapTimes.Length == 0) return finishTime;
            var t = finishTime;
            for (var i = lapTimes.Length - 1; i >= lap; i--) t -= lapTimes[i];
            return t;
        }
    }
}
