using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Rear awareness of a computer driver: it only sees what is behind while looking back (the player's turn-round
    /// animation), often while holding a rear item and casually otherwise. The last-placed driver never looks back.
    /// </summary>
    internal sealed class AILookBack
    {
        /// <summary>A driver looks back at least this long (s) before something goes to the rear.</summary>
        public const float Warning = 0.5f;
        const float RearFieldOfView = 120f, RearSightRange = 35f, DropReach = 20f;
        const float MinBehind = 1f, RearLineHalfWidth = 3f, RearLineSpread = 0.1f;
        const float FirstLookMin = 3f, FirstLookSpread = 6f;
        const float SlowRearInterval = 6f, FastRearInterval = 2.5f, RearIntervalCap = 1.3f;
        const float MinRearJitter = 0.7f, MaxRearJitter = 1.3f, MinCasualInterval = 8f, MaxCasualInterval = 20f;
        const float MinDuration = 0.8f, MaxDuration = 1.4f;
        const float PlantCheckInterval = 0.2f, PlantLookDuration = 1.2f, PlantLookLead = 0.3f;

        readonly DifficultySettings difficulty;
        readonly AIBlackboard board;
        readonly System.Random rng;
        float next;
        float plantCheckTimer;

        public AILookBack(DifficultySettings difficulty, AIBlackboard board, System.Random rng)
        {
            this.difficulty = difficulty;
            this.board = board;
            this.rng = rng;
            next = FirstLookMin + rng.NextFloat() * FirstLookSpread;
        }

        /// <summary>Seconds left of the current look back, 0 or less while looking ahead.</summary>
        public float Remaining { get; private set; }
        /// <summary>Seconds into the current look back.</summary>
        public float Elapsed { get; private set; }
        /// <summary>The current look back is for a strategic trap plant in a narrow segment.</summary>
        public bool PlantLook { get; private set; }
        /// <summary>Nearest kart seen behind during the current look back.</summary>
        public Kart SeenBehind { get; private set; }
        public bool Active => Remaining > 0f;

        /// <summary>
        /// Runs the current look back or schedules the next: often while holding a <paramref name="rearItem"/>, just
        /// before a narrow segment for a trap kept for one (<paramref name="plantNarrow"/>), else a casual glance.
        /// </summary>
        public void Tick(Kart kart, bool rearItem, bool plantNarrow, float dt)
        {
            if (Remaining > 0f)
            {
                Remaining -= dt;
                Elapsed += dt;
                SeenBehind = NearestBehind(kart);
                return;
            }
            SeenBehind = null;
            PlantLook = false;
            if (!kart.ControlsEnabled || kart.Effects.ControlsLocked || AIKartQueries.IsLast(kart)) return;
            if (plantNarrow && rearItem && TryPlantLook(kart, dt)) return;
            Schedule(rearItem, dt);
        }

        /// <summary>Ends the look back within <paramref name="seconds"/> (an item just went to the rear).</summary>
        public void CutShort(float seconds) => Remaining = Mathf.Min(Remaining, seconds);

        /// <summary><paramref name="target"/> is in line behind the kart, within drop reach for a <paramref name="trap"/>.</summary>
        public static bool InRearLine(Kart kart, Kart target, bool trap)
        {
            var l = kart.transform.InverseTransformPoint(target.transform.position);
            var reach = trap ? DropReach : RearSightRange;
            return -l.z < reach && Mathf.Abs(l.x) < RearLineHalfWidth - RearLineSpread * l.z;
        }

        bool TryPlantLook(Kart kart, float dt)
        {
            plantCheckTimer -= dt;
            if (plantCheckTimer > 0f) return false;
            plantCheckTimer = PlantCheckInterval;
            var d = kart.Progress.PathDistance + board.direction * Mathf.Max(0f, kart.Physics.ForwardSpeed) * (Warning + PlantLookLead);
            if (!AIKartQueries.NarrowRoad(kart, board.route, d)) return false;
            Start(PlantLookDuration, true);
            return true;
        }

        void Schedule(bool rearItem, float dt)
        {
            var rearInterval = Mathf.Lerp(SlowRearInterval, FastRearInterval, difficulty.aiPrecision);
            if (rearItem) next = Mathf.Min(next, rearInterval * RearIntervalCap);
            next -= dt;
            if (next > 0f) return;
            next = rearItem ? rearInterval * rng.Range(MinRearJitter, MaxRearJitter) : rng.Range(MinCasualInterval, MaxCasualInterval);
            Start(rng.Range(MinDuration, MaxDuration), false);
        }

        void Start(float duration, bool plant)
        {
            Remaining = duration;
            Elapsed = 0f;
            PlantLook = plant;
        }

        static Kart NearestBehind(Kart kart)
        {
            Kart best = null;
            var bestDistance = RearSightRange;
            var tf = kart.transform;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (o == kart || o.Eliminated) continue;
                var l = tf.InverseTransformPoint(o.transform.position);
                l.y = 0f;
                var distance = l.magnitude;
                if (l.z >= -MinBehind || distance >= bestDistance || Vector3.Angle(Vector3.back, l) > RearFieldOfView * 0.5f) continue;
                best = o;
                bestDistance = distance;
            }
            return best;
        }
    }
}
