using MHK.Core;
using MHK.Karts;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Lane choice of a computer driver: the MHK2 centre band with its wobble, item boxes, a lined-up shot, speed pads, a
    /// missile yield, berths around melee holders, kart traffic (<see cref="AIPassPlanner"/>), planted traps and finally a
    /// lane free of walls and props.
    /// </summary>
    internal sealed class AILanePlanner
    {
        const float WobbleRate = 0.2f, WobbleAmplitude = 0.3f;
        const float LookBase = 8f, LookPerSpeed = 1.5f, MinRoadWidth = 2f;
        const float SlowLaneChange = 0.8f, FastLaneChange = 1.2f;
        const float YieldLeftLane = 0.8f, YieldRightLane = 0.2f;
        const float PickupReachShare = 0.7f, PickupDistanceCost = 0.2f;
        const float PadLaneReach = 3.5f, MinPadAlignment = 0.7f, PadTangentSmoothing = 4f;
        const float HazardMargin = 0.6f;
        const float MeleeEdge = 0.15f, MeleeLead = 1.2f, MaxMeleeLead = 20f;

        readonly DifficultySettings difficulty;
        readonly AIBlackboard board;
        readonly AIRouteFollower line;
        readonly AIFeelers feelers;
        readonly AIPassPlanner traffic;
        readonly int rollSeed;
        readonly float noiseSeed;
        float time;

        public AILanePlanner(DifficultySettings difficulty, AIBlackboard board, AIRouteFollower line, AIFeelers feelers, int rollSeed,
            float noiseSeed, float laneU)
        {
            this.difficulty = difficulty;
            this.board = board;
            this.line = line;
            this.feelers = feelers;
            traffic = new AIPassPlanner(line, feelers);
            this.rollSeed = rollSeed;
            this.noiseSeed = noiseSeed;
            Lane = laneU;
        }

        /// <summary>Planned lane (relative to the racing line, 0.5 = on it) the steering follows.</summary>
        public float Lane { get; private set; }

        /// <summary>Half width (m) of the kart's chassis, set once its collider is known.</summary>
        public float KartHalfWidth { get; set; }

        /// <summary>Speed (m/s) that keeps the kart behind a kart ahead it cannot pass, infinity when the way is free.</summary>
        public float FollowSpeed => traffic.FollowSpeed;

        /// <summary>Advances the clock of the lane wobble by one physics step.</summary>
        public void Advance(float dt) => time += dt;

        /// <summary>
        /// Moves the lane towards this step's wish, in priority order (traps last: bumping a kart beats driving onto a
        /// mine), then the freest lane the feelers found. <paramref name="roadShift"/> converts lanes to road lanes,
        /// <paramref name="capSpeed"/> is the speed the kart would drive at without traffic.
        /// Returns the distance to the first wall or solid body in the chosen lane (<see cref="AIFeelers.Range"/> when none).
        /// </summary>
        public float Plan(Kart kart, float speed, float capSpeed, float dt, float roadShift, GameConfig cfg)
        {
            var width = Mathf.Max(MinRoadWidth, line.Width(0f));
            var range = Mathf.Min(cfg.aiLookAheadRange, LookBase + Mathf.Abs(speed) * LookPerSpeed);
            var urgent = false;
            var desired = Wish(kart, range, roadShift);
            if (difficulty.aiAvoidHammerChance > 0f) desired = AvoidMelee(kart, speed, width, roadShift, desired);
            desired = traffic.Plan(kart, speed, capSpeed, width, roadShift, dt, cfg, ref urgent, desired);
            desired = AvoidHazards(kart, range, desired, ref urgent);
            desired = feelers.ChooseLane(Mathf.Clamp(desired, AILanes.DriveMin - roadShift, AILanes.DriveMax - roadShift), width, out var clear,
                out var obstructed);
            if (obstructed) urgent = true;
            Glide(desired, width, urgent, traffic.Engaged, dt, cfg);
            return clear;
        }

        float Wish(Kart kart, float range, float roadShift)
        {
            var wobble = (Mathf.PerlinNoise(noiseSeed, time * WobbleRate) - 0.5f) * WobbleAmplitude * (1f - difficulty.aiPrecision);
            var desired = AILanes.Centre + wobble;
            if (WantsItem(kart) && FindPickup(kart, range, out var pickLane)) desired = pickLane;
            else if (board.aimRequested && line.Route == null) desired = board.aimLaneU - roadShift;
            else if (FindSpeedPad(kart, range, desired, out var padLane)) desired = padLane;
            if (board.yieldRequested && board.yieldTo != null)
                desired = (kart.transform.InverseTransformPoint(board.yieldTo.transform.position).x < 0f ? YieldLeftLane : YieldRightLane) - roadShift;
            return desired;
        }

        // Small lane wishes are ignored (not when keeping clear of a kart) and lane changes glide, so frame-to-frame lane
        // picks don't make the kart weave.
        void Glide(float desired, float width, bool urgent, bool exact, float dt, GameConfig cfg)
        {
            if (!urgent && !exact && Mathf.Abs(desired - Lane) * width < cfg.aiLaneTolerance) desired = Lane;
            var rate = cfg.aiLaneChangeSpeed / width * Mathf.Lerp(SlowLaneChange, FastLaneChange, difficulty.aiPrecision)
                       * (urgent ? cfg.aiLaneUrgentFactor : 1f);
            Lane = Mathf.MoveTowards(Lane, desired, dt * rate);
        }

        static bool WantsItem(Kart kart)
        {
            var inv = kart.Inventory;
            return inv != null && inv.Held == null && !inv.Rolling && PowerupRoller.ItemsEnabled;
        }

        bool FindPickup(Kart kart, float range, out float lane)
        {
            lane = Lane;
            var best = float.MaxValue;
            var pos = kart.transform.position;
            var boxes = ItemBox.Active;
            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Available) ConsiderPickup(box.transform.position, pos, range, ref best, ref lane);
            }
            var hazards = PlantedHazards.All;
            for (var i = 0; i < hazards.Count; i++)
            {
                var h = hazards[i];
                if (h.LooksLikeItemBox && h.DangerRadius > 0f && !SeesThrough(h)) ConsiderPickup(h.Position, pos, range, ref best, ref lane);
            }
            return best < float.MaxValue;
        }

        void ConsiderPickup(Vector3 p, Vector3 kartPos, float range, ref float best, ref float lane)
        {
            if ((p - kartPos).sqrMagnitude > range * range) return;
            if (!line.TryLocate(p, range, out var at) || at.RoadLane < AILanes.TargetMin || at.RoadLane > AILanes.TargetMax) return;
            var swerve = Mathf.Abs(at.Lane - Lane) * at.Width;
            if (swerve > at.Ahead * PickupReachShare) return;
            var cost = swerve + at.Ahead * PickupDistanceCost;
            if (cost >= best) return;
            best = cost;
            lane = at.Lane;
        }

        bool SeesThrough(IPlantedHazard fake) => AIRolls.Once(rollSeed ^ AIRolls.FakeSalt, fake) < difficulty.aiAvoidFakeBoxChance;

        // Nearest speed pad ahead along the road, close to the planned lane, that the driver notices (its precision, per pad and lap).
        bool FindSpeedPad(Kart kart, float range, float desired, out float lane)
        {
            lane = desired;
            var pads = TrackZone.SpeedPads;
            var nearest = float.MaxValue;
            var pos = kart.transform.position;
            for (var i = 0; i < pads.Count; i++)
            {
                var pad = pads[i];
                var p = pad.transform.position;
                if ((p - pos).sqrMagnitude > range * range) continue;
                if (AIRolls.PerLap(rollSeed ^ AIRolls.PadSalt, pad, kart.Progress.Lap) >= difficulty.aiPrecision) continue;
                if (!line.TryLocate(p, range, out var at) || at.Ahead >= nearest) continue;
                if (at.RoadLane < AILanes.TargetMin || at.RoadLane > AILanes.TargetMax || Mathf.Abs(at.Lane - desired) * at.Width > PadLaneReach) continue;
                if (Vector3.Dot(pad.transform.forward, line.Tangent(at.Ahead, PadTangentSmoothing)) < MinPadAlignment) continue;
                nearest = at.Ahead;
                lane = at.Lane;
            }
            return nearest < float.MaxValue;
        }

        float AvoidHazards(Kart kart, float range, float u, ref bool urgent)
        {
            var hazards = PlantedHazards.All;
            var pos = kart.transform.position;
            for (var i = 0; i < hazards.Count; i++)
            {
                var h = hazards[i];
                var radius = h.DangerRadius;
                if (radius <= 0f) continue;
                var p = h.Position;
                if ((p - pos).sqrMagnitude > range * range) continue;
                var avoid = h.LooksLikeItemBox ? SeesThrough(h) : AIRolls.Once(rollSeed ^ AIRolls.TrapSalt, h) < difficulty.aiAvoidTrapChance;
                if (!avoid || !line.TryLocate(p, range, out var at)) continue;
                var half = (radius + KartHalfWidth + HazardMargin) / at.Width;
                if (Mathf.Abs(u - at.Lane) >= half) continue;
                u = DodgeLane(u, at.Lane, half, at.RoadLane - at.Lane);
                urgent = true;
            }
            return u;
        }

        static float DodgeLane(float u, float hazardLane, float half, float shift)
        {
            var right = hazardLane + half;
            var left = hazardLane - half;
            if (u >= hazardLane) return right + shift <= AILanes.DodgeMax ? right : left;
            return left + shift >= AILanes.DodgeMin ? left : right;
        }

        // Keeps the item's reach plus the kart's half width sideways from opponents holding a melee item, a plain lane
        // preference that kart, trap and wall avoidance override.
        float AvoidMelee(Kart kart, float speed, float width, float roadShift, float u)
        {
            var all = Kart.All;
            for (var i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (!IsMeleeThreat(kart, other, speed, out var reach, out var local)) continue;
                var otherU = Lane + local.x / width + roadShift;
                var half = (reach + KartHalfWidth) / width;
                var r = u + roadShift;
                if (Mathf.Abs(r - otherU) >= half) continue;
                u = BerthLane(r, otherU, half) - roadShift;
            }
            return u;
        }

        bool IsMeleeThreat(Kart kart, Kart other, float speed, out float reach, out Vector3 local)
        {
            reach = 0f;
            local = default;
            if (other == kart || other.Eliminated || other.Effects.HasRespawnPending) return false;
            var inv = other.Inventory;
            if (inv == null || !inv.HasItem || inv.Charges <= 0) return false;
            var item = AIItemProfile.Of(inv.Held);
            if (item.Kind != AIItemKind.Melee || item.Reach <= 0f) return false;
            reach = item.Reach;
            var tf = kart.transform;
            local = tf.InverseTransformPoint(other.transform.position);
            if (local.z < -reach || Mathf.Abs(local.y) > AILanes.SameLevelHeight) return false;
            var closing = Mathf.Max(0f, speed - Vector3.Dot(other.Physics.Body.linearVelocity, tf.forward));
            if (local.z > reach + Mathf.Min(closing * MeleeLead, MaxMeleeLead)) return false;
            return AIRolls.PerLap(rollSeed ^ AIRolls.MeleeSalt, other, kart.Progress.Lap) < difficulty.aiAvoidHammerChance;
        }

        // Road lane beside a melee holder: the side it is on unless that runs out of road, else as far out as the road allows.
        static float BerthLane(float r, float otherU, float half)
        {
            var right = otherU + half;
            var left = otherU - half;
            if (r >= otherU)
            {
                if (right <= 1f - MeleeEdge) return right;
                if (left >= MeleeEdge) return left;
                return 1f - otherU >= otherU ? 1f - MeleeEdge : MeleeEdge;
            }
            if (left >= MeleeEdge) return left;
            if (right <= 1f - MeleeEdge) return right;
            return otherU >= 1f - otherU ? MeleeEdge : 1f - MeleeEdge;
        }
    }
}
