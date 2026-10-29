using MHK.Core;
using MHK.Karts;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Stuck recovery of a computer driver: MHK2's reverse manoeuvre (reverse, then a drive-off window before a respawn) and
    /// a progress watchdog that respawns a kart whose race distance (in a battle: position) stalls or that is far off every route.
    /// </summary>
    internal sealed class AIStuckRecovery
    {
        // Movement within one physics step (m) that only a teleport (respawn) produces.
        const float TeleportDistance = 5f;
        const float StallWindow = 1f, BattleStallWindow = 0.6f, StallDistance = 1f;
        const float ReverseTime = 1f, MinReverse = 0.5f, ReverseTargetAhead = 8f;
        const float DriveOffTime = 2f, MinDriveOff = 6f;

        enum Mode { Watch, Reverse, DriveOff, PushForward }

        readonly AIRouteFollower line;
        Mode mode;
        float timer;
        float windowDistance;
        float reverseDistance;
        Vector3 lastPos;
        bool hasLastPos;
        float progressMark;
        float progressTimer;
        float offRouteTimer;
        float lastRaceDistance;
        bool battle, battlePushSafe, battleTurning;
        Vector3 battleAim;
        float roamed;
        // Battle: reverse manoeuvres since the kart last roamed aiStuckMinProgress. Every second one steers the other
        // way, so a kart wedged offroad (a wall ahead, a slope or a prop beside it) does not repeat the same failed turn.
        int battleStreak;

        public AIStuckRecovery(AIRouteFollower line) => this.line = line;

        /// <summary>
        /// Battle: the kart drives the track both ways and leaves the racing line, so progress is the way it drives (no
        /// race distance or off-route watch, no respawn after the reverse manoeuvre), and reversing turns its nose towards
        /// <paramref name="aim"/> (the racing line ahead in its direction, or its goal off the line). A reverse that does
        /// not move the kart is followed by pushing forward when <paramref name="forwardSafe"/>. A kart that keeps getting
        /// stuck without roaming on alternates the side it turns to. <paramref name="turning"/>: the kart turns around
        /// (<see cref="AIBattleTurn"/>, which handles its own stalls and failure), so neither watch runs.
        /// </summary>
        public void SetBattleAim(Vector3 aim, bool forwardSafe, bool turning)
        {
            battle = true;
            battleAim = aim;
            battlePushSafe = forwardSafe;
            battleTurning = turning;
        }

        /// <summary>Runs the watch for this physics step. True while reversing: the input is set and the driving skips this step.</summary>
        public bool Tick(Kart kart, float dt, GameConfig cfg, ref KartInput input)
        {
            var pos = kart.transform.position;
            if (!hasLastPos) { lastPos = pos; hasLastPos = true; }
            var moved = Vector3.Distance(pos, lastPos);
            lastPos = pos;
            var prog = kart.Progress;
            if (!kart.ControlsEnabled || kart.Eliminated || kart.Effects.ControlsLocked || kart.Effects.HasRespawnPending
                || MagnetPowerup.Holds(kart) || (battle && battleTurning))
            {
                ResetWindow(Mode.Watch);
                ResetProgressWatch(prog);
                roamed = 0f;
                return false;
            }
            if (battle ? WatchRoaming(kart, dt, moved, cfg) : WatchProgress(kart, prog, dt, moved, cfg)) return false;
            timer += dt;
            windowDistance += moved;
            return mode switch
            {
                Mode.Watch => CheckStall(),
                Mode.Reverse => Reverse(kart, moved, ref input),
                Mode.PushForward => PushForward(kart, ref input),
                _ => CheckDriveOff(kart),
            };
        }

        bool CheckStall()
        {
            if (timer >= (battle ? BattleStallWindow : StallWindow))
            {
                if (windowDistance < StallDistance)
                {
                    reverseDistance = 0f;
                    if (battle) battleStreak++;
                    ResetWindow(Mode.Reverse);
                }
                else ResetWindow(Mode.Watch);
            }
            return false;
        }

        bool Reverse(Kart kart, float moved, ref KartInput input)
        {
            reverseDistance += moved;
            input.brake = 1f;
            input.throttle = 0f;
            var local = kart.transform.InverseTransformPoint(battle ? battleAim : line.Point(ReverseTargetAhead, AILanes.Centre));
            input.steer = (local.x > 0 ? -1f : 1f) * BattleSide;
            if (timer >= ReverseTime) ResetWindow(reverseDistance > MinReverse ? Mode.DriveOff : battle && battlePushSafe ? Mode.PushForward : Mode.Watch);
            return true;
        }

        bool PushForward(Kart kart, ref KartInput input)
        {
            input.brake = 0f;
            input.throttle = 1f;
            input.steer = (kart.transform.InverseTransformPoint(battleAim).x > 0 ? 1f : -1f) * BattleSide;
            if (timer >= ReverseTime) ResetWindow(Mode.DriveOff);
            return true;
        }

        // -1 on every second reverse manoeuvre of a battle streak, else 1.
        float BattleSide => battle && battleStreak % 2 == 0 && battleStreak > 0 ? -1f : 1f;

        bool CheckDriveOff(Kart kart)
        {
            if (timer >= DriveOffTime)
            {
                if (!battle && windowDistance < Mathf.Max(MinDriveOff, 2f * reverseDistance)) kart.RequestRespawn();
                ResetWindow(Mode.Watch);
            }
            return false;
        }

        void ResetWindow(Mode next)
        {
            mode = next;
            timer = 0f;
            windowDistance = 0f;
        }

        void ResetProgressWatch(RacerProgress prog)
        {
            progressMark = lastRaceDistance = prog.RaceDistance;
            progressTimer = 0f;
            offRouteTimer = 0f;
        }

        // Respawns when the race distance has not grown by aiStuckMinProgress beyond the farthest point reached, or the kart
        // has been more than aiOffRouteDistance off every route, for stuckRespawnSeconds. Never during the reverse manoeuvre.
        bool WatchProgress(Kart kart, RacerProgress prog, float dt, float moved, GameConfig cfg)
        {
            var distance = prog.RaceDistance;
            // Only a real teleport restarts the watch from a lower race distance (not a projection switching lines).
            if (moved > TeleportDistance && distance < lastRaceDistance) progressMark = distance;
            lastRaceDistance = distance;
            if (distance - progressMark >= cfg.aiStuckMinProgress)
            {
                progressMark = distance;
                progressTimer = 0f;
            }
            else progressTimer += dt;
            offRouteTimer = prog.OffRouteDistance > cfg.aiOffRouteDistance ? offRouteTimer + dt : 0f;
            if (mode != Mode.Watch) return false;
            if (progressTimer < cfg.stuckRespawnSeconds && offRouteTimer < cfg.stuckRespawnSeconds) return false;
            kart.RequestRespawn();
            ResetWindow(Mode.Watch);
            ResetProgressWatch(prog);
            return true;
        }

        // Battle: respawns when the kart has driven less than aiStuckMinProgress within stuckRespawnSeconds. Karts circle
        // and fight on the spot in a battle, so this counts the way driven, not the distance from a spot.
        bool WatchRoaming(Kart kart, float dt, float moved, GameConfig cfg)
        {
            roamed += moved > TeleportDistance ? cfg.aiStuckMinProgress : moved;
            if (roamed >= cfg.aiStuckMinProgress)
            {
                roamed = 0f;
                progressTimer = 0f;
                battleStreak = 0;
            }
            else progressTimer += dt;
            if (mode != Mode.Watch || progressTimer < cfg.stuckRespawnSeconds) return false;
            kart.RequestRespawn();
            ResetWindow(Mode.Watch);
            progressTimer = 0f;
            roamed = 0f;
            battleStreak = 0;
            return true;
        }
    }
}
