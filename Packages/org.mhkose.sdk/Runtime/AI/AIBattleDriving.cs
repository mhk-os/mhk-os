using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Battle part of a computer driver: <see cref="AIBattlePlanner"/> decides between racing the track (the race driving
    /// of <see cref="AIKartDriver"/> in <see cref="Direction"/>) and the modes that leave the racing line, which this drives:
    /// the way to the goal comes from <see cref="AIBattleNavigator"/>, with slowing for sharp turns and for walls or unsafe
    /// ground ahead and three-point turns when the nose is against a wall or an edge.
    /// </summary>
    internal sealed class AIBattleDriving
    {
        const float SharpTurn = 60f, GentleTurn = 25f, StopMargin = 2f, HardBrakeExcess = 1.2f, LightBrake = 0.3f, CrawlShare = 0.15f;
        const float TurnRoom = 5f, BackUpAngle = 45f, BackUpSpeed = 2f, BackUpSeconds = 0.8f, UTurn = 150f, UTurnShare = 0.5f;
        const float BackUpMinMove = 0.5f, PushSeconds = 1.5f, RollingBack = 1f;
        // Turning around heads for the line this far (m) along it in the new direction.
        const float TurnGoalAhead = 25f;

        readonly AIBattlePlanner planner;
        readonly AIBattleNavigator navigator;
        readonly bool arena;
        float backUpTimer, pushTimer;
        bool backingUp, navigating;
        Vector3 backUpStart;

        public AIBattleDriving(DifficultySettings difficulty, BattleAiSettings skill, bool arena, AIBlackboard board, int seed)
        {
            planner = new AIBattlePlanner(skill, board, arena, seed);
            navigator = new AIBattleNavigator(difficulty);
            this.arena = arena;
        }

        /// <summary>Off the racing line: this class drives (<see cref="Drive"/>), else the race driving does.</summary>
        public bool Navigating => planner.Mode != BattleMode.Race;
        /// <summary>1 along the track's direction, -1 against it.</summary>
        public int Direction => planner.Direction;
        /// <summary>Alternative routes are followed (circuits).</summary>
        public bool FollowsRoutes => !arena;
        /// <summary>Point the kart steers at while <see cref="Navigating"/> (also where the stuck recovery backs out towards).</summary>
        public Vector3 SteerPoint => navigator.SteerPoint;
        /// <summary>Item box the kart roams to, null for none (other drivers pick different ones).</summary>
        public ItemBox Box => planner.Box;
        /// <summary>Opponent hunted, null for none.</summary>
        public Kart Target => planner.Target;
        /// <summary>No drop, deadly or unsafe ground right ahead, so pushing forward out of a jam is safe.</summary>
        public bool ForwardSafe => navigator.GroundAhead > StopMargin;

        /// <summary>Decides mode, direction and goal for this physics step (before the line follower ticks).</summary>
        public void Think(Kart kart, TrackRuntime track, AIRouteFollower line, GameConfig cfg, float dt)
        {
            planner.Tick(kart, track, line, Leash(cfg), cfg, dt);
            if (Navigating == navigating) return;
            navigating = Navigating;
            if (!navigating) return;
            // Fresh from the racing line: no turn, back-up or blocked state left over from an earlier stretch off it.
            navigator.Reset(kart.transform.position);
            backUpTimer = pushTimer = 0f;
            backingUp = false;
        }

        /// <summary>Plans the way for this physics step while <see cref="Navigating"/> and sets steering, throttle and brake wishes.</summary>
        public void Drive(Kart kart, TrackRuntime track, AIRouteFollower line, AISteering steering, float radius, GameConfig cfg, float dt,
            ref KartInput input)
        {
            var turn = planner.Mode == BattleMode.Turn;
            var goal = turn ? line.Point(TurnGoalAhead, AILanes.Centre) : planner.Point;
            navigator.Tick(kart, planner.Target, track, goal, arena, Leash(cfg), radius, turn, cfg, dt);
            // Turning around takes the kart away from its goal first: no giving up meanwhile.
            planner.WatchProgress(navigator.Way, navigator.AlongLine, navigator.Turning ? 0f : dt);
            steering.SteerAt(kart, navigator.SteerPoint, cfg, dt, ref input);
            if (navigator.Jumping)
            {
                input.throttle = 1f;
                return;
            }
            var angle = TurnAngle(kart);
            if (BackingUp(kart, angle, dt, ref input)) return;
            var speed = kart.Physics.ForwardSpeed;
            // Walls ahead only slow the kart to a crawl so it can still turn away, drops and unsafe ground stop it.
            var vmax = Mathf.Min(TurnSpeed(kart, angle, cfg),
                Mathf.Max(CrawlShare * kart.NormalTopSpeed, StoppingSpeed(navigator.WallAhead, cfg)), StoppingSpeed(navigator.GroundAhead, cfg));
            input.throttle = speed < vmax ? 1f : 0f;
            if (speed > vmax) input.brake = speed > vmax * HardBrakeExcess ? 1f : LightBrake;
        }

        float Leash(GameConfig cfg) => arena ? cfg.aiBattleArenaLeash : cfg.aiBattleOffroadLeash;

        // Nose against a wall or an edge with the way out to the side or behind: reverse while turning (a three-point turn).
        // While the kart rolls backwards the hazard probe looks behind it, so an edge there ends the manoeuvre and no new one
        // starts before the kart drives forward again. A back-up that did not move the kart (blocked behind too, uphill) is
        // followed by pushing forward instead of backing up again.
        bool BackingUp(Kart kart, float angle, float dt, ref KartInput input)
        {
            backUpTimer -= dt;
            pushTimer -= dt;
            if (backUpTimer > 0f)
            {
                if (kart.Physics.ForwardSpeed < -RollingBack && navigator.GroundAhead < TurnRoom)
                {
                    backUpTimer = 0f;
                    backingUp = false;
                    return false;
                }
            }
            else
            {
                if (backingUp && (kart.transform.position - backUpStart).sqrMagnitude < BackUpMinMove * BackUpMinMove) pushTimer = PushSeconds;
                backingUp = false;
                var blocked = Mathf.Min(navigator.WallAhead, navigator.GroundAhead);
                var speed = kart.Physics.ForwardSpeed;
                if (pushTimer > 0f || speed < -RollingBack || Mathf.Abs(speed) > BackUpSpeed || blocked > TurnRoom || (angle < BackUpAngle && blocked > StopMargin))
                    return false;
                backUpTimer = BackUpSeconds;
                backingUp = true;
                backUpStart = kart.transform.position;
            }
            var local = kart.transform.InverseTransformPoint(navigator.SteerPoint);
            input.steer = local.x > 0f ? -1f : 1f;
            input.throttle = 0f;
            input.brake = 1f;
            return true;
        }

        float TurnAngle(Kart kart)
        {
            var to = navigator.SteerPoint - kart.transform.position;
            var forward = kart.transform.forward;
            return Vector3.Angle(new Vector3(forward.x, 0f, forward.z), new Vector3(to.x, 0f, to.z));
        }

        static float StoppingSpeed(float distance, GameConfig cfg) =>
            float.IsPositiveInfinity(distance) ? float.PositiveInfinity : Mathf.Sqrt(2f * cfg.aiPlanBraking * Mathf.Max(0f, distance - StopMargin));

        // Full speed, slower the sharper the turn to the steering point, slowest for turning around so the kart turns on the
        // spot instead of running wide into walls.
        static float TurnSpeed(Kart kart, float angle, GameConfig cfg)
        {
            var t = Mathf.InverseLerp(GentleTurn, SharpTurn, angle);
            if (t <= 0f) return float.PositiveInfinity;
            var top = kart.NormalTopSpeed;
            var turn = cfg.aiBattleTurnSpeedShare * top * Mathf.Lerp(1f, UTurnShare, Mathf.InverseLerp(SharpTurn, UTurn, angle));
            return Mathf.Lerp(top, turn, t);
        }
    }
}
