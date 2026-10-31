using MHK.Core;
using MHK.Karts;
using MHK.Powerups;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Kart traffic of a computer driver: an early passing side for a slower or blocking kart ahead (driven out alongside
    /// and past it at full speed, <see cref="GameConfig.aiPassLookSeconds"/>), a free side line instead of tucking in close
    /// behind a kart on the same line (held beside its lane until passed or pulled away), a side gap to karts alongside, a
    /// follow speed only behind a kart that leaves no side free, and a lane straight at a nearby kart it can flatten when
    /// the driver hunts those (<see cref="DifficultySettings.aiFlattenSeekChance"/>). Karts it can drive through (ghosts,
    /// flattened or shrunk karts) are no traffic.
    /// </summary>
    internal sealed class AIPassPlanner
    {
        // A kart ahead closer than BlockGap (m, nose to tail) blocks even at the same speed, slower ones from MinClosing (m/s).
        // Following keeps FollowGap plus FollowHeadway seconds of the own speed and backs off at FollowBackOff (m/s per m),
        // but a stopped kart is pushed past at CrawlSpeed, a finished one at speed. Moving out to pass only brakes when even
        // an urgent lane change (plus LaneLag s for the steering) would not clear the kart before PassSafetyGap.
        const float MinClosing = 0.5f, BlockGap = 3f;
        const float ReturnGap = 1.5f, PassClearMargin = 2f, AbortDrop = 2f, AbortSeconds = 0.3f;
        const float FollowGap = 2.5f, FollowHeadway = 0.25f, FollowBackOff = 1f, PassSafetyGap = 1f, CrawlSpeed = 2f, LaneLag = 0.3f;
        const float UrgentSeconds = 1f, LostSeconds = 1f;
        // A kart in the lane ahead closer than ShadowGap (m, nose to tail) that does not pull away is not sat behind: the
        // side line is taken as for a pass, judged as if closing at ShadowClosing (m/s) and only with ShadowEdgeMargin (m)
        // to spare from the road edges, and held until the kart is passed or ShadowRelease (m) ahead. No side free: the
        // kart stays where it is (only real blockers are followed).
        const float ShadowGap = 8f, ShadowRelease = 14f, ShadowClosing = 2f, ShadowEdgeMargin = 0.5f;
        // Karts farther than TrafficReach (m) beyond the look-ahead range are no traffic (checked before anything else).
        const float TrafficReach = 10f;
        const float EdgeSlack = 0.02f, AlongsideMargin = 1f, DefaultHalfWidth = 0.6f, DefaultHalfLength = 1f;
        // Flatten hunting: karts up to FlattenReach (m, nose to tail) ahead whose lane is at most FlattenSwerveShare of
        // their distance sideways, so the kart only leans towards targets on its way.
        const float FlattenReach = 30f, FlattenSwerveShare = 0.5f;

        readonly AIRouteFollower line;
        readonly AIFeelers feelers;
        readonly bool seekFlatten;
        Kart kart;
        GameConfig cfg;
        float speed, cap, width, roadShift, myU, halfWidth, halfLength;
        Vector3 position;
        bool selfCrushable;
        Kart target;
        float side, lostTime, blockedTime;
        bool shadowing;

        /// <summary><paramref name="seekFlatten"/>: this driver steers into karts it can flatten close ahead.</summary>
        public AIPassPlanner(AIRouteFollower line, AIFeelers feelers, bool seekFlatten)
        {
            this.line = line;
            this.feelers = feelers;
            this.seekFlatten = seekFlatten;
        }

        /// <summary>Speed (m/s) to stay behind a kart that cannot be passed, infinity when the way is free.</summary>
        public float FollowSpeed { get; private set; } = float.PositiveInfinity;

        /// <summary>This step's lane wish keeps clear of a kart or aims at one: small lane corrections count too.</summary>
        public bool Engaged { get; private set; }

        /// <summary>
        /// Moves the lane wish <paramref name="u"/> onto a kart to flatten, the passing side of a kart ahead or clear of
        /// karts alongside. <paramref name="capSpeed"/> is the kart's speed without traffic: a kart ahead it could drive
        /// faster than counts as slower. <paramref name="urgent"/> is set when the lane must change fast to clear or reach
        /// a kart.
        /// </summary>
        public float Plan(Kart self, float kartSpeed, float capSpeed, float roadWidth, float shift, float dt, GameConfig config, ref bool urgent,
            float u)
        {
            FollowSpeed = float.PositiveInfinity;
            Engaged = false;
            // A ghost or flattened kart drives through every other kart.
            if (Kart.All.Count < 2 || self.Effects.Ghost || self.Effects.RunOverActive)
            {
                target = null;
                return u;
            }
            Begin(self, kartSpeed, capSpeed, roadWidth, shift, config);
            if (seekFlatten && !selfCrushable) u = SeekFlatten(u, ref urgent);
            u = KeepPass(u, dt, ref urgent);
            var blocker = FindBlocker(u, out var gap, out var closing, out var shadow);
            if (blocker != null && blocker != target) u = StartPass(blocker, u, gap, closing, shadow, ref urgent);
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
            position = self.transform.position;
            myU = line.LaneHere(position);
            selfCrushable = CrushOnKartContact.IsCrushable(self);
            if (halfWidth <= 0f) HalfSize(self, out halfWidth, out halfLength);
        }

        // Nearest kart this one flattens on contact, close ahead in reach of its lane and slower: the wish goes straight at it.
        float SeekFlatten(float u, ref bool urgent)
        {
            var best = FlattenReach;
            var closing = 0f;
            var lane = u;
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (!CrushOnKartContact.IsCrushable(o) || o.Effects.Ghost || o.Effects.RunOverActive) continue;
                if (!Locate(o, out var otherU, out var along, out _, out var lengths) || along <= 0f) continue;
                var gap = along - lengths;
                var road = otherU + roadShift;
                if (gap > best || road < AILanes.TargetMin || road > AILanes.TargetMax) continue;
                if (Mathf.Abs(otherU - myU) * width > along * FlattenSwerveShare) continue;
                var c = cap - o.Physics.ForwardSpeed;
                if (c < MinClosing) continue;
                best = gap;
                closing = c;
                lane = otherU;
            }
            if (closing <= 0f) return u;
            Engaged = true;
            if (best < closing * UrgentSeconds) urgent = true;
            return lane;
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
            if (shadowing && closing >= MinClosing) shadowing = false;
            if (along < -(lengths + ReturnGap) || gap > cfg.aiLookAheadRange || lostTime > LostSeconds || (shadowing && gap > ShadowRelease))
            {
                target = null;
                return u;
            }
            var to = otherU + side * half;
            blockedTime = Free(to, target, along, lengths, shadowing ? Mathf.Max(closing, ShadowClosing) : closing, shadowing ? ShadowEdgeMargin : 0f)
                ? 0f : blockedTime + dt;
            if (blockedTime > AbortSeconds)
            {
                // Still behind it: plan again (the other side, else follow). Alongside but not ahead yet: drop back behind
                // it on this side.
                if (gap > 0f)
                {
                    target = null;
                    return u;
                }
                if (along > 0f) FollowSpeed = Mathf.Max(0f, target.Physics.ForwardSpeed - AbortDrop);
            }
            Engaged = true;
            if (gap > 0f) MoveOut(target, otherU, half, gap, ref urgent);
            return side < 0f ? Mathf.Min(u, to) : Mathf.Max(u, to);
        }

        // Nearest kart ahead in the lane that is slower or close enough to block, or (shadow) just close in front.
        Kart FindBlocker(float u, out float gap, out float closing, out bool shadow)
        {
            Kart best = null;
            gap = float.MaxValue;
            closing = 0f;
            shadow = false;
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (!Measure(o, out var otherU, out var along, out var half, out var lengths)) continue;
                var g = along - lengths;
                if (g < 0f || g >= gap || (Mathf.Abs(otherU - u) >= half && Mathf.Abs(otherU - myU) >= half)) continue;
                var c = cap - o.Physics.ForwardSpeed;
                var blocks = g <= Mathf.Min(cfg.aiLookAheadRange, Mathf.Max(BlockGap, c * cfg.aiPassLookSeconds)) && (g <= BlockGap || c >= MinClosing);
                if (!blocks && (g > ShadowGap || c <= -MinClosing)) continue;
                best = o;
                gap = g;
                closing = c;
                shadow = !blocks;
            }
            return best;
        }

        // Passes on the cheaper free side. Only a kart with no side free (walls, traps or other karts beside it, or road
        // too narrow) is followed; a kart just ahead on the same line (shadow) is then left as it is.
        float StartPass(Kart o, float u, float gap, float closing, bool shadow, ref bool urgent)
        {
            Measure(o, out var otherU, out var along, out var half, out var lengths);
            var left = otherU - half;
            var right = otherU + half;
            var judged = shadow ? Mathf.Max(closing, ShadowClosing) : closing;
            var margin = shadow ? ShadowEdgeMargin : 0f;
            var leftFree = Free(left, o, along, lengths, judged, margin);
            var rightFree = Free(right, o, along, lengths, judged, margin);
            if (!leftFree && !rightFree)
            {
                if (!shadow) FollowSpeed = Follow(o, gap, FollowGap + Mathf.Max(0f, speed) * FollowHeadway);
                target = null;
                return u;
            }
            var leftCost = Mathf.Abs(left - myU) + Mathf.Abs(left - u);
            var rightCost = Mathf.Abs(right - myU) + Mathf.Abs(right - u);
            side = !rightFree || (leftFree && leftCost < rightCost) ? -1f : 1f;
            target = o;
            shadowing = shadow;
            lostTime = blockedTime = 0f;
            Engaged = true;
            MoveOut(o, otherU, half, gap, ref urgent);
            return side < 0f ? left : right;
        }

        // Still (partly) in the lane of kart o ahead: the lane change turns urgent when the normal one would not clear o in
        // time, and the speed only drops by what even the urgent one cannot make up.
        void MoveOut(Kart o, float otherU, float half, float gap, ref bool urgent)
        {
            var sideways = (half - Mathf.Abs(otherU - myU)) * width;
            if (sideways <= 0f || o.Progress.Finished) return;
            var ahead = Mathf.Max(0f, o.Physics.ForwardSpeed);
            var closing = speed - ahead;
            if (closing <= 0f) return;
            var room = Mathf.Max(0f, gap - PassSafetyGap);
            if ((sideways / cfg.aiLaneChangeSpeed + LaneLag) * closing <= room) return;
            urgent = true;
            var outTime = sideways / (cfg.aiLaneChangeSpeed * cfg.aiLaneUrgentFactor) + LaneLag;
            if (outTime * closing <= room) return;
            FollowSpeed = Mathf.Min(FollowSpeed, Mathf.Max(ahead + room / outTime, CrawlSpeed));
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

        // Road (measured edges, edgeMargin m to spare), feelers free in lane pl for the track driven until clear ahead of the
        // passed kart, and no third kart there that this kart would reach before.
        bool Free(float pl, Kart passing, float along, float lengths, float closing, float edgeMargin = 0f)
        {
            var road = pl + roadShift;
            if (road < AILanes.DriveMin - EdgeSlack || road > AILanes.DriveMax + EdgeSlack) return false;
            var passTime = (along + lengths + ReturnGap) / Mathf.Max(closing, MinClosing);
            var need = Mathf.Max(along + lengths + PassClearMargin, speed * passTime);
            var reach = Mathf.Min(feelers.Range, need);
            if (!OnRoad(pl, 0f, edgeMargin) || !OnRoad(pl, reach * 0.5f, edgeMargin) || !OnRoad(pl, reach, edgeMargin)) return false;
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

        bool OnRoad(float pl, float ahead, float edgeMargin)
        {
            line.RoadEdges(ahead, out var left, out var right);
            var w = (halfWidth + edgeMargin) / width;
            return pl - w >= left && pl + w <= right;
        }

        // Karts this one has to keep clear of: not ghosts, not flattened karts and not karts it flattens on contact (two
        // crushable karts flatten each other, so a crushable kart keeps clear of those too).
        bool Measure(Kart o, out float otherU, out float along, out float half, out float lengths)
        {
            otherU = along = half = lengths = 0f;
            if (o == null || o.Effects.Ghost || o.Effects.RunOverActive || (!selfCrushable && CrushOnKartContact.IsCrushable(o))) return false;
            return Locate(o, out otherU, out along, out half, out lengths);
        }

        // Lane of o (from its track progress, else from where it is seen), centre distance ahead along the road, the lane
        // distance two karts keep with the side gap and their summed half lengths. False for karts not on the road here.
        bool Locate(Kart o, out float otherU, out float along, out float half, out float lengths)
        {
            otherU = along = half = lengths = 0f;
            if (o == null || o == kart || o.Eliminated || !o.isActiveAndEnabled || o.Effects.Hidden) return false;
            var reach = cfg.aiLookAheadRange + TrafficReach;
            var at = o.transform.position;
            if ((at - position).sqrMagnitude > reach * reach) return false;
            var local = kart.transform.InverseTransformPoint(at);
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
