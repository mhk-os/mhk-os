using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Last Man Standing (<see cref="GameMode.LastManStanding"/>): a race of one lap less than there are karts
    /// (<see cref="LapsFor"/>). Each lap, the moment every other kart still in has completed it, the kart that has not
    /// is knocked out. It stops, plays its losing reaction and fades off the track (the local player's kart stays for
    /// the camera). So one kart goes per lap, and the kart that completes the last lap first is the last one standing.
    /// The race is decided when one kart is left or every human is out, and the results follow after
    /// <see cref="GameConfig.eliminationGraceSeconds"/>. Online the server decides the knockouts
    /// (<see cref="EliminateShared"/>), a knocked-out kart drives on as a ghost and the server's result ends the race.
    /// </summary>
    public class LastManStandingRules : MonoBehaviour, IComparer<Kart>
    {
        // The losing reaction plays before a knocked-out kart fades; the fade itself (KartSeeThrough) takes about 0.3 s.
        const float OutFadeDelaySeconds = 1.5f;
        const float OutFadeSeconds = 0.5f;

        RaceManager race;
        float endTimer = -1f;
        readonly Dictionary<Kart, int> outLaps = new();
        readonly List<(Kart kart, float left)> fading = new();

        /// <summary>Laps of a Last Man Standing race of <paramref name="karts"/> karts: one per knockout, at least 1.</summary>
        public static int LapsFor(int karts) => Mathf.Max(1, karts - 1);

        /// <summary>Karts not knocked out yet.</summary>
        public int AliveCount
        {
            get
            {
                var n = 0;
                if (race != null)
                    for (var i = 0; i < race.Karts.Count; i++)
                        if (!race.Karts[i].Eliminated) n++;
                return n;
            }
        }

        /// <summary>One kart is left or every human is out: no more knockouts, the results follow.</summary>
        public bool Decided { get; private set; }

        /// <summary>A kart was knocked out (its finish at the knockout time is reported right before).</summary>
        public event Action<Kart> KartEliminated;

        public void Begin(RaceManager raceManager) => race = raceManager;

        // Online the server knocks karts out (its Eliminated push), never this client's own count.
        bool Shared => race != null && race.IsOnline;

        /// <summary>Lap on which <paramref name="k"/> was knocked out (1 = the first lap), 0 while it is still in.</summary>
        public int OutLap(Kart k) => outLaps.TryGetValue(k, out var lap) ? lap : 0;

        void FixedUpdate()
        {
            if (race == null || race.Phase != RacePhase.Racing || Decided) return;
            if (Shared)
            {
                Decided = AliveCount <= 1;
                return;
            }
            while (KnockOutNext()) { }
            Decided = AliveCount <= 1 || AllHumansOut();
        }

        // The lap being decided is the one after the knockouts so far. Once every kart still in but one has completed it,
        // the kart furthest behind goes; karts completing it in the same step leave the one behind as the last.
        bool KnockOutNext()
        {
            var karts = race.Karts;
            var lap = outLaps.Count + 1;
            int alive = 0, done = 0;
            Kart last = null;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (k.Eliminated) continue;
                alive++;
                if (k.Progress.LapTimes.Count >= lap) done++;
                if (last == null || Behind(k, last)) last = k;
            }
            if (alive < 2 || done < alive - 1) return false;
            KnockOut(last, lap);
            return true;
        }

        static bool Behind(Kart a, Kart b)
        {
            var pa = a.Progress;
            var pb = b.Progress;
            if (pa.RaceDistance != pb.RaceDistance) return pa.RaceDistance < pb.RaceDistance;
            return pa.Finished && pb.Finished && pa.FinishTime > pb.FinishTime;
        }

        /// <summary>
        /// Online the server knocked <paramref name="k"/> out (the kart furthest behind once every other kart still in
        /// completed the lap). It becomes a ghost and drives on.
        /// </summary>
        public void EliminateShared(Kart k)
        {
            if (k != null && !k.Eliminated) KnockOut(k, outLaps.Count + 1);
        }

        void KnockOut(Kart k, int lap)
        {
            outLaps[k] = lap;
            k.Eliminate();
            // Online the kart drives on as a ghost (set before its finish, so the race keeps the camera on it).
            if (Shared) k.IsGhost = true;
            k.Progress.ForceFinish(k.Progress.RaceClock);
            KartEliminated?.Invoke(k);
            if (k != race.LocalPlayer && !Shared) fading.Add((k, OutFadeDelaySeconds + OutFadeSeconds));
        }

        bool AllHumansOut()
        {
            var humans = 0;
            var karts = race.Karts;
            for (var i = 0; i < karts.Count; i++)
            {
                if (!karts[i].IsHuman) continue;
                if (!karts[i].Eliminated) return false;
                humans++;
            }
            return humans > 0;
        }

        // Knocked-out karts fade after their losing reaction and leave the track (still ranked and in the results).
        void Update()
        {
            for (var i = fading.Count - 1; i >= 0; i--)
            {
                var (k, left) = fading[i];
                left -= Time.deltaTime;
                if (k == null)
                {
                    fading.RemoveAt(i);
                    continue;
                }
                if (left <= OutFadeSeconds) k.LeaveFade = 0f;
                if (left > 0f)
                {
                    fading[i] = (k, left);
                    continue;
                }
                k.gameObject.SetActive(false);
                fading.RemoveAt(i);
            }
        }

        /// <summary>
        /// Runs the grace once the race is decided. Returns true when it has elapsed: karts still in that have not
        /// finished then finish where they are (ranked by the distance they drove), and the race can end.
        /// </summary>
        public bool TickRaceEnd(float dt)
        {
            if (!Decided) return false;
            if (endTimer < 0f) endTimer = GameRoot.Instance.Config.eliminationGraceSeconds;
            endTimer -= dt;
            if (endTimer > 0f) return false;
            var karts = race.Karts;
            for (var i = 0; i < karts.Count; i++)
                if (!karts[i].Eliminated) karts[i].Progress.ForceFinish(karts[i].Progress.RaceClock);
            return true;
        }

        /// <summary>
        /// Karts still in first, by distance driven (the winner's whole race first, a tie between finishers by finish
        /// time), then the knocked-out karts, the last one out first.
        /// </summary>
        public int Compare(Kart a, Kart b)
        {
            if (a.Eliminated != b.Eliminated) return a.Eliminated ? 1 : -1;
            if (a.Eliminated) return OutLap(b).CompareTo(OutLap(a));
            var pa = a.Progress;
            var pb = b.Progress;
            var byDistance = pb.RaceDistance.CompareTo(pa.RaceDistance);
            if (byDistance != 0 || !(pa.Finished && pb.Finished)) return byDistance;
            return pa.FinishTime.CompareTo(pb.FinishTime);
        }
    }
}
