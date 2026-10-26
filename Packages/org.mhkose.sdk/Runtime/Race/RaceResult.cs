using MHK.Core;

namespace MHK.Race
{
    public enum RacePhase { Loading, Intro, Countdown, Racing, Finished, Results }

    /// <summary>One row of the race results.</summary>
    public class RaceResult
    {
        public int position;
        public string name;
        public string characterId;
        public string kartId;
        public bool isHuman;
        public float time;
        public float bestLap;
        public int pointsGained;
        public int totalPoints;
        /// <summary>Battle modes: still in the game when the round ended.</summary>
        public bool survived;
        /// <summary>Online: how the race ended for the racer by the server's result, null for local racers (AI, offline).</summary>
        public Online.RaceOutcome? outcome;
        /// <summary>Online: the result came from the server (points are set points, no records).</summary>
        public bool online;

        internal RacerSetup racer;
        internal float totalTimeBefore;

        /// <summary>Takes this race's points and time back from the racer (the race is restarted).</summary>
        internal void Rollback()
        {
            if (racer == null) return;
            racer.points -= pointsGained;
            racer.totalTime = totalTimeBefore;
        }
    }
}
