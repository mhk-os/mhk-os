using System;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Deterministic clock and seeds for track objects that affect karts (crushers, moving walls, collapsing bridges).
    /// After GO the clock counts physics steps (<see cref="RaceManager.RaceTicks"/>), so a hazard driven by it is in the
    /// same state at the same race time in every run, in ghost replays (whose race clock is the replay's) and after
    /// replay seeking. Before GO it runs up to 0 at GO on frame time, which is visual only: karts cannot move yet.
    /// </summary>
    public static class RaceClock
    {
        const uint FnvOffset = 2166136261u;
        const uint FnvPrime = 16777619u;
        const uint IndexPrime = 0x9E3779B9u;
        const float UnitScale = 1f / 16777216f;

        /// <summary>
        /// Physics steps since GO, already counting the step being simulated (RaceManager steps first, execution order
        /// -100). 0 before GO and without a race. Read it in FixedUpdate.
        /// </summary>
        public static int Ticks
        {
            get
            {
                var race = RaceManager.Current;
                return race != null && race.ClockRunning ? race.RaceTicks : 0;
            }
        }

        /// <summary>The race clock runs (GO was given).</summary>
        public static bool Running
        {
            get
            {
                var race = RaceManager.Current;
                return race != null && race.ClockRunning;
            }
        }

        /// <summary>Length of one tick (s).</summary>
        public static float Step => Time.fixedDeltaTime;

        /// <summary>
        /// Race time (s) at the end of the current physics step: <see cref="Ticks"/> × <see cref="Step"/> from GO. Before
        /// GO minus the time left until GO (intro and countdown, frame time), held while the track loads. Without a race
        /// the scene time.
        /// </summary>
        public static double Seconds
        {
            get
            {
                var race = RaceManager.Current;
                if (race == null) return Time.timeAsDouble;
                return race.ClockRunning ? race.RaceTicks * (double)Time.fixedDeltaTime : -race.SecondsToGo;
            }
        }

        /// <summary>
        /// <see cref="Seconds"/> of the picture drawn now (<see cref="RaceManager.RenderSeconds"/>, the time ghosts are
        /// shown at), for visuals that follow a physics-driven object.
        /// </summary>
        public static double RenderSeconds
        {
            get
            {
                var race = RaceManager.Current;
                if (race == null) return Time.timeAsDouble;
                return race.ClockRunning ? race.RenderSeconds : -race.SecondsToGo;
            }
        }

        /// <summary>Race time (s) of tick <paramref name="ticks"/>.</summary>
        public static double SecondsAt(int ticks) => ticks * (double)Time.fixedDeltaTime;

        /// <summary>
        /// Stable seed of <paramref name="c"/>: hash of the track id, the object's hierarchy path (names and sibling
        /// indices) and <paramref name="salt"/>. The same in every run of the track. Reads names (allocates): call it in
        /// Awake / OnEnable, not per step.
        /// </summary>
        public static uint Seed(Component c, uint salt = 0)
        {
            var race = RaceManager.Current;
            var trackId = race != null && race.Track != null ? race.Track.Id : c.gameObject.scene.name;
            var h = Mix(FnvOffset, trackId);
            for (var t = c.transform; t != null; t = t.parent)
            {
                h = Mix(h, t.name);
                h = (h ^ (uint)t.GetSiblingIndex()) * FnvPrime;
            }
            return Finalise((h ^ salt) * FnvPrime);
        }

        /// <summary>Uniform value in [0, 1) number <paramref name="index"/> of <paramref name="seed"/>. Stateless, no allocation.</summary>
        public static float Value01(uint seed, int index) => (Finalise(seed ^ unchecked((uint)index * IndexPrime)) >> 8) * UnitScale;

        static uint Mix(uint h, string s)
        {
            if (s == null) return h;
            foreach (var ch in s) h = (h ^ ch) * FnvPrime;
            return (h ^ 0xFFu) * FnvPrime;
        }

        // murmur3 finaliser: every input bit affects every output bit.
        static uint Finalise(uint h)
        {
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }

        /// <summary>
        /// Clip time (s) of a clip <paramref name="length"/> s long at time <paramref name="t"/>: looped, or played forwards
        /// and backwards with <paramref name="pingPong"/>. Double precision so long races don't drift.
        /// </summary>
        public static float Cycle(double t, float length, bool pingPong)
        {
            if (length <= 0f) return 0f;
            double period = pingPong ? 2.0 * length : length;
            var r = t - Math.Floor(t / period) * period;
            if (pingPong) r = length - Math.Abs(r - length);
            return (float)r;
        }
    }
}
