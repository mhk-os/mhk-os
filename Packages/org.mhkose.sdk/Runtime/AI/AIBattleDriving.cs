using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Driving of a computer driver in a battle: leaves the racing line for the goals of <see cref="AIBattlePlanner"/>,
    /// finds the way with <see cref="AIBattleNavigator"/> and slows for sharp turns and for walls or unsafe ground ahead.
    /// </summary>
    internal sealed class AIBattleDriving
    {
        const float SharpTurn = 60f, GentleTurn = 25f, StopMargin = 2f, HardBrakeExcess = 1.2f, LightBrake = 0.3f, CrawlShare = 0.15f;
        const float TurnRoom = 5f, BackUpAngle = 45f, BackUpSpeed = 2f, BackUpSeconds = 0.8f, UTurn = 150f, UTurnShare = 0.5f;
        const float BackUpMinMove = 0.5f, PushSeconds = 1.5f, RollingBack = 1f;

        readonly AIBattlePlanner planner;
        readonly AIBattleNavigator navigator;
        readonly float speedFactor;
        readonly bool arena;
        float backUpTimer, pushTimer;
        bool backingUp;
        Vector3 backUpStart;

        public AIBattleDriving(DifficultySettings difficulty, BattleAiSettings skill, TrackAiSettings levelAi, bool arena,
            AIBlackboard board, int seed)
        {
            planner = new AIBattlePlanner(skill, board, new System.Random(seed));
            navigator = new AIBattleNavigator(difficulty);
            speedFactor = levelAi.normalSpeedFactor * difficulty.aiSpeedFactor;
            this.arena = arena;
        }

        /// <summary>Point the kart steers at (also where the stuck recovery backs out towards).</summary>
        public Vector3 SteerPoint => navigator.SteerPoint;
        public BattleGoal Goal => planner.Goal;
        /// <summary>Item box station the kart heads for, -1 for none.</summary>
        public int Station => planner.Station;
        /// <summary>No drop, deadly or unsafe ground right ahead, so pushing forward out of a jam is safe.</summary>
        public bool ForwardSafe => navigator.GroundAhead > StopMargin;

        /// <summary>Plans goal and way for this physics step and sets steering, throttle and brake wishes.</summary>
        public void Drive(Kart kart, TrackRuntime track, AISteering steering, float radius, GameConfig cfg, float dt, ref KartInput input)
        {
            var leash = arena ? cfg.aiBattleArenaLeash : cfg.aiBattleOffroadLeash;
            planner.Tick(kart, track, arena, leash, cfg, dt);
            navigator.Tick(kart, planner.Goal == BattleGoal.Attack ? planner.Opponent : null, track, planner.Point, arena, leash, radius, cfg, dt);
            planner.WatchProgress(navigator.Way, navigator.AlongLine, dt);
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

        // Cruise at the class's share of the top speed (no limit from 1 on), slower the sharper the turn to the steering point,
        // slowest for turning around so the kart turns on the spot instead of running wide into walls.
        float TurnSpeed(Kart kart, float angle, GameConfig cfg)
        {
            var top = kart.NormalTopSpeed;
            var cruise = speedFactor >= 1f ? float.PositiveInfinity : speedFactor * top;
            var t = Mathf.InverseLerp(GentleTurn, SharpTurn, angle);
            if (t <= 0f) return cruise;
            var turn = cfg.aiBattleTurnSpeedShare * top * Mathf.Lerp(1f, UTurnShare, Mathf.InverseLerp(SharpTurn, UTurn, angle));
            return Mathf.Lerp(Mathf.Min(cruise, top), Mathf.Min(cruise, turn), t);
        }
    }
}
