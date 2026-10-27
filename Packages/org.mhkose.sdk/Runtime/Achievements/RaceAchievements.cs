using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Input;
using MHK.Karts;
using MHK.Modding;
using MHK.Online;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Achievements
{
    /// <summary>
    /// Records the achievement stats of one race for the local players (<see cref="AchievementStats"/>): the metres they
    /// drive, the karts each of their powerup uses hits, their own planted hazards they run into, and at the results
    /// their places, wins, times, tracks and championships. Added by <see cref="RaceManager"/> to every race but menu
    /// background races and replays.
    /// </summary>
    sealed class RaceAchievements : MonoBehaviour
    {
        // A kart that moved further in one physics step was put back on the track (respawn), it did not drive there.
        const float MaxStepMetres = 4f;
        const float FastRaceSeconds = 120f;
        // Racers needed for a top-3 place and for a win to count.
        const int MinRacersForTop3 = 4;
        const int MinRacersForWin = 2;

        [AutoStaticsCleanup] static RaceAchievements current;
        // The championship whose race wins are followed, and per race whether a local player won it (a restarted race
        // overwrites its entry).
        [AutoStaticsCleanup] static GameSession cupSession;
        [AutoStaticsCleanup] static readonly List<bool> cupWins = new();

        sealed class Use
        {
            public Kart user;
            public string stat;
            public readonly HashSet<Kart> victims = new();
        }

        RaceManager race;
        readonly Dictionary<Kart, Vector3> lastPosition = new();
        // Metres driven and not recorded yet.
        double metres;
        readonly Dictionary<int, Use> uses = new();
        readonly HashSet<int> ownHazardUses = new();

        internal void Begin(RaceManager raceManager)
        {
            race = raceManager;
            race.ResultsReady += OnResults;
            current = this;
        }

        void OnDestroy()
        {
            if (current == this) current = null;
            if (race != null) race.ResultsReady -= OnResults;
            RecordDistance();
        }

        /// <summary>A local player used <paramref name="def"/>: the karts this use hits are counted.</summary>
        internal static void ItemUsed(Kart user, PowerupDefinition def, int usage)
        {
            if (current == null || usage == 0 || def == null || !IsLocalPlayer(user)) return;
            current.uses[usage] = new Use { user = user, stat = AchievementStats.PowerupHits(def.Id) };
        }

        /// <summary>A hit of use <paramref name="usage"/> by <paramref name="source"/> landed on <paramref name="victim"/>.</summary>
        internal static void KartHit(Kart victim, Kart source, int usage)
        {
            if (current == null || usage == 0 || victim == source || !current.uses.TryGetValue(usage, out var use)) return;
            if (use.user == source && use.victims.Add(victim)) AchievementStats.Max(use.stat, use.victims.Count);
        }

        /// <summary>A hazard planted by use <paramref name="usage"/> of <paramref name="kart"/> caught that kart itself.</summary>
        internal static void OwnHazard(Kart kart, int usage)
        {
            if (current != null && usage != 0 && IsLocalPlayer(kart) && current.ownHazardUses.Add(usage)) AchievementStats.Add(AchievementStats.OwnTraps);
        }

        static bool IsLocalPlayer(Kart k) => k != null && k.IsHuman && !k.IsRemote;

        // The player drives (not the computer after the line, an autopilot or a benchmark).
        static bool Drives(Kart k) => k.Driver is PlayerKartDriver && !k.IsRemote && !k.Progress.Finished && !k.Eliminated;

        void FixedUpdate()
        {
            if (race.Phase != RacePhase.Racing)
            {
                lastPosition.Clear();
                return;
            }
            var karts = race.Karts;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!Drives(k))
                {
                    lastPosition.Remove(k);
                    continue;
                }
                var p = k.Physics.Body.position;
                if (lastPosition.TryGetValue(k, out var before))
                {
                    var step = Vector3.Distance(before, p);
                    if (step <= MaxStepMetres) metres += step;
                }
                lastPosition[k] = p;
            }
        }

        void RecordDistance()
        {
            var whole = Math.Floor(metres);
            if (whole < 1.0) return;
            metres -= whole;
            AchievementStats.Add(AchievementStats.DistanceMetres, (long)whole);
        }

        void OnResults()
        {
            RecordDistance();
            var session = race.Session;
            RaceResult best = null;
            foreach (var r in race.Results)
                if (r.isHuman && Completed(r) && (best == null || r.position < best.position)) best = r;
            var racers = race.Results.Count;
            if (session.mode == GameMode.Championship && !session.online) FollowChampionship(session, best != null && best.position == 1 && racers >= MinRacersForWin);
            if (best == null) return;
            AchievementStats.Collect(AchievementStats.TracksFinished, race.Track.Id);
            if (session.mode == GameMode.Battle)
            {
                if (best.position == 1 && racers >= MinRacersForWin) AchievementStats.Add(AchievementStats.BattlesWon);
                return;
            }
            if (best.time > 0f && best.time < FastRaceSeconds) AchievementStats.Add(AchievementStats.RacesUnder2Min);
            if (session.mode == GameMode.TimeTrial) return;
            AchievementStats.Add(AchievementStats.RacesFinished);
            if (best.position <= 3 && racers >= MinRacersForTop3) AchievementStats.Add(AchievementStats.RacesTop3);
            if (best.position != 1 || racers < MinRacersForWin) return;
            AchievementStats.Add(AchievementStats.RacesWon);
            if (session.online && race.Online != null && !race.Online.Solo) AchievementStats.Add(AchievementStats.OnlineRacesWon);
        }

        // Offline every local player finished when the results come; online the server's outcome says.
        static bool Completed(RaceResult r) => r.outcome == null ? r.time > 0f : r.outcome == RaceOutcome.Finished;

        static void FollowChampionship(GameSession session, bool won)
        {
            if (cupSession != session || session.raceIndex == 0)
            {
                cupSession = session;
                cupWins.Clear();
            }
            while (cupWins.Count <= session.raceIndex) cupWins.Add(false);
            cupWins[session.raceIndex] = won;
            if (session.HasNextRace || string.IsNullOrEmpty(session.cupId) || cupWins.Count != session.trackIds.Count || !cupWins.TrueForAll(w => w)) return;
            AchievementStats.Add(AchievementStats.PerfectChampionships);
            AchievementStats.Collect(AchievementStats.PerfectCups, session.cupId);
        }
    }
}
