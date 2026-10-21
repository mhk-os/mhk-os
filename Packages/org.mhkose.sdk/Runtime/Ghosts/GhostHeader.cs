using System;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Ghost metadata: who drove what where, the result, and the simulation version and content fingerprint it was
    /// recorded with (ghosts of another version or content never count as records).
    /// </summary>
    [Serializable]
    public sealed class GhostHeader
    {
        /// <summary>Unique id of this run.</summary>
        public string id;
        /// <summary>Build of the recording game (<see cref="Core.BuildInfo.Id"/>, a build number in older ghosts): a label only.</summary>
        public string gameBuild;
        /// <summary>Simulation version of the recording build (<see cref="Core.SimulationVersion.Current"/>).</summary>
        public int simVersion;
        public string playerName;
        /// <summary>Stable id of the recording player (<see cref="Core.UserSettings.playerId"/>).</summary>
        public string playerId;
        /// <summary>Recording date, UTC ticks.</summary>
        public long dateUtcTicks;
        public string trackId;
        public string trackPackId;
        public string trackPackVersion;
        /// <summary>Hash of the track's driving-relevant data (<see cref="GhostFingerprint.TrackHash"/>).</summary>
        public string trackHash;
        public string kartId;
        public string characterId;
        /// <summary><see cref="Core.Difficulty"/> (engine class) index.</summary>
        public int engineClass;
        /// <summary>Its label at recording time (e.g. "100cc").</summary>
        public string engineClassLabel;
        public int laps;
        /// <summary>Hash of the race rules, engine class and kart physics (<see cref="GhostFingerprint.SetupHash"/>).</summary>
        public string setupHash;
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
