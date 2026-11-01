using MHK.Karts;
using MHK.Powerups;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Steering of a computer driver while it tunnels with Hank's molehill (<see cref="MolehillPowerup"/>). Underground the
    /// keys turn the dig at a rate, not the wheel through the kart's steering geometry (its physics is suspended), so it
    /// pursues a point on its lane ahead, or on the lane of an opponent the head still catches (the mound launches karts
    /// it passes under), and asks for the share of the dig's turn rate that arc needs. Full throttle, so it pops out driving.
    /// </summary>
    internal sealed class AIDigSteering
    {
        // Look-ahead: this many seconds at the dig's speed, within MinAhead and MaxAhead (m).
        const float AheadSeconds = 0.45f, MinAhead = 6f, MaxAhead = 24f;
        // An opponent is gone for while the head catches it within this share of the dig left.
        const float ChaseShare = 0.9f;
        const float MinReach = 1f;

        readonly AIRouteFollower line;

        public AIDigSteering(AIRouteFollower line) => this.line = line;

        /// <summary>Steers <paramref name="dig"/> along lane <paramref name="laneU"/> of the followed line, or onto an opponent ahead.</summary>
        public void Steer(Kart kart, MolehillPowerup dig, float laneU, ref KartInput input)
        {
            var ahead = Mathf.Clamp(dig.Speed * AheadSeconds, MinAhead, MaxAhead);
            var target = AIItemJudge.DigTarget(kart, dig.Remaining * ChaseShare, dig.Speed, line.Direction, out var caught);
            if (target != null && line.TryLaneOf(target, out var lane, out _))
            {
                laneU = lane;
                ahead = Mathf.Min(ahead, caught);
            }
            SteerAt(dig, line.Point(ahead, laneU), ref input);
        }

        /// <summary>Steers <paramref name="dig"/> towards the world point <paramref name="target"/> (battles, off the racing line).</summary>
        public void SteerAt(MolehillPowerup dig, Vector3 target, ref KartInput input)
        {
            var to = target - dig.Head;
            to.y = 0f;
            var reach = Mathf.Max(MinReach, to.magnitude);
            // Behind the head: as far as it can turn (sin falls off past 90°).
            var alpha = Mathf.Clamp(Vector3.SignedAngle(dig.Heading, to, Vector3.up), -90f, 90f) * Mathf.Deg2Rad;
            // Pure pursuit: the arc through the target, as a turn rate at the dig's speed.
            var yawRate = dig.Speed * 2f * Mathf.Sin(alpha) / reach * Mathf.Rad2Deg;
            input.steer = dig.TurnRate > 0f ? Mathf.Clamp(yawRate / dig.TurnRate, -1f, 1f) : 0f;
            input.throttle = 1f;
            input.brake = 0f;
        }
    }
}
