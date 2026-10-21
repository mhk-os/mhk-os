using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Kart traffic of a computer driver: an early passing side for a slower or blocking kart ahead, a side gap to karts
    /// alongside, and a follow speed behind a kart that leaves no side free (<see cref="GameConfig.aiPassLookSeconds"/>).
    /// </summary>
    internal sealed class AIPassPlanner
    {
        // A kart ahead closer than BlockGap (m, nose to tail) blocks even at the same speed, slower ones from MinClosing (m/s).
        // Following keeps FollowGap plus FollowHeadway seconds of the own speed (PassSafetyGap while moving out to pass) and
        // backs off at FollowBackOff (m/s per m), but a stopped kart is pushed past at CrawlSpeed, a finished one at speed.
        const float MinClosing = 0.5f, BlockGap = 3f;
        const float ReturnGap = 1.5f, PassClearMargin = 2f, AbortDrop = 2f, AbortSeconds = 0.3f;
        const float FollowGap = 2.5f, FollowHeadway = 0.25f, FollowBackOff = 1f, PassSafetyGap = 1f, CrawlSpeed = 2f;
        const float UrgentSeconds = 1f, LostSeconds = 1f;
        const float EdgeSlack = 0.02f, AlongsideMargin = 1f, DefaultHalfWidth = 0.6f, DefaultHalfLength = 1f;

        readonly AIRouteFollower line;
        readonly AIFeelers feelers;
        Kart kart;
        GameConfig cfg;
        float speed, cap, width, roadShift, myU, halfWidth, halfLength;
        Kart target;
        float side, lostTime, blockedTime;

        public AIPassPlanner(AIRouteFollower line, AIFeelers feelers)
        {
            this.line = line;
            this.feelers = feelers;
        }

        /// <summary>Speed (m/s) to stay behind a kart that cannot be passed, infinity when the way is free.</summary>
        public float FollowSpeed { get; private set; } = float.PositiveInfinity;

        /// <summary>This step's lane wish keeps clear of a kart: small lane corrections count too.</summary>
        public bool Engaged { get; private set; }

        /// <summary>
        /// Moves the lane wish <paramref name="u"/> onto the passing side of a kart ahead or clear of karts alongside.
        /// <paramref name="capSpeed"/> is the kart's speed without traffic: a kart ahead it could drive faster than counts
        /// as slower. <paramref name="urgent"/> is set when a kart is less than a second ahead in the lane.
        /// </summary>
        public float Plan(Kart self, float kartSpeed, float capSpeed, float roadWidth, float shift, float dt, GameConfig config, ref bool urgent,
            float u)
        {
            FollowSpeed = float.PositiveInfinity;
            Engaged = false;
            if (Kart.All.Count < 2)
            {
                target = null;
                return u;
            }
            Begin(self, kartSpeed, capSpeed, roadWidth, shift, config);
            u = KeepPass(u, dt, ref urgent);
            var blocker = FindBlocker(u, out var gap, out var closing);
            if (blocker != null && blocker != target) u = StartPass(blocker, u, gap, closing, ref urgent);
            return GiveRoom(u, ref urgent);
        }

        void Begin(Kart self, float kartSpeed, float capSpeed, float roadWidth, float shift, GameConfig config)
        {
            kart = self;
            speed = kartSpeed;
            cap = Mathf.Max(kartSpeed, capSpeed);
            width = roadWidth;
            roadShift = shift;
            cfg = config;
            myU = line.LaneHere(self.transform.position);
            if (halfWidth <= 0f) HalfSize(self, out halfWidth, out halfLength);
        }

        float KeepPass(float u, float dt, ref bool urgent)
        {
            if (target == null) return u;
            if (!Measure(target, out var otherU, out var along, out var half, out var lengths))
            {
                target = null;
                return u;
            }
            var gap = along - lengths;
            var closing = cap - target.Physics.ForwardSpeed;
            lostTime = gap > BlockGap && closing < -MinClosing ? lostTime + dt : 0f;
            if (along < -(lengths + ReturnGap) || gap > cfg.aiLookAheadRange || lostTime > LostSeconds)
            {
                target = null;
                return u;
            }
            var to = otherU + side * half;
            blockedTime = Free(to, target, along, lengths, closing) ? 0f : blockedTime + dt;
            if (blockedTime > AbortSeconds)
            {
                // Still behind it: plan again. Alongside but not ahead yet: drop back behind it on this side.
                if (gap > 0f)
                {
                    target = null;
                    return u;
                }
                if (along > 0f) FollowSpeed = Mathf.Max(0f, target.Physics.ForwardSpeed - AbortDrop);
            }
            Engaged = true;
            if (gap > 0f && Mathf.Abs(otherU - myU) < half) FollowSpeed = Mathf.Min(FollowSpeed, Follow(target, gap, PassSafetyGap));
            if (gap > 0f && gap < (speed - target.Physics.ForwardSpeed) * UrgentSeconds) urgent = true;
            return side < 0f ? Mathf.Min(u, to) : Mathf.Max(u, to);
        }

        Kart FindBlocker(float u, out float gap, out float closing)
        {
            Kart best = null;
            gap = float.MaxValue;
            closing = 0f;
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (!Measure(o, out var otherU, out var along, out var half, out var lengths)) continue;
                var g = along - lengths;
                if (g < 0f || g >= gap || (Mathf.Abs(otherU - u) >= half && Mathf.Abs(otherU - myU) >= half)) continue;
                var c = cap - o.Physics.ForwardSpeed;
                if (g > Mathf.Min(cfg.aiLookAheadRange, Mathf.Max(BlockGap, c * cfg.aiPassLookSeconds))) continue;
                if (g > BlockGap && c < MinClosing) continue;
                best = o;
                gap = g;
                closing = c;
            }
            return best;
        }

        float StartPass(Kart o, float u, float gap, float closing, ref bool urgent)
        {
            Measure(o, out var otherU, out var along, out var half, out var lengths);
            var left = otherU - half;
            var right = otherU + half;
            var leftFree = Free(left, o, along, lengths, closing);
            var rightFree = Free(right, o, along, lengths, closing);
            if (!leftFree && !rightFree)
            {
                FollowSpeed = Follow(o, gap, FollowGap + Mathf.Max(0f, speed) * FollowHeadway);
                target = null;
                return u;
            }
            if (Mathf.Abs(otherU - myU) < half) FollowSpeed = Follow(o, gap, PassSafetyGap);
            var leftCost = Mathf.Abs(left - myU) + Mathf.Abs(left - u);
            var rightCost = Mathf.Abs(right - myU) + Mathf.Abs(right - u);
            side = !rightFree || (leftFree && leftCost < rightCost) ? -1f : 1f;
            target = o;
            lostTime = blockedTime = 0f;
            Engaged = true;
            if (gap < (speed - o.Physics.ForwardSpeed) * UrgentSeconds) urgent = true;
            return side < 0f ? left : right;
        }

        float Follow(Kart o, float gap, float keep)
        {
            if (o.Progress.Finished) return float.PositiveInfinity;
            var ahead = Mathf.Max(0f, o.Physics.ForwardSpeed);
            var v = gap >= keep ? ahead + Mathf.Sqrt(2f * cfg.aiPlanBraking * (gap - keep)) : ahead - (keep - gap) * FollowBackOff;
            return Mathf.Max(v, CrawlSpeed);
        }

        // Karts beside this one: the wish stays on this kart's side of each with the side gap, so neither pushes the other.
        float GiveRoom(float u, ref bool urgent)
        {
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (o == target || !Measure(o, out var otherU, out var along, out var half, out var lengths)) continue;
                if (Mathf.Abs(along) > lengths + AlongsideMargin) continue;
                var limit = otherU <= myU ? otherU + half : otherU - half;
                if (otherU <= myU ? u >= limit : u <= limit) continue;
                u = limit;
                Engaged = true;
                if (Mathf.Abs(otherU - myU) < half - cfg.aiPassSideGap / width) urgent = true;
            }
            return u;
        }

        // Road (measured edges), feelers free in lane pl for the track driven until clear ahead of the passed kart, and no
        // third kart there that this kart would reach before.
        bool Free(float pl, Kart passing, float along, float lengths, float closing)
        {
            var road = pl + roadShift;
            if (road < AILanes.DriveMin - EdgeSlack || road > AILanes.DriveMax + EdgeSlack) return false;
            var passTime = (along + lengths + ReturnGap) / Mathf.Max(closing, MinClosing);
            var need = Mathf.Max(along + lengths + PassClearMargin, speed * passTime);
            var reach = Mathf.Min(feelers.Range, need);
            if (!OnRoad(pl, 0f) || !OnRoad(pl, reach * 0.5f) || !OnRoad(pl, reach)) return false;
            if (feelers.FreeAhead(pl) < Mathf.Min(feelers.Range - 0.01f, need)) return false;
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (o == passing || !Measure(o, out var otherU, out var otherAlong, out var half, out var both)) continue;
                if (otherAlong <= -both || Mathf.Abs(otherU - pl) >= half) continue;
                if (otherAlong - both < Mathf.Max(BlockGap, (cap - o.Physics.ForwardSpeed) * passTime)) return false;
            }
            return true;
        }

        bool OnRoad(float pl, float ahead)
        {
            line.RoadEdges(ahead, out var left, out var right);
            var w = halfWidth / width;
            return pl - w >= left && pl + w <= right;
        }

        // Lane of o (from its track progress, else from where it is seen), centre distance ahead along the road, the lane
        // distance two karts keep with the side gap and their summed half lengths. False for karts not in the way.
        bool Measure(Kart o, out float otherU, out float along, out float half, out float lengths)
        {
            otherU = along = half = lengths = 0f;
            if (o == null || o == kart || o.Eliminated || !o.isActiveAndEnabled || o.Effects.Hidden || o.Effects.RunOverActive) return false;
            var local = kart.transform.InverseTransformPoint(o.transform.position);
            if (Mathf.Abs(local.y) > AILanes.SameLevelHeight) return false;
            if (!line.TryLaneOf(o, out otherU, out along))
            {
                otherU = myU + local.x / width;
                along = local.z;
            }
            HalfSize(o, out var w, out var l);
            half = (halfWidth + w + cfg.aiPassSideGap) / width;
            lengths = halfLength + l;
            return true;
        }

        static void HalfSize(Kart k, out float w, out float l)
        {
            var box = k.Physics.Chassis;
            if (box == null)
            {
                w = DefaultHalfWidth;
                l = DefaultHalfLength;
                return;
            }
            var scale = box.transform.lossyScale;
            w = box.size.x * 0.5f * Mathf.Abs(scale.x);
            l = box.size.z * 0.5f * Mathf.Abs(scale.z);
        }
    }
}
