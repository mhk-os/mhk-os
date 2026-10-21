using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Computer driver modelled on MHK2's ComputerCarController. It follows a racing line, plans lanes and braking, recovers
    /// when stuck and presses the player's keys through <see cref="AIKeys"/>, with item use delegated to <see cref="AIItemBrain"/>.
    /// In a battle it leaves the racing line for battle goals (<see cref="AIBattleDriving"/>).
    /// </summary>
    public sealed class AIKartDriver : IKartDriver
    {
        const int SeedSalt = 17, BattleSalt = 0x5BD1E995;
        const float NoiseSeedRange = 100f, FirstLaneMin = 0.4f, FirstLaneSpread = 0.2f, ScanSpread = 0.1f;
        const float MinRouteReach = 10f, RouteReachMargin = 10f;
        const float SlowScanInterval = 0.2f, FastScanInterval = 0.06f, MinScanRange = 10f;
        const float FeelerWidthShare = 1.15f, MinFeelerRadius = 0.4f, MaxFeelerRadius = 1.1f;
        const float MinKartHalfWidth = 0.3f, DefaultKartHalfWidth = 0.8f;
        const float MinSteerAhead = 3f;
        const float CoastBrakeSpeed = 1f, CoastBrake = 0.2f;
        const float WallStopMargin = 2.5f, SqueezeSpeed = 3f;
        const float BrakeRampSpeed = 5f, HardBrakeExcess = 1.5f, LightBrake = 0.3f;
        const float YieldBrakeSpeed = 4f, YieldBrake = 0.6f;

        readonly DifficultySettings difficulty;
        readonly TrackAiSettings levelAi;
        readonly float rankFactor;
        readonly AIBlackboard board = new();
        readonly AIKeys keys = new();
        readonly AIItemBrain items;
        readonly AIRouteFollower line;
        readonly AIFeelers feelers;
        readonly AILanePlanner lanes;
        readonly AISteering steering;
        readonly AICornerPlanner corners;
        readonly AIStuckRecovery stuck;
        float scanTimer;
        bool gapAhead;
        float halfWidth;
        int obstacleMask;
        float roadShift;
        readonly int battleSeed;
        AIBattleDriving battle;

        /// <summary>Item box station this driver heads for in a battle, -1 for none (other drivers pick different ones).</summary>
        internal int BattleStation => battle != null ? battle.Station : -1;

        /// <summary>
        /// A driver aiming for <paramref name="targetRank"/> on a track with <paramref name="levelAi"/> settings.
        /// <paramref name="seed"/> (the racer index) keeps the drivers of one race apart.
        /// </summary>
        public AIKartDriver(DifficultySettings difficulty, TrackAiSettings levelAi, int targetRank, int seed)
        {
            this.difficulty = difficulty;
            this.levelAi = levelAi ?? new TrackAiSettings();
            rankFactor = RankFactor(this.levelAi, targetRank);
            // Mixed with the global random state so every race brings new item timing, rolls and wobble.
            var rng = new System.Random(unchecked(seed * AIRolls.KeyPrime + SeedSalt) ^ Random.Range(int.MinValue, int.MaxValue));
            var noiseSeed = rng.NextFloat() * NoiseSeedRange;
            var laneU = FirstLaneMin + rng.NextFloat() * FirstLaneSpread;
            items = new AIItemBrain(difficulty, board, rng);
            var rollSeed = rng.Next();
            battleSeed = rollSeed ^ BattleSalt;
            scanTimer = rng.NextFloat() * ScanSpread;
            line = new AIRouteFollower(difficulty, rollSeed);
            feelers = new AIFeelers(line);
            lanes = new AILanePlanner(difficulty, board, line, feelers, rollSeed, noiseSeed, laneU);
            steering = new AISteering(line);
            corners = new AICornerPlanner(line);
            stuck = new AIStuckRecovery(line);
        }

        public void UpdateInput(Kart kart, ref KartInput input)
        {
            var cfg = GameRoot.Instance.Config;
            var dt = Time.fixedDeltaTime;
            Decide(kart, ref input, cfg, dt);
            keys.Press(kart.Physics, ref input, dt, cfg.aiKeyMinHold);
        }

        float FeelerRadius => Mathf.Clamp(halfWidth * FeelerWidthShare, MinFeelerRadius, MaxFeelerRadius);

        void Decide(Kart kart, ref KartInput input, GameConfig cfg, float dt)
        {
            var track = TrackRuntime.Current;
            input = default;
            if (track == null || !track.HasMainPath) return;
            lanes.Advance(dt);
            var speed = kart.Physics.ForwardSpeed;
            // MHK2 speed constants are in original m/s: times s for the scaled karts (KartPhysics.SpeedScale).
            var s = kart.Physics.SpeedScale;
            EnsureKartMetrics(kart, track);
            FollowLine(kart, track, speed, cfg);
            if (kart.Progress.Finished)
            {
                steering.Steer(kart, steering.Ahead(speed), lanes.Lane, cfg, dt, ref input);
                input.brake = speed > CoastBrakeSpeed * s ? CoastBrake : 0f;
                return;
            }
            EnsureBattle(cfg);
            if (battle != null) stuck.SetBattleAim(battle.SteerPoint, battle.ForwardSafe);
            if (stuck.Tick(kart, dt, cfg, ref input)) return;
            if (battle != null)
            {
                battle.Drive(kart, track, steering, FeelerRadius, cfg, dt, ref input);
                items.Tick(kart, ref input, dt, cfg);
                return;
            }

            var steerAhead = steering.Ahead(speed);
            ScanIfDue(kart, speed, steerAhead, dt, cfg);
            var clear = lanes.Plan(kart, speed, Kart.All.Count > 1 ? FreeSpeed(kart, speed, s, cfg) : 0f, dt, roadShift, cfg);
            steering.Steer(kart, Mathf.Max(MinSteerAhead, steerAhead * feelers.AheadScale), lanes.Lane, cfg, dt, ref input);
            var throttle = SpeedCapThrottle(kart, speed);
            // Run-up to a jump: full throttle. The level speed factors leave too little take-off speed at the original 2 g.
            if (gapAhead) throttle = 1f;
            ApplyBrakes(speed, BrakeSpeed(kart, speed, s, clear, cfg), s, ref input, ref throttle);
            input.throttle = Mathf.Clamp01(throttle);
            items.Tick(kart, ref input, dt, cfg);
        }

        void EnsureKartMetrics(Kart kart, TrackRuntime track)
        {
            if (halfWidth <= 0f)
            {
                halfWidth = KartHalfWidthOf(kart);
                lanes.KartHalfWidth = halfWidth;
                obstacleMask = track.ObstacleMask;
            }
        }

        void EnsureBattle(GameConfig cfg)
        {
            if (battle != null) return;
            var race = RaceManager.Current;
            if (race == null || !race.IsBattle) return;
            var arena = race.Track != null && race.Track.layout == TrackLayout.Arena;
            battle = new AIBattleDriving(difficulty, cfg.GetBattleAi(race.Session.difficulty), levelAi, arena, board, battleSeed);
        }

        void FollowLine(Kart kart, TrackRuntime track, float speed, GameConfig cfg)
        {
            var race = RaceManager.Current;
            var routeReach = Mathf.Max(MinRouteReach, speed * speed / (2f * Mathf.Max(1f, cfg.aiPlanBraking)) + RouteReachMargin);
            line.Tick(kart, track, routeReach, race == null || !race.IsBattle, cfg);
            board.route = line.Route;
            roadShift = line.RoadShift(0f);
            corners.Refresh(kart, difficulty, cfg);
        }

        void ScanIfDue(Kart kart, float speed, float steerAhead, float dt, GameConfig cfg)
        {
            scanTimer -= dt;
            if (scanTimer > 0f) return;
            scanTimer = Mathf.Lerp(SlowScanInterval, FastScanInterval, difficulty.aiPrecision);
            var range = Mathf.Clamp(Mathf.Abs(speed) * cfg.aiObstacleLookSeconds, MinScanRange, Mathf.Max(MinScanRange, cfg.aiLookAheadRange));
            feelers.Scan(kart.transform, range, steerAhead, lanes.Lane, FeelerRadius, obstacleMask, cfg.aiPushableMass);
            gapAhead = line.GapWithin(cfg.gapRunUp);
        }

        // MHK2's level speed factor, rubber-banded by the distance to the human, times the rank and engine-class factors.
        // With keys it is a speed cap: full throttle below that share of the kart's top speed, none above.
        float SpeedCapThrottle(Kart kart, float speed)
        {
            var factor = RubberBand(kart) * rankFactor * difficulty.aiSpeedFactor;
            return factor >= 1f || speed < factor * kart.NormalTopSpeed ? 1f : 0f;
        }

        float FreeSpeed(Kart kart, float speed, float s, GameConfig cfg) =>
            Mathf.Min(Mathf.Min(RubberBand(kart) * rankFactor * difficulty.aiSpeedFactor, 1f) * kart.NormalTopSpeed,
                corners.CornerSpeed(kart, speed, s, lanes.Lane, cfg));

        float RubberBand(Kart kart)
        {
            var f = levelAi.normalSpeedFactor;
            var gap = AIKartQueries.DistanceToHuman(kart);
            if (float.IsNaN(gap)) return f;
            if (gap > 0f)
                return f + (levelAi.leadingSpeedFactor - levelAi.normalSpeedFactor) * Mathf.Min(gap / Mathf.Max(1f, levelAi.leadingDistance), 1f);
            return f + (levelAi.trailingSpeedFactor - levelAi.normalSpeedFactor) * Mathf.Min(-gap / Mathf.Max(1f, levelAi.trailingDistance), 1f);
        }

        float BrakeSpeed(Kart kart, float speed, float s, float clear, GameConfig cfg)
        {
            var vmax = corners.CornerSpeed(kart, speed, s, lanes.Lane, cfg);
            // Walls or solid bodies block every lane ahead: slow enough to stop or squeeze past. Loose props are not braked for.
            if (clear < feelers.Range - 0.01f)
                vmax = Mathf.Min(vmax, Mathf.Sqrt(2f * cfg.aiPlanBraking * Mathf.Max(0f, clear - WallStopMargin)) + SqueezeSpeed * s);
            if (lanes.FollowSpeed < vmax) vmax = lanes.FollowSpeed;
            return vmax;
        }

        void ApplyBrakes(float speed, float vmax, float s, ref KartInput input, ref float throttle)
        {
            if (speed > vmax)
            {
                input.brake = Mathf.Min((speed - vmax * 0.5f) / (BrakeRampSpeed * s), 1f) * (speed > vmax + HardBrakeExcess * s ? 1f : LightBrake);
                throttle = 0f;
            }
            if (!board.yieldRequested) return;
            throttle = 0f;
            input.brake = Mathf.Max(input.brake, speed > YieldBrakeSpeed * s ? YieldBrake : 0f);
        }

        static float RankFactor(TrackAiSettings ai, int targetRank)
        {
            var ranks = ai.rankFactors;
            var f = ranks != null && ranks.Length > 0 ? ranks[Mathf.Clamp(targetRank, 0, ranks.Length - 1)] : 1f;
            return float.IsNaN(f) || f <= 0f ? 1f : f;
        }

        static float KartHalfWidthOf(Kart kart) =>
            kart.Physics.TryGetComponent<BoxCollider>(out var box)
                ? Mathf.Max(MinKartHalfWidth, box.size.x * 0.5f * Mathf.Abs(box.transform.lossyScale.x))
                : DefaultKartHalfWidth;
    }
}
