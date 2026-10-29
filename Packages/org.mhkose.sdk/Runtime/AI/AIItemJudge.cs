using MHK.Core;
using MHK.Karts;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Aimed item use of a computer driver: whether firing the held item now pays off, by what the item does. Ranged
    /// items fire only at a kart in their line of fire and ask the driving to line one up otherwise.
    /// </summary>
    internal sealed class AIItemJudge
    {
        const float HomingFireAnywayTime = 12f, HomingShotMinRange = 30f, HomingShotReachShare = 1.5f;
        const float MeleeReachShare = 0.9f;
        const float MinShotAlong = 2f, AimCorridor = 10f;
        const float ShotFallbackTime = 25f, AimTargetAfterFallback = 60f;
        const float MinShotSpeed = 0.01f, MinShotFlatLength = 0.2f;
        const float ShotTangentSmoothing = 3f, MinShotTangentSqr = 0.5f;
        const float LobWindowFar = 12f, LobWindowNear = 1f;
        const float BlastMinAlong = 3f, ThrowWindow = 20f, ThrowShortfall = 3f, ThrowMaxOff = 2f;
        const float LeaderHoldTime = 30f, BoostHoldTime = 20f, BoostThreatDistance = 25f;
        const float RamRange = 20f, RamHalfWidth = 6f, ShieldRadius = 15f, ShieldHoldTime = 15f;
        const float MinDigCheck = 5f, DigCheckShare = 0.5f, DigSampleStep = 2f;

        readonly DifficultySettings difficulty;
        readonly AIBlackboard board;
        readonly AIMissileThreat missile;
        Kart aimTarget;
        float aimLane;

        public AIItemJudge(DifficultySettings difficulty, AIBlackboard board, AIMissileThreat missile)
        {
            this.difficulty = difficulty;
            this.board = board;
            this.missile = missile;
        }

        public void ClearAim() => aimTarget = null;

        /// <summary>Tells the driving whether and where to line the kart up for a shot.</summary>
        public void PublishAim()
        {
            board.aimRequested = aimTarget != null;
            board.aimLaneU = aimLane;
            board.aimTarget = aimTarget;
        }

        /// <summary>Firing <paramref name="item"/> now pays off. May ask the driving to line up a shot.</summary>
        public bool WorthFiring(Kart kart, AIItemProfile item, float heldTime, float steer) => item.Kind switch
        {
            AIItemKind.HomingMissile => HomingMissileWorth(kart, heldTime),
            AIItemKind.HomingShot => HomingShotWorth(kart, item),
            AIItemKind.StraightShot or AIItemKind.GroundShot or AIItemKind.Ball => AimShot(kart, item, heldTime),
            AIItemKind.Lob => LobWillLand(kart, item, board.direction),
            AIItemKind.Melee or AIItemKind.Area => AIKartQueries.CountAround(kart, item.Reach * MeleeReachShare) > 0,
            AIItemKind.Targeted => TargetedWorth(kart, item),
            // Dropped behind after a look back. Throwable traps also go in front of a kart ahead.
            AIItemKind.Trap => item.ForwardThrowRange > 0f && ThrowLandsBeforeKart(kart, item),
            AIItemKind.Self => SelfWorth(kart, item, heldTime, steer),
            AIItemKind.Dig => DigClear(kart, item, board.direction),
            _ => false,
        };

        static bool HomingMissileWorth(Kart kart, float heldTime) =>
            !AIKartQueries.IsLeading(kart) && (heldTime > HomingFireAnywayTime || !AIKartQueries.LeaderProtected(kart));

        static bool HomingShotWorth(Kart kart, AIItemProfile item)
        {
            var t = AIKartQueries.FirstTarget(kart, item.Selection, 0f);
            return t != null && !t.Effects.IsProtected
                && AIKartQueries.Distance(kart, t) < Mathf.Max(HomingShotMinRange, item.Reach * HomingShotReachShare);
        }

        bool AimShot(Kart kart, AIItemProfile item, float heldTime)
        {
            var dir = ShotDirection(kart, item, out var climb);
            var shotSpeed = item.Speed;
            if (item.MinSpeedOverOwner >= 0f)
                shotSpeed = Mathf.Max(shotSpeed, Vector3.Dot(kart.Physics.Body.linearVelocity, dir) + item.MinSpeedOverOwner);
            FindShotTargets(kart, item, dir, climb, shotSpeed, out var lined, out var candidate, out var candidateAlong);
            var los = item.NeedsLineOfSight;
            if (lined != null && (!los || AIKartQueries.LineOfSight(kart, lined))) return true;
            if (candidate == null || (los && !AIKartQueries.LineOfSight(kart, candidate))) return false;
            RequestLineUp(kart, candidate);
            // Held for long without a clean shot: fire at it anyway (the originals fire blind).
            return heldTime > ShotFallbackTime && candidateAlong < AimTargetAfterFallback;
        }

        static void FindShotTargets(Kart kart, AIItemProfile item, Vector3 dir, float climb, float shotSpeed, out Kart lined,
            out Kart candidate, out float candidateAlong)
        {
            lined = candidate = null;
            var linedAlong = candidateAlong = float.MaxValue;
            var origin = kart.transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Effects.IsProtected) continue;
                var to = o.transform.position - origin;
                var along = Vector3.Dot(to, dir);
                if (Mathf.Abs(to.y - climb * along) > AILanes.SameLevelHeight) continue;
                if (along < MinShotAlong || along > item.Range) continue;
                var predicted = to + o.Physics.Body.linearVelocity * FlightTime(item, shotSpeed, along);
                var off = predicted - dir * Vector3.Dot(predicted, dir);
                off.y = 0f;
                var lateral = off.magnitude;
                if (lateral <= item.LateralTolerance)
                {
                    if (along < linedAlong) { lined = o; linedAlong = along; }
                }
                else if (lateral < AimCorridor && along < candidateAlong && o.Progress.CurrentBranch == null)
                {
                    candidate = o;
                    candidateAlong = along;
                }
            }
        }

        void RequestLineUp(Kart kart, Kart candidate)
        {
            if (board.route != null || kart.Progress.CurrentBranch != null) return;
            aimTarget = candidate;
            aimLane = AIKartQueries.RoadLaneOf(candidate);
        }

        static Vector3 ShotDirection(Kart kart, AIItemProfile item, out float climb)
        {
            var dir = kart.transform.forward;
            if (item.AlongTrack)
            {
                var path = AIKartQueries.MainPath();
                if (path != null)
                {
                    var tangent = path.GetTangent(kart.Progress.PathDistance, ShotTangentSmoothing);
                    // Shots fly along the line the way the kart faces (StraightProjectile.AimDirection).
                    if (tangent.sqrMagnitude > MinShotTangentSqr) dir = Vector3.Dot(tangent, kart.transform.forward) < 0f ? -tangent : tangent;
                }
            }
            var flat = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
            climb = item.Kind == AIItemKind.StraightShot ? dir.y / Mathf.Max(flat, MinShotFlatLength) : 0f;
            return GeometryUtil.FlatNormalized(dir, Vector3.forward, 1e-4f);
        }

        static float FlightTime(AIItemProfile item, float speed, float distance)
        {
            if (speed <= MinShotSpeed) return 0f;
            if (item.Deceleration > 0f)
            {
                var disc = speed * speed - 2f * item.Deceleration * distance;
                return disc > 0f ? (speed - Mathf.Sqrt(disc)) / item.Deceleration : 2f * distance / speed;
            }
            return distance / speed;
        }

        // The puddle stays: an unprotected kart ahead (in the kart's direction along the track) must still be short of it
        // when it lands, and drive into it.
        static bool LobWillLand(Kart kart, AIItemProfile item, int direction)
        {
            var path = AIKartQueries.MainPath();
            if (path == null) return false;
            var mySpeed = Mathf.Max(0f, kart.Physics.ForwardSpeed);
            var landing = item.LobLanding(mySpeed);
            var flight = AIItemProfile.LobFlightTime(landing);
            var myD = kart.Progress.PathDistance;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Effects.IsProtected) continue;
                var gap = direction * path.Delta(myD, o.Progress.PathDistance);
                if (gap <= 0f) continue;
                var atLanding = gap + (o.Physics.ForwardSpeed - mySpeed) * flight;
                if (atLanding > landing - LobWindowFar && atLanding < landing - LobWindowNear) return true;
            }
            return false;
        }

        // A thrown trap lands just in front of a kart ahead. A thrown charge goes off on contact or landing, so any kart in
        // the line of throw up to the landing spot counts, within its blast radius.
        static bool ThrowLandsBeforeKart(Kart kart, AIItemProfile item)
        {
            var tf = kart.transform;
            var dir = ShotDirection(kart, item, out _);
            var blast = item.BlastRadius > 0f;
            var minAlong = blast ? BlastMinAlong : item.ForwardThrowRange - ThrowWindow;
            var maxAlong = blast ? item.ForwardThrowRange + item.BlastRadius * 0.5f : item.ForwardThrowRange - ThrowShortfall;
            var maxOff = blast ? Mathf.Max(ThrowMaxOff, item.BlastRadius) : ThrowMaxOff;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Effects.IsProtected) continue;
                var to = o.transform.position - tf.position;
                var along = Vector3.Dot(to, dir);
                if (along < minAlong || along > maxAlong) continue;
                var off = to - dir * along;
                off.y = 0f;
                if (off.magnitude < maxOff && AIKartQueries.LineOfSight(kart, o)) return true;
            }
            return false;
        }

        static bool TargetedWorth(Kart kart, AIItemProfile item)
        {
            if (item.Selection is TargetSelection.AllOthers or TargetSelection.AllAhead)
                return AIKartQueries.CountTargets(kart, item.Selection, item.Reach) > 0;
            var t = AIKartQueries.FirstTarget(kart, item.Selection, item.Reach);
            return t != null && !t.Effects.IsProtected;
        }

        bool SelfWorth(Kart kart, AIItemProfile item, float heldTime, float steer)
        {
            var leading = AIKartQueries.IsLeading(kart);
            if (item.Protects) return ProtectionWorth(kart, item, leading, heldTime, steer);
            if (!item.Boosts) return true;
            if (leading && heldTime < BoostHoldTime && AIKartQueries.ClosestBehind(kart, BoostThreatDistance) == null) return false;
            return AIKartQueries.OnStraight(kart, steer, board.direction);
        }

        bool ProtectionWorth(Kart kart, AIItemProfile item, bool leading, float heldTime, float steer)
        {
            if (kart.Effects.IsProtected) return false;
            // Kept for the homing missile (used when it is imminent), which always goes for the leader.
            if (difficulty.aiDefendMissile && (missile.Incoming != null || (leading && heldTime < LeaderHoldTime))) return false;
            if (item.Rams) return AIKartQueries.CountAhead(kart, RamRange, RamHalfWidth) > 0;
            if (item.Boosts) return AIKartQueries.OnStraight(kart, steer, board.direction);
            return AIKartQueries.CountAround(kart, ShieldRadius) > 0 || heldTime > ShieldHoldTime;
        }

        /// <summary>The kart is grounded and the first part of the dig ahead (in <paramref name="direction"/> along the track) crosses no gap.</summary>
        public static bool DigClear(Kart kart, AIItemProfile item, int direction)
        {
            if (!kart.Physics.IsGrounded) return false;
            var track = TrackRuntime.Current;
            var path = AIKartQueries.MainPath();
            if (track == null || path == null) return true;
            var d = kart.Progress.PathDistance;
            var reach = Mathf.Max(MinDigCheck, item.Reach * DigCheckShare);
            for (var x = DigSampleStep; x <= reach; x += DigSampleStep)
                if (TrackGaps.IsGap(track, path, d + direction * x)) return false;
            return true;
        }
    }
}
