using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Turning around in a battle, planned on the real ground instead of the track line's width: the arcs the kart drives
    /// at full lock forwards and backwards are swept against walls, solid bodies, kerbs and unsafe ground
    /// (<see cref="AIBattleArea"/>, the measured road's edges on circuits). Room for the whole turn: one forward U-turn.
    /// Too little: a three-point turn of forward and reverse legs, each as long as its arc is clear, re-planned from where
    /// the last one ended. A leg that stalls (a wall the sweeps missed, another kart) ends at once and the next one goes
    /// the other way (boxed in both ways: a short push where there was more room); a turn that does not come round
    /// within six legs or ten seconds fails and the kart respawns.
    /// </summary>
    internal sealed class AIBattleTurn
    {
        // A wanted direction more than StartAngle off the nose starts a turn, less than DoneAngle ends it.
        const float StartAngle = 110f, DoneAngle = 50f;
        // Arcs are swept in ArcStep (m) legs from the kart's leading end, up to MaxArcAngle of heading change, and stop
        // ArcMargin (m) short of what they meet. Legs shorter than MinLegAngle are not driven.
        const float ArcStep = 1f, ArcMargin = 1f, MaxArcAngle = 200f, MinLegAngle = 12f;
        // The driven arc is RadiusSlack wider than the full-lock radius (tyre slip); FallbackRadius (m) before the
        // wheelbase is measured. Leading end: LeadShare of the kart's half length ahead of (behind) its centre.
        const float RadiusSlack = 1.2f, FallbackRadius = 4f, LeadShare = 0.9f;
        const float GroundClearance = 0.25f, GroundProbeDepth = 2f;
        // A leg stalls when the kart moves slower than StallSpeed (m/s) for StallSeconds after its first LegSettle seconds.
        const float StallSpeed = 0.5f, StallSeconds = 0.35f, LegSettle = 0.5f;
        // A leg ends after its planned heading change or MaxLegSeconds, the whole turn fails after MaxLegs legs or MaxSeconds.
        const float MaxLegSeconds = 3f, MaxSeconds = 10f;
        const int MaxLegs = 6;
        // Faster than the turning speed times BrakeShare: brake (wheel towards the turn) before the first leg.
        // Legs coast above Coast times the turning speed (forward legs brake lightly with LightBrake).
        const float BrakeShare = 1.3f, Coast = 1.1f, LightBrake = 0.3f;
        // Moving more than this (m) in one step is a respawn: the turn starts afresh.
        const float TeleportDistance = 5f;

        enum Leg : byte { None, Brake, Forward, Reverse }

        readonly AIBattleArea.Hints hints = new();
        Leg leg;
        int side, legs;
        float legAngle, turned, legTime, stallTime, totalTime, lastYaw;
        Vector3 lastPos;

        /// <summary>A turn is under way: <see cref="Drive"/> sets the input.</summary>
        public bool Active => leg != Leg.None;

        /// <summary>Forgets a turn under way.</summary>
        public void Reset() => leg = Leg.None;

        /// <summary>
        /// Starts or continues turning the kart around towards <paramref name="wanted"/> (flat unit direction) and sets the
        /// input. False when no turn is needed (or it just ended): the normal driving steers. <paramref name="radius"/> is
        /// the feeler radius, <paramref name="leash"/> and <paramref name="pavedLeash"/> how far beyond the road edges
        /// the arcs may run, <paramref name="turnSpeed"/> the speed (m/s) forward legs are driven at.
        /// </summary>
        public bool Drive(Kart kart, TrackRuntime track, AITurnLimit limit, Vector3 wanted, float radius, float leash, float pavedLeash,
            float turnSpeed, GameConfig cfg, float dt, ref KartInput input)
        {
            var forward = Flat(kart.transform.forward);
            var angle = Vector3.SignedAngle(forward, wanted, Vector3.up);
            var pos = kart.transform.position;
            // A respawn (a jump in position) or a hit that takes the controls: the turn starts afresh afterwards.
            if (leg != Leg.None && ((pos - lastPos).sqrMagnitude > TeleportDistance * TeleportDistance || kart.Effects.ControlsLocked)) leg = Leg.None;
            lastPos = pos;
            if (kart.Effects.ControlsLocked) return false;
            if (leg == Leg.None)
            {
                if (Mathf.Abs(angle) <= StartAngle) return false;
                Begin(kart, angle);
            }
            else if (Mathf.Abs(angle) < DoneAngle)
            {
                leg = Leg.None;
                return false;
            }
            totalTime += dt;
            legTime += dt;
            var yaw = kart.transform.eulerAngles.y;
            turned += Mathf.DeltaAngle(lastYaw, yaw) * side;
            lastYaw = yaw;
            var speed = kart.Physics.ForwardSpeed;
            if (leg != Leg.Brake)
            {
                stallTime = legTime > LegSettle && Mathf.Abs(speed) < StallSpeed ? stallTime + dt : 0f;
                if (turned >= legAngle || stallTime > StallSeconds || legTime > MaxLegSeconds)
                    NextLeg(kart, track, limit, angle, radius, leash, pavedLeash, turnSpeed, cfg);
            }
            else if (speed <= turnSpeed * BrakeShare) NextLeg(kart, track, limit, angle, radius, leash, pavedLeash, turnSpeed, cfg);
            if (legs > MaxLegs || totalTime > MaxSeconds)
            {
                leg = Leg.None;
                kart.RequestRespawn();
                return false;
            }
            Press(speed, turnSpeed, ref input);
            return true;
        }

        void Begin(Kart kart, float angle)
        {
            side = angle >= 0f ? 1 : -1;
            legs = 0;
            totalTime = 0f;
            lastYaw = kart.transform.eulerAngles.y;
            leg = Leg.Brake;
            legTime = stallTime = turned = 0f;
            legAngle = 0f;
            hints.Reset();
        }

        // Plans the next leg from the kart's pose: forward all the way round when its arc is clear for that (either side),
        // else the longer of the forward and the reverse leg on the side the kart turns to (after a stalled or finished
        // leg the other gear first).
        void NextLeg(Kart kart, TrackRuntime track, AITurnLimit limit, float angle, float radius, float leash, float pavedLeash, float turnSpeed,
            GameConfig cfg)
        {
            var previous = leg;
            legs++;
            legTime = stallTime = turned = 0f;
            var r = (limit.Ready ? limit.LockRadius(turnSpeed) : FallbackRadius) * RadiusSlack;
            var need = Mathf.Abs(angle) - DoneAngle + MinLegAngle;
            var s = angle >= 0f ? 1 : -1;
            var ahead = Clear(kart, track, r, s, true, need, radius, leash, pavedLeash, cfg);
            if (previous == Leg.Brake && ahead < need)
            {
                // Before the first leg: the long way round is fine when only it has room.
                var needLong = 360f - Mathf.Abs(angle) - DoneAngle + MinLegAngle;
                var other = Clear(kart, track, r, -s, true, needLong, radius, leash, pavedLeash, cfg);
                if (other >= needLong)
                {
                    Start(Leg.Forward, -s, other);
                    return;
                }
            }
            if (ahead >= need)
            {
                Start(Leg.Forward, s, ahead);
                return;
            }
            var back = Clear(kart, track, r, s, false, need, radius, leash, pavedLeash, cfg);
            var forwardFirst = previous == Leg.Reverse ? ahead >= MinLegAngle : previous == Leg.Forward ? back < MinLegAngle : ahead >= back;
            if (forwardFirst && ahead >= MinLegAngle) Start(Leg.Forward, s, ahead);
            else if (back >= MinLegAngle) Start(Leg.Reverse, s, back);
            // Boxed in both ways: push whichever way had more room for a leg's time, the stall check ends it.
            else Start(previous == Leg.Forward || back > ahead ? Leg.Reverse : Leg.Forward, s, MinLegAngle);
        }

        void Start(Leg next, int turnSide, float angle)
        {
            leg = next;
            side = turnSide;
            legAngle = angle;
        }

        // Forward legs: wheel to the turning side at the turning speed. Reverse legs: wheel the other way (the nose still
        // swings to the turning side), the brake key reverses. Braking before the first leg keeps the wheel in the turn.
        void Press(float speed, float turnSpeed, ref KartInput input)
        {
            switch (leg)
            {
                case Leg.Brake:
                    input.steer = side;
                    input.throttle = 0f;
                    input.brake = 1f;
                    break;
                case Leg.Forward:
                    input.steer = side;
                    input.throttle = speed < turnSpeed ? 1f : 0f;
                    input.brake = speed > turnSpeed * Coast ? LightBrake : 0f;
                    break;
                default:
                    input.steer = -side;
                    input.throttle = 0f;
                    input.brake = speed < -turnSpeed * Coast ? 0f : 1f;
                    break;
            }
        }

        // Heading change (degrees, up to want) the kart can make on an arc of radius r turning to side s, forwards or
        // backwards, before its leading end meets a wall, a solid body, a kerb or unsafe ground.
        float Clear(Kart kart, TrackRuntime track, float r, int s, bool forwards, float want, float radius, float leash, float pavedLeash,
            GameConfig cfg)
        {
            var tf = kart.transform;
            var pos = tf.position;
            var groundY = pos.y;
            var lift = Vector3.up * (radius + GroundClearance);
            if (GroundProbe.Down(pos, 1f, GroundProbeDepth, track.ObstacleMask, out var ground)) groundY = ground.point.y;
            var f = Flat(tf.forward).normalized;
            var right = Vector3.Cross(Vector3.up, f);
            var centre = pos + right * (forwards ? s * r : -s * r);
            var lead = forwards ? f : -f;
            var half = HalfLength(kart) * LeadShare;
            var mask = track.ObstacleMask;
            var minNormalY = AIBattleArea.MinNormalY(cfg);
            var stepAngle = ArcStep / r * Mathf.Rad2Deg;
            var limit = Mathf.Min(want, MaxArcAngle);
            var from = Lead(pos, lead, half, groundY, lift);
            var y = groundY;
            var reach = 0f;
            for (var a = stepAngle; a <= limit + stepAngle; a += stepAngle)
            {
                var rot = Quaternion.AngleAxis(s * a, Vector3.up);
                var p = centre + rot * (pos - centre);
                var to = Lead(p, rot * lead, half, y, lift);
                var chord = to - from;
                var length = chord.magnitude;
                if (length > 1e-3f)
                {
                    var dir = chord / length;
                    var free = Mathf.Min(AIBattleNavigator.Sweep(from, dir, length, radius, mask),
                        AIFeelers.LowWall(from, dir, length, radius + GroundClearance, radius, mask));
                    if (free < length) return Margin(reach + free / r * Mathf.Rad2Deg, r);
                }
                if (!AIBattleArea.Safe(track, to, y, leash, pavedLeash, minNormalY, hints, out var next, out _)) return Margin(reach, r);
                y = next;
                to.y = y + lift.y;
                from = to;
                reach = a;
            }
            return Mathf.Min(reach, want);
        }

        static float Margin(float angle, float r) => Mathf.Max(0f, angle - ArcMargin / r * Mathf.Rad2Deg);

        static Vector3 Lead(Vector3 centre, Vector3 direction, float half, float groundY, Vector3 lift)
        {
            var p = centre + direction * half;
            return new Vector3(p.x, groundY, p.z) + lift;
        }

        static float HalfLength(Kart kart)
        {
            var box = kart.Physics.Chassis;
            return box != null ? box.size.z * 0.5f * Mathf.Abs(box.transform.lossyScale.z) : 1f;
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    }
}
