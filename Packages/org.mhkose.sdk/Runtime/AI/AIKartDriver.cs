using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Computer driver modelled on MHK2's ComputerCarController. It follows a racing line, plans lanes and braking, recovers
    /// when stuck and presses the player's keys through <see cref="AIKeys"/>, with item use delegated to <see cref="AIItemBrain"/>.
    /// In a battle it races the track the same way in the direction <see cref="AIBattleDriving"/> picks and leaves the
    /// racing line to hunt opponents, roam arenas and turn around.
    /// </summary>
    public sealed class AIKartDriver : IKartDriver
    {
        const int SeedSalt = 17, BattleSalt = 0x5BD1E995;
        const float NoiseSeedRange = 100f, FirstLaneMin = 0.4f, FirstLaneSpread = 0.2f, ScanSpread = 0.1f;
        const float MinRouteReach = 10f, RouteReachMargin = 10f;
        const float SlowScanInterval = 0.2f, FastScanInterval = 0.06f, MinScanRange = 10f;
        const float FeelerWidthShare = 1.15f, MinFeelerRadius = 0.4f, MaxFeelerRadius = 1.1f;
        const float MinKartHalfWidth = 0.3f, DefaultKartHalfWidth = 0.8f;
        const float MinSteerAhead = 3f, StuckAimAhead = 8f;
        const float CoastBrakeSpeed = 1f, CoastBrake = 0.2f;
        const float WallStopMargin = 2.5f, SqueezeSpeed = 3f;
        const float BrakeRampSpeed = 5f, HardBrakeExcess = 1.5f, LightBrake = 0.3f;
        const float YieldBrakeSpeed = 4f, YieldBrake = 0.6f;

        // One driver's step in the Profiler (and for ProfilerRecorder cost probes).
        [NoAutoStaticsCleanup] static readonly ProfilerMarker UpdateMarker = new("MHK.AIKartDriver.UpdateInput");

        readonly DifficultySettings difficulty;
        readonly AIBlackboard board = new();
        readonly AIKeys keys = new();
        readonly AIItemBrain items;
        readonly AIRouteFollower line;
        readonly AIFeelers feelers;
        readonly AILanePlanner lanes;
        readonly AISteering steering;
        readonly AICornerPlanner corners;
        readonly AIStuckRecovery stuck;
        readonly AIRamThreat rams;
        readonly AITurnLimit turn = new();
        float scanTimer;
        float halfWidth;
        int obstacleMask;
        float roadShift;
        readonly int battleSeed;
        AIBattleDriving battle;

        /// <summary>Item box this driver roams to in a battle arena, null for none (other drivers pick different ones).</summary>
        internal ItemBox BattleBox => battle?.Box;
        /// <summary>Opponent this driver hunts in a battle, null for none (other drivers spread over different targets).</summary>
        internal Kart BattleTarget => battle?.Target;

        /// <summary>
        /// A driver of the engine class <paramref name="difficulty"/>. <paramref name="seed"/> (the racer index) keeps the
        /// drivers of one race apart.
        /// </summary>
        public AIKartDriver(DifficultySettings difficulty, int seed)
        {
            this.difficulty = difficulty;
            // Mixed with the global random state so every race brings new item timing, rolls and wobble.
            var rng = new System.Random(unchecked(seed * AIRolls.KeyPrime + SeedSalt) ^ Random.Range(int.MinValue, int.MaxValue));
            var noiseSeed = rng.NextFloat() * NoiseSeedRange;
            var laneU = FirstLaneMin + rng.NextFloat() * FirstLaneSpread;
            items = new AIItemBrain(difficulty, board, rng);
            var rollSeed = rng.Next();
            battleSeed = rollSeed ^ BattleSalt;
            scanTimer = rng.NextFloat() * ScanSpread;
            rams = new AIRamThreat(difficulty, rollSeed);
            line = new AIRouteFollower(rollSeed, AIRacingLine.LineFor(seed));
            feelers = new AIFeelers(line);
            lanes = new AILanePlanner(difficulty, board, line, feelers, rams, rollSeed, noiseSeed, laneU);
            steering = new AISteering(line, turn);
            corners = new AICornerPlanner(line, turn);
            stuck = new AIStuckRecovery(line);
            // Racing lines are built on first use: this driver's while the race loads, not on its first step.
            var track = TrackRuntime.Current;
            if (track != null && track.HasMainPath) line.Prepare(track, GameRoot.Instance.Config);
        }

        public void UpdateInput(Kart kart, ref KartInput input)
        {
            var cfg = GameRoot.Instance.Config;
            var dt = Time.fixedDeltaTime;
            using var marker = UpdateMarker.Auto();
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
            EnsureBattle(cfg);
            if (battle != null)
            {
                battle.Think(kart, track, line, cfg, dt);
                line.Direction = battle.Direction;
                board.direction = battle.Direction;
            }
            FollowLine(kart, track, speed, cfg);
            if (kart.Progress.Finished)
            {
                steering.Steer(kart, steering.Ahead(speed), lanes.Lane, cfg, dt, ref input);
                input.brake = speed > CoastBrakeSpeed * s ? CoastBrake : 0f;
                return;
            }
            rams.Tick(kart, halfWidth);
            if (battle != null)
                stuck.SetBattleAim(battle.Navigating ? battle.SteerPoint : line.Point(StuckAimAhead, AILanes.Centre), !battle.Navigating || battle.ForwardSafe,
                    battle.Turning);
            if (stuck.Tick(kart, dt, cfg, ref input)) return;
            if (battle != null && battle.Navigating)
            {
                battle.Drive(kart, track, line, steering, turn, FeelerRadius, cfg, dt, ref input);
                items.Tick(kart, ref input, dt, cfg);
                return;
            }

            var steerAhead = steering.Ahead(speed);
            ScanIfDue(kart, speed, steerAhead, dt, cfg);
            var corner = corners.CornerSpeed(kart, speed, s, lanes.Lane);
            var clear = lanes.Plan(kart, speed, Mathf.Min(kart.NormalTopSpeed, corner), dt, roadShift, cfg);
            steering.Steer(kart, Mathf.Max(MinSteerAhead, steerAhead * feelers.AheadScale), lanes.Lane, cfg, dt, ref input);
            // Always full throttle (no speed cap below the player's): only bends, walls and blocked karts ahead brake.
            var throttle = 1f;
            ApplyBrakes(speed, BrakeSpeed(corner, s, clear, cfg), s, ref input, ref throttle);
            input.throttle = throttle;
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
            battle = new AIBattleDriving(difficulty, cfg.GetBattleAi(race.Session.difficulty), arena, board, rams, battleSeed);
        }

        void FollowLine(Kart kart, TrackRuntime track, float speed, GameConfig cfg)
        {
            var routeReach = Mathf.Max(MinRouteReach, speed * speed / (2f * Mathf.Max(1f, cfg.aiPlanBraking)) + RouteReachMargin);
            line.Tick(kart, track, routeReach, battle == null || battle.FollowsRoutes, cfg);
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
        }

        // corner: the bend planner's speed (AICornerPlanner.CornerSpeed).
        float BrakeSpeed(float corner, float s, float clear, GameConfig cfg)
        {
            var vmax = corner;
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

        static float KartHalfWidthOf(Kart kart) =>
            kart.Physics.TryGetComponent<BoxCollider>(out var box)
                ? Mathf.Max(MinKartHalfWidth, box.size.x * 0.5f * Mathf.Abs(box.transform.lossyScale.x))
                : DefaultKartHalfWidth;
    }
}
