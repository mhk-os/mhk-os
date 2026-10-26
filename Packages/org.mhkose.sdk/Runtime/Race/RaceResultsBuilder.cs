using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>End-of-race bookkeeping: finish times for racers still driving, results, points and track records.</summary>
    static class RaceResultsBuilder
    {
        const float MinOwnPaceClock = 5f;
        const float MinOwnPaceDistance = 50f;
        const float FieldPaceShare = 0.75f;
        const float MinPace = 5f;
        const float MaxSlowdown = 1.06f;
        const float MaxSecondsPerMissingLap = 4f;
        const float MinGapBehind = 0.2f, MaxGapBehind = 1.2f;

        /// <summary>
        /// Racers still on the track get a finish time from the distance left (all remaining laps) at their own average
        /// speed so far, slowed by a random 0-6 % plus 0-4 s per missing lap (MHK2), never ahead of a racer before them.
        /// Draws three <see cref="Random"/> values per unfinished racer in ranking order.
        /// </summary>
        public static void EstimateUnfinished(IReadOnlyList<Kart> karts, IReadOnlyList<Kart> ranking, float lapLength, int laps)
        {
            var total = lapLength * laps;
            var finished = karts.Where(k => k.Progress.Finished).ToList();
            var floor = finished.Count > 0 ? finished.Max(k => k.Progress.FinishTime) : 0f;
            // Field pace for karts without a usable own pace (stuck at the start): finishers' average speed.
            var fieldPace = finished.Count > 0 ? finished.Average(k => total / Mathf.Max(1f, k.Progress.FinishTime)) : 0f;
            foreach (var k in ranking.ToList())
            {
                var p = k.Progress;
                if (p.Finished) continue;
                var remaining = Mathf.Clamp(total - p.RaceDistance, 0f, total);
                var ownPace = p.RaceClock > MinOwnPaceClock && p.RaceDistance > MinOwnPaceDistance ? p.RaceDistance / p.RaceClock : 0f;
                var pace = Mathf.Max(ownPace, fieldPace * FieldPaceShare, MinPace);
                var time = p.RaceClock + remaining / pace * Random.Range(1f, MaxSlowdown)
                           + Random.Range(0f, MaxSecondsPerMissingLap) * (remaining / lapLength);
                time = Mathf.Max(time, floor + Random.Range(MinGapBehind, MaxGapBehind));
                floor = time;
                p.ForceFinish(time);
            }
        }

        /// <summary>Fills <paramref name="results"/> in ranking order, awards points and updates the track records.</summary>
        public static void Build(RaceManager race, IReadOnlyList<Kart> ranking, List<RaceResult> results)
        {
            var cfg = GameRoot.Instance.Config;
            var save = GameRoot.Instance.Save;
            var session = race.Session;
            var battle = race.Battle != null;
            results.Clear();
            for (var i = 0; i < ranking.Count; i++)
            {
                var k = ranking[i];
                var gained = session.mode == GameMode.Championship && i < cfg.championshipPoints.Length ? cfg.championshipPoints[i] : 0;
                results.Add(Award(k, i + 1, gained, battle));
                if (k.IsHuman && !battle) UpdateTrackRecord(save.GetRecord(race.Track.Id), k, race.Laps);
            }
            if (session.mode == GameMode.Championship && !session.HasNextRace) MarkCupWon(session, save);
            GameRoot.Instance.SaveProgress();
        }

        /// <summary>
        /// Online results: the server's rows (place, time, race and set points, outcome) in its order. A solo race shows
        /// the local ranking with the AI, the player's row taking the server's points.
        /// </summary>
        public static void BuildOnline(RaceManager race, IReadOnlyList<Kart> ranking, Online.OnlineRace online, List<RaceResult> results)
        {
            results.Clear();
            var server = online.Results;
            var battle = race.Battle != null;
            if (online.Solo)
            {
                var mine = server != null ? server.rows.Find(r => r.slot == online.LocalSlot && !r.ai) : null;
                for (var i = 0; i < ranking.Count; i++)
                {
                    var k = ranking[i];
                    var result = FromKart(k, i + 1, battle);
                    if (k == race.LocalPlayer && mine != null) TakeRow(result, mine, battle);
                    results.Add(result);
                }
                return;
            }
            if (server == null) return;
            var rows = new List<Online.RaceResultRow>(server.rows);
            rows.Sort((a, b) => a.position.CompareTo(b.position));
            foreach (var row in rows)
            {
                var slot = online.Setup.Slot(row.slot);
                var k = online.KartOf(row.slot);
                var result = k != null ? FromKart(k, row.position, battle) : new RaceResult
                {
                    characterId = slot != null ? slot.characterId : null,
                    kartId = slot != null ? slot.kartId : null,
                    bestLap = -1f,
                };
                result.name = row.username;
                result.isHuman = row.slot == online.LocalSlot;
                TakeRow(result, row, battle);
                results.Add(result);
            }
        }

        static RaceResult FromKart(Kart k, int position, bool battle) => new()
        {
            position = position,
            name = k.DisplayName,
            characterId = k.Character != null ? k.Character.Id : null,
            kartId = k.KartDef != null ? k.KartDef.Id : null,
            isHuman = k.IsHuman,
            time = k.Progress.FinishTime,
            bestLap = k.Progress.BestLap,
            survived = battle && !k.Eliminated,
            online = true,
        };

        static void TakeRow(RaceResult result, Online.RaceResultRow row, bool battle)
        {
            result.online = true;
            result.position = row.position;
            result.time = row.finishMs > 0 ? row.finishMs / 1000f : 0f;
            result.pointsGained = row.points;
            result.totalPoints = row.setPoints;
            result.status = row.outcome switch
            {
                Online.RaceOutcome.Dnf => battle ? null : "DNF",
                Online.RaceOutcome.Left => "Left",
                Online.RaceOutcome.KickedCheating => "Kicked",
                Online.RaceOutcome.KickedModerator => "Removed",
                Online.RaceOutcome.Desync => "Desync",
                _ => null,
            };
            if (battle && row.outcome != Online.RaceOutcome.Finished && row.outcome != Online.RaceOutcome.Dnf) result.survived = false;
        }

        static RaceResult Award(Kart k, int position, int gained, bool battle)
        {
            var result = new RaceResult { racer = k.Setup, totalTimeBefore = k.Setup.totalTime };
            k.Setup.points += gained;
            k.Setup.totalTime += k.Progress.FinishTime;
            result.position = position;
            result.name = k.DisplayName;
            result.characterId = k.Character != null ? k.Character.Id : null;
            result.kartId = k.KartDef != null ? k.KartDef.Id : null;
            result.isHuman = k.IsHuman;
            result.time = k.Progress.FinishTime;
            result.bestLap = k.Progress.BestLap;
            result.pointsGained = gained;
            result.totalPoints = k.Setup.points;
            result.survived = battle && !k.Eliminated;
            return result;
        }

        static void UpdateTrackRecord(TrackRecord rec, Kart k, int laps)
        {
            var p = k.Progress;
            var characterId = k.Character != null ? k.Character.Id : null;
            var kartId = k.KartDef != null ? k.KartDef.Id : null;
            if (p.BestLap > 0 && (rec.bestLap < 0 || p.BestLap < rec.bestLap))
            {
                rec.bestLap = p.BestLap;
                rec.bestLapCharacter = characterId;
                rec.bestLapKart = kartId;
            }
            if (p.LapTimes.Count >= laps && (rec.bestRace < 0 || p.FinishTime < rec.bestRace))
            {
                rec.bestRace = p.FinishTime;
                rec.bestRaceCharacter = characterId;
                rec.bestRaceKart = kartId;
            }
        }

        static void MarkCupWon(GameSession session, SaveData save)
        {
            var winner = session.racers.OrderByDescending(r => r.points).FirstOrDefault();
            if (winner != null && winner.isHuman && !string.IsNullOrEmpty(session.cupId) && !save.wonCups.Contains(session.cupId))
                save.wonCups.Add(session.cupId);
        }
    }
}
