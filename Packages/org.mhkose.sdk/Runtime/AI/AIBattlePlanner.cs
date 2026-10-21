using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>What a computer driver in a battle is after.</summary>
    internal enum BattleGoal : byte { Wander, ItemBox, Attack, Escape }

    /// <summary>
    /// Goals of a computer driver in a battle: an item box when its hands are empty (a health pickup when it is weak), an
    /// opponent to hunt with an attacking item or to ram, a way out from a hunter or a crowd when weak, else a spot to
    /// wander to (offroad included). Decides every <see cref="BattleAiSettings.decisionInterval"/> seconds.
    /// </summary>
    internal sealed class AIBattlePlanner
    {
        const float DecisionSpread = 0.2f, TurnCostPerDegree = 0.12f, PathCostShare = 1.2f;
        const float ContestMargin = 4f, ContestCost = 20f, KeepShare = 0.8f, PowerDetour = 25f, LevelHeight = 3f;
        const float HunterCone = 35f, AimTargetBonus = 10f, KeepTargetBonus = 8f, WoundedBonus = 30f, CrowdShare = 0.5f;
        const float RamRange = 25f, ArenaRamRange = 40f, RamContact = 3.5f, MaxLeadSeconds = 1.5f, MinLeadSpeed = 5f;
        const float ArriveDistance = 5f, WanderMinSeconds = 4f, WanderMaxSeconds = 9f, WanderLeashShare = 0.8f, OffroadWanderChance = 0.3f;
        const float ProgressStep = 3f, GiveUpSeconds = 4f, IgnoreSeconds = 8f;
        const int WanderTries = 5;

        readonly BattleAiSettings skill;
        readonly System.Random rng;
        readonly AIBlackboard board;
        readonly AIBattleArea.Hints wanderHints = new();
        float decideTimer, wanderTimer, kartIgnoreTimer, boxIgnoreTimer, closest, stallTimer;
        ItemBox box, ignoredBox, stationBox;
        Kart ignoredKart;
        Object pursued;
        Vector3 escapePoint, wanderPoint;
        int station = -1, lastStation = -1, pursuedStation = -1, received;
        bool pursuedAlong;

        public AIBattlePlanner(BattleAiSettings skill, AIBlackboard board, System.Random rng)
        {
            this.skill = skill;
            this.board = board;
            this.rng = rng;
        }

        public BattleGoal Goal { get; private set; }
        /// <summary>Where the kart heads for this step.</summary>
        public Vector3 Point { get; private set; }
        /// <summary>Opponent hunted (<see cref="BattleGoal.Attack"/>) or escaped from (<see cref="BattleGoal.Escape"/>).</summary>
        public Kart Opponent { get; private set; }
        /// <summary>Item box station the kart heads for (<see cref="AIBattleBoxes"/>), -1 for none.</summary>
        public int Station => Goal == BattleGoal.ItemBox ? station : -1;

        /// <summary>Re-decides when due or when the goal is gone, then updates <see cref="Point"/>.</summary>
        public void Tick(Kart kart, TrackRuntime track, bool arena, float leash, GameConfig cfg, float dt)
        {
            decideTimer -= dt;
            wanderTimer -= dt;
            kartIgnoreTimer -= dt;
            boxIgnoreTimer -= dt;
            WatchContact(kart, cfg);
            WatchReceived(kart);
            if (station >= 0) stationBox = AIBattleBoxes.NearestAvailable(station, kart.transform.position);
            if (decideTimer <= 0f || GoalGone(kart))
            {
                decideTimer = skill.decisionInterval * rng.Range(1f - DecisionSpread, 1f + DecisionSpread);
                Decide(kart, track, arena, leash, cfg);
            }
            Point = Goal switch
            {
                BattleGoal.ItemBox => station < 0 ? box.transform.position
                    : stationBox != null ? stationBox.transform.position : AIBattleBoxes.StationCentre(station),
                BattleGoal.Attack => Lead(kart, Opponent),
                BattleGoal.Escape => escapePoint,
                _ => wanderPoint,
            };
        }

        /// <summary>
        /// Gives up a box, station or kart the kart does not get closer to (walls, other karts, a faster target) for a while.
        /// <paramref name="way"/> is the distance left along the way the navigator takes (<paramref name="alongLine"/>: along
        /// the lines), so following a winding road or a route towards the goal counts as progress.
        /// </summary>
        public void WatchProgress(float way, bool alongLine, float dt)
        {
            var atStation = Goal == BattleGoal.ItemBox && station >= 0;
            Object goal = Goal == BattleGoal.ItemBox ? atStation ? null : box : Goal == BattleGoal.Attack ? Opponent : null;
            var key = atStation ? station : -1;
            if (goal != pursued || key != pursuedStation || alongLine != pursuedAlong || way < closest - ProgressStep)
            {
                pursued = goal;
                pursuedStation = key;
                pursuedAlong = alongLine;
                closest = way;
                stallTimer = 0f;
                return;
            }
            stallTimer += dt;
            if ((goal == null && !atStation) || stallTimer < GiveUpSeconds) return;
            if (atStation) LeaveStation();
            else if (goal == box) Ignore(box);
            else Ignore(Opponent, IgnoreSeconds);
            pursued = null;
            pursuedStation = -1;
            decideTimer = 0f;
        }

        void WatchReceived(Kart kart)
        {
            if (kart.Inventory.Received == received) return;
            received = kart.Inventory.Received;
            if (station >= 0) LeaveStation();
        }

        void LeaveStation()
        {
            lastStation = station;
            station = -1;
            stationBox = null;
        }

        void Ignore(ItemBox b)
        {
            ignoredBox = b;
            boxIgnoreTimer = IgnoreSeconds;
        }

        void Ignore(Kart o, float seconds)
        {
            ignoredKart = o;
            kartIgnoreTimer = seconds;
        }

        bool Ignored(Kart o) => o == ignoredKart && kartIgnoreTimer > 0f;

        bool GoalGone(Kart kart) => Goal switch
        {
            BattleGoal.ItemBox => station >= 0 ? stationBox == null : box == null || !box.Available || !box.isActiveAndEnabled,
            BattleGoal.Attack => Opponent == null || !AIKartQueries.IsOpponent(kart, Opponent) || Opponent.Effects.IsProtected || Ignored(Opponent),
            BattleGoal.Wander => wanderTimer <= 0f || Arrived(kart),
            _ => false,
        };

        // A rammed kart takes no ramming damage for GameConfig.ramCooldown: pull away and come back later.
        void WatchContact(Kart kart, GameConfig cfg)
        {
            if (Goal != BattleGoal.Attack || Opponent == null || Flat(Opponent.transform.position - kart.transform.position).magnitude > RamContact) return;
            Ignore(Opponent, cfg.ramCooldown);
        }

        // Health when weak, escape when weak and pressed, attack with an attacking item, an item box with empty hands (one
        // close on the way first, else a station), escape from a hunter without a weapon, ram when aggressive, else wander.
        void Decide(Kart kart, TrackRuntime track, bool arena, float leash, GameConfig cfg)
        {
            var weak = kart.Health <= skill.fleeHealth * cfg.maxHealth;
            var held = kart.Inventory.Held;
            var attacking = held != null && Attacks(held);
            if (weak && TryBox(kart, track, arena, cfg, BattleBoxKind.Health)) return;
            var threat = rng.Roll(skill.threatAwareness) ? Hunter(kart, cfg) : null;
            if (weak && (threat != null || (threat = Nearest(kart, cfg.aiBattleThreatRange * CrowdShare)) != null))
            {
                Escape(kart, track, arena, threat, cfg);
                return;
            }
            if (attacking && TryAttack(kart, track, arena, float.PositiveInfinity, cfg)) return;
            var powered = kart.Effects.DamageDealtMultiplier > 1f;
            if (held == null && ((!powered && TryBox(kart, track, arena, cfg, BattleBoxKind.Power, PowerDetour))
                    || TryBox(kart, track, arena, cfg, BattleBoxKind.Item, cfg.aiBattleBoxGrabRange) || TryStation(kart, track, arena, cfg)))
                return;
            if (threat != null && !attacking)
            {
                Escape(kart, track, arena, threat, cfg);
                return;
            }
            var keepRam = Goal == BattleGoal.Attack && Opponent != null;
            if (!weak && (keepRam || rng.Roll(skill.aggression)) && TryAttack(kart, track, arena, arena ? ArenaRamRange : RamRange, cfg)) return;
            Wander(kart, track, leash, cfg);
        }

        static bool Attacks(PowerupDefinition def)
        {
            var item = AIItemProfile.Of(def);
            return item.Kind switch
            {
                AIItemKind.Trap or AIItemKind.Dig => false,
                AIItemKind.Self => item.Rams,
                AIItemKind.Unknown => def.aiUsage == PowerupAiUsage.OffensiveForward,
                _ => true,
            };
        }

        bool TryBox(Kart kart, TrackRuntime track, bool arena, GameConfig cfg, BattleBoxKind wanted, float maxCost = float.PositiveInfinity)
        {
            ItemBox best = null;
            var bestCost = maxCost;
            var boxes = ItemBox.Active;
            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (!b.Available || (b == ignoredBox && boxIgnoreTimer > 0f) || AIBattleBoxes.KindOf(b) != wanted) continue;
                // A detour box on another level (a bridge above, a road below) is not on the way.
                if (maxCost < float.PositiveInfinity && Mathf.Abs(b.transform.position.y - kart.transform.position.y) > LevelHeight) continue;
                var cost = Travel(kart, track, arena, b.transform.position, AIBattleBoxes.LineDistance(track, b), cfg) + Contest(kart, b);
                if (b == box && station < 0) cost *= KeepShare;
                if (cost >= bestCost) continue;
                best = b;
                bestCost = cost;
            }
            if (best == null) return false;
            box = best;
            station = -1;
            Goal = BattleGoal.ItemBox;
            return true;
        }

        bool TryStation(Kart kart, TrackRuntime track, bool arena, GameConfig cfg)
        {
            var count = AIBattleBoxes.StationCount(track, cfg.aiBattleStationSpacing);
            if (station < 0 || station >= count || AIBattleBoxes.NearestAvailable(station, kart.transform.position) == null)
                station = PickStation(kart, track, arena, count, cfg);
            if (station < 0) return false;
            stationBox = AIBattleBoxes.NearestAvailable(station, kart.transform.position);
            Goal = BattleGoal.ItemBox;
            return true;
        }

        // aiBattleStationAheadChance: a station ahead along the track, else one behind (the first one always ahead: the grid
        // faces forwards). Not the one just used, not one another computer driver heads for, chosen at random with the
        // chance falling with the square of the distance.
        int PickStation(Kart kart, TrackRuntime track, bool arena, int count, GameConfig cfg)
        {
            var ahead = lastStation < 0 || rng.Roll(cfg.aiBattleStationAheadChance);
            var pick = PickStation(kart, track, arena, count, cfg, ahead ? 1 : -1, true);
            if (pick < 0) pick = PickStation(kart, track, arena, count, cfg, ahead ? -1 : 1, true);
            if (pick < 0) pick = PickStation(kart, track, arena, count, cfg, 0, false);
            return pick < 0 && lastStation >= 0 && lastStation < count && AIBattleBoxes.NearestAvailable(lastStation, kart.transform.position) != null
                ? lastStation : pick;
        }

        int PickStation(Kart kart, TrackRuntime track, bool arena, int count, GameConfig cfg, int direction, bool unclaimed)
        {
            var total = 0f;
            for (var pass = 0; pass < 2; pass++)
            {
                var roll = pass == 0 ? 0f : rng.NextFloat() * total;
                for (var s = 0; s < count; s++)
                {
                    var weight = StationWeight(kart, track, arena, s, cfg, direction, unclaimed);
                    if (pass == 0) total += weight;
                    else if (weight > 0f && (roll -= weight) <= 0f) return s;
                }
                if (total <= 0f) return -1;
            }
            return -1;
        }

        float StationWeight(Kart kart, TrackRuntime track, bool arena, int s, GameConfig cfg, int direction, bool unclaimed)
        {
            if (s == lastStation || AIBattleBoxes.NearestAvailable(s, kart.transform.position) == null) return 0f;
            var line = AIBattleBoxes.StationLineDistance(s);
            if (direction != 0 && track.path.Delta(kart.Progress.PathDistance, line) * direction <= 0f) return 0f;
            if (unclaimed && Claimed(kart, s)) return 0f;
            var travel = Travel(kart, track, arena, AIBattleBoxes.StationCentre(s), line, cfg) / Mathf.Max(1f, cfg.aiBattleStationDistanceScale);
            return 1f / (1f + travel * travel);
        }

        static bool Claimed(Kart kart, int s)
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
                if (karts[i] != kart && !karts[i].Eliminated && karts[i].Driver is AIKartDriver d && d.BattleStation == s)
                    return true;
            return false;
        }

        float Travel(Kart kart, TrackRuntime track, bool arena, Vector3 to, float toLine, GameConfig cfg)
        {
            var delta = Flat(to - kart.transform.position);
            var straight = delta.magnitude;
            var turn = Vector3.Angle(Flat(kart.transform.forward), delta) * TurnCostPerDegree;
            if (arena || straight <= cfg.aiBattleDirectRange) return straight + turn;
            return Mathf.Abs(track.path.Delta(kart.Progress.PathDistance, toLine)) * PathCostShare + turn;
        }

        static float Contest(Kart kart, ItemBox b)
        {
            var p = b.transform.position;
            var mine = (p - kart.transform.position).sqrMagnitude;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Inventory.Held != null) continue;
                var theirs = Mathf.Sqrt((p - o.transform.position).sqrMagnitude) + ContestMargin;
                if (theirs * theirs < mine) return ContestCost;
            }
            return 0f;
        }

        bool TryAttack(Kart kart, TrackRuntime track, bool arena, float maxRange, GameConfig cfg)
        {
            Kart best = null;
            var bestCost = float.PositiveInfinity;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Effects.IsProtected || Ignored(o)) continue;
                var travel = Travel(kart, track, arena, o.transform.position, o.Progress.PathDistance, cfg);
                if (travel > maxRange) continue;
                var cost = travel - skill.aggression * WoundedBonus * (1f - o.Health / (float)Mathf.Max(1, cfg.maxHealth))
                    - (o == board.aimTarget ? AimTargetBonus : 0f) - (o == Opponent && Goal == BattleGoal.Attack ? KeepTargetBonus : 0f);
                if (cost >= bestCost) continue;
                best = o;
                bestCost = cost;
            }
            if (best == null) return false;
            Opponent = best;
            Goal = BattleGoal.Attack;
            return true;
        }

        Vector3 Lead(Kart kart, Kart target)
        {
            var p = target.transform.position;
            var distance = Flat(p - kart.transform.position).magnitude;
            var seconds = Mathf.Min(MaxLeadSeconds, distance / Mathf.Max(MinLeadSpeed, kart.Physics.ForwardSpeed));
            return p + Flat(target.Physics.Body.linearVelocity) * (seconds * skill.aimLead);
        }

        static Kart Hunter(Kart kart, GameConfig cfg)
        {
            Kart best = null;
            var bestDistance = cfg.aiBattleThreatRange;
            var p = kart.transform.position;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Inventory.Held == null || !Attacks(o.Inventory.Held)) continue;
                var to = p - o.transform.position;
                var distance = to.magnitude;
                if (distance >= bestDistance || Vector3.Angle(o.transform.forward, to) > HunterCone) continue;
                best = o;
                bestDistance = distance;
            }
            return best;
        }

        static Kart Nearest(Kart kart, float range)
        {
            Kart best = null;
            var bestDistance = range;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o)) continue;
                var distance = AIKartQueries.Distance(kart, o);
                if (distance >= bestDistance) continue;
                best = o;
                bestDistance = distance;
            }
            return best;
        }

        void Escape(Kart kart, TrackRuntime track, bool arena, Kart threat, GameConfig cfg)
        {
            Opponent = threat;
            Goal = BattleGoal.Escape;
            var p = kart.transform.position;
            if (arena)
            {
                var away = Flat(p - threat.transform.position);
                escapePoint = p + (away.sqrMagnitude > 0.01f ? away.normalized : Flat(kart.transform.forward).normalized) * cfg.aiBattleWanderRadius;
                return;
            }
            var path = track.path;
            var from = kart.Progress.PathDistance;
            var direction = path.Delta(from, threat.Progress.PathDistance) > 0f ? -1f : 1f;
            escapePoint = path.GetPoint(path.WrapDistance(from + direction * cfg.aiBattleWanderRadius));
        }

        void Wander(Kart kart, TrackRuntime track, float leash, GameConfig cfg)
        {
            Opponent = null;
            if (Goal == BattleGoal.Wander && wanderTimer > 0f && !Arrived(kart)) return;
            Goal = BattleGoal.Wander;
            wanderTimer = rng.Range(WanderMinSeconds, WanderMaxSeconds);
            var path = track.path;
            var from = kart.Progress.PathDistance;
            for (var i = 0; i < WanderTries; i++)
            {
                var d = path.WrapDistance(from + rng.Range(-2f, 2f) * cfg.aiBattleWanderRadius);
                var centre = path.GetPoint(d);
                var side = Vector3.Cross(Vector3.up, path.GetFlatTangent(d, 1f)).normalized;
                var reach = 0.5f * path.GetWidth(d) + (rng.Roll(OffroadWanderChance) ? leash * WanderLeashShare : 0f);
                wanderPoint = centre + side * rng.Range(-reach, reach);
                wanderHints.Reset();
                if (AIBattleArea.Safe(track, wanderPoint, centre.y, leash, AIBattleArea.MinNormalY(cfg), wanderHints, out _)) return;
            }
            wanderPoint = path.GetPoint(path.WrapDistance(from + rng.Range(-1f, 1f) * cfg.aiBattleWanderRadius));
        }

        bool Arrived(Kart kart) => Flat(wanderPoint - kart.transform.position).magnitude < ArriveDistance;

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    }
}
