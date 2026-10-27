using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>What a computer driver in a battle is doing.</summary>
    internal enum BattleMode : byte
    {
        /// <summary>Circuits: drives the track like a race in its direction (<see cref="AIBattlePlanner.Direction"/>), picking up item boxes on the way.</summary>
        Race,
        /// <summary>Circuits: turns around on the road to its direction along the track.</summary>
        Turn,
        /// <summary>Hunts an opponent to take it down with its item (or a ram).</summary>
        Hunt,
        /// <summary>Arenas: roams to item boxes and open spots at full speed.</summary>
        Roam,
    }

    /// <summary>
    /// What a computer driver in a battle does. On circuits it races the track in its direction (<see cref="BattleMode.Race"/>,
    /// the race driving with its item box pickups), in arenas it roams to item boxes and open spots (<see cref="BattleMode.Roam"/>).
    /// Holding an attacking item with an opponent in sight within <see cref="BattleAiSettings.huntRange"/> it hunts that
    /// opponent (<see cref="BattleMode.Hunt"/>, also a ram of a close opponent right in front without an item) until the
    /// item is used up, the target is gone or it stays too far or out of sight; then it races on the way it faces. With
    /// fewer than <see cref="EndgameKarts"/> karts left it seeks out the others wherever they are, armed or ramming. Once per
    /// battle, at a seeded time after GO, it reverses its direction, and it turns back at dead ends (a gap jumped the
    /// other way, the end of an open line); a speed pad sets the direction to the pad's. Every change of direction is a
    /// turn on the road (<see cref="BattleMode.Turn"/>).
    /// </summary>
    internal sealed class AIBattlePlanner
    {
        const float DecisionSpread = 0.2f;
        const int ReverseKey = 0x52455653;
        // The nose more than TurnEnterAngle off the direction along the line starts a turn around, less than
        // TurnLeaveAngle ends it (the navigator's turn uses the same angles).
        const float TurnEnterAngle = 110f, TurnLeaveAngle = 50f;
        // Dead ends are looked for this often (s), as far as the stopping distance plus DeadEndMargin (at least DeadEndMin).
        const float DeadEndInterval = 0.1f, DeadEndMin = 12f, DeadEndMargin = 8f;
        // Hunting: the cost of a target is its distance plus BehindCost when it is behind, HuntedCost per other computer
        // driver hunting it, less bonuses for the current target, the item brain's aim target and a wounded one.
        const float BehindCost = 25f, HuntedCost = 15f, KeepTargetBonus = 10f, AimTargetBonus = 8f, WoundedBonus = 20f;
        const float LevelHeight = 4f, RamCone = 30f, RamContact = 3.5f, RamLeaveShare = 1.5f;
        const float MaxLeadSeconds = 1.5f, MinLeadSpeed = 5f;
        const float ProgressStep = 3f, GiveUpSeconds = 4f, IgnoreSeconds = 8f;
        // Arenas: a box costs its distance plus TurnCostPerDegree per degree to turn towards it, ContestCost when an
        // empty-handed opponent is closer; spots lie within RoamCone of the heading (wider when none is safe).
        const float TurnCostPerDegree = 0.12f, ContestMargin = 4f, ContestCost = 20f, KeepShare = 0.8f;
        const float RoamCone = 60f, ArriveDistance = 5f, RoamSpeed = 8f, MinRoamSeconds = 4f, MinMoveSpeed = 2f;
        const int RoamTries = 6;
        // A box giving the character's special costs this share of a normal one: preferred when about as easy to reach.
        const float SpecialShare = 0.7f;
        // With fewer karts than this left in the battle, computer drivers hunt the rest across the whole track.
        const int EndgameKarts = 3;

        readonly BattleAiSettings skill;
        readonly System.Random rng;
        readonly AIBlackboard board;
        readonly bool arena;
        readonly float reverseAt;
        readonly AIBattleArea.Hints roamHints = new();
        float decideTimer, deadEndTimer, loseTimer, kartIgnoreTimer, boxIgnoreTimer, roamTimer, closest, stallTimer;
        float lastClock = -1f;
        bool started, reversed, turnBack, ramming, targetVisible, endgame;
        TrackZone lastPad;
        ItemBox box, ignoredBox;
        Kart ignoredKart;
        Object pursued;
        Vector3 roamPoint;

        /// <param name="seed">Seed of this driver's rolls in this battle (the reversal time is rolled from it).</param>
        public AIBattlePlanner(BattleAiSettings skill, AIBlackboard board, bool arena, int seed)
        {
            this.skill = skill;
            this.board = board;
            this.arena = arena;
            rng = new System.Random(seed);
            reverseAt = Mathf.Lerp(Mathf.Min(skill.reverseMinSeconds, skill.reverseMaxSeconds),
                Mathf.Max(skill.reverseMinSeconds, skill.reverseMaxSeconds), AIRolls.Roll01(seed, ReverseKey));
        }

        public BattleMode Mode { get; private set; }
        /// <summary>Circuits: 1 along the track's direction, -1 against it.</summary>
        public int Direction { get; private set; } = 1;
        /// <summary>Where the kart heads for (<see cref="BattleMode.Hunt"/>, <see cref="BattleMode.Roam"/>).</summary>
        public Vector3 Point { get; private set; }
        /// <summary>Opponent hunted, null when not hunting.</summary>
        public Kart Target => Mode == BattleMode.Hunt ? opponent : null;
        /// <summary>Item box the kart roams to (arenas), null for none.</summary>
        public ItemBox Box => Mode == BattleMode.Roam ? box : null;

        Kart opponent;

        /// <summary>
        /// Updates mode, direction and goal for this physics step. <paramref name="line"/> is the kart's line follower (dead
        /// ends ahead), <paramref name="leash"/> how far beyond the road edges spots may lie.
        /// </summary>
        public void Tick(Kart kart, TrackRuntime track, AIRouteFollower line, float leash, GameConfig cfg, float dt)
        {
            var clock = kart.Progress.RaceClock;
            if (!started || clock < lastClock) Restart(kart, track);
            lastClock = clock;
            decideTimer -= dt;
            deadEndTimer -= dt;
            kartIgnoreTimer -= dt;
            boxIgnoreTimer -= dt;
            roamTimer -= dt;
            var armed = Armed(kart);
            WatchEndgame();
            if (Mode == BattleMode.Hunt && !KeepHunting(kart, armed, cfg, dt)) EndHunt(kart, track);
            WatchReverse(kart, clock);
            FollowSpeedPad(kart, track);
            if (!arena) WatchDirection(kart, track, line, cfg);
            if (decideTimer <= 0f || RoamGoalGone(kart))
            {
                decideTimer = skill.decisionInterval * rng.Range(1f - DecisionSpread, 1f + DecisionSpread);
                Decide(kart, track, armed, leash, cfg);
            }
            Point = Mode switch
            {
                BattleMode.Hunt => Lead(kart, opponent),
                BattleMode.Roam => box != null ? box.transform.position : roamPoint,
                _ => kart.transform.position,
            };
        }

        // A new battle (first step, or the race clock started again): drive the way the kart faces, reversal still to come.
        void Restart(Kart kart, TrackRuntime track)
        {
            started = true;
            reversed = turnBack = ramming = endgame = false;
            lastPad = null;
            opponent = null;
            box = null;
            pursued = null;
            decideTimer = roamTimer = deadEndTimer = loseTimer = 0f;
            Mode = arena ? BattleMode.Roam : BattleMode.Race;
            Direction = arena ? 1 : FacingSign(kart, track);
        }

        // The one reversal, at the rolled time, while racing or roaming (a hunt or a turn finishes first).
        void WatchReverse(Kart kart, float clock)
        {
            if (reversed || clock < reverseAt || Mode is BattleMode.Hunt or BattleMode.Turn) return;
            reversed = true;
            if (arena)
            {
                turnBack = true;
                box = null;
                roamTimer = decideTimer = 0f;
                return;
            }
            Direction = -Direction;
            Mode = BattleMode.Turn;
        }

        // The endgame starts with fewer than EndgameKarts karts left: decide right away to go after the others.
        void WatchEndgame()
        {
            if (endgame || AIKartQueries.CountRacing() >= EndgameKarts) return;
            endgame = true;
            decideTimer = 0f;
        }

        // A speed pad turns the kart to the pad's direction: on circuits the kart races on that way along the track instead
        // of turning back against the boost, in arenas it roams on to a new spot ahead (not when heading for a box). Not
        // while hunting: the way is the target's.
        void FollowSpeedPad(Kart kart, TrackRuntime track)
        {
            var pad = SpeedPadAt(kart.transform.position);
            var entered = pad != null && pad != lastPad;
            lastPad = pad;
            if (!entered || Mode == BattleMode.Hunt) return;
            if (arena)
            {
                if (box == null) roamTimer = 0f;
                return;
            }
            var sign = Vector3.Dot(pad.transform.forward, LineTangent(kart, track)) >= 0f ? 1 : -1;
            if (sign == Direction) return;
            Direction = sign;
            Mode = BattleMode.Race;
        }

        static TrackZone SpeedPadAt(Vector3 position)
        {
            var pads = TrackZone.SpeedPads;
            for (var i = 0; i < pads.Count; i++)
                if (pads[i].Contains(position))
                    return pads[i];
            return null;
        }

        // Circuits: a nose far off the direction (reversal, respawn, spin) turns the kart around; a dead end ahead turns
        // the direction around.
        void WatchDirection(Kart kart, TrackRuntime track, AIRouteFollower line, GameConfig cfg)
        {
            if (Mode == BattleMode.Hunt) return;
            var angle = Vector3.Angle(Flat(kart.transform.forward), Flat(LineTangent(kart, track)) * Direction);
            if (Mode == BattleMode.Turn)
            {
                if (angle < TurnLeaveAngle) Mode = BattleMode.Race;
                return;
            }
            if (angle > TurnEnterAngle)
            {
                Mode = BattleMode.Turn;
                return;
            }
            if (deadEndTimer > 0f || line.Direction != Direction) return;
            deadEndTimer = DeadEndInterval;
            var speed = Mathf.Max(0f, kart.Physics.ForwardSpeed);
            var range = Mathf.Max(DeadEndMin, speed * speed / (2f * Mathf.Max(1f, cfg.aiPlanBraking)) + DeadEndMargin);
            if (!line.DeadEndWithin(range)) return;
            Direction = -Direction;
            Mode = BattleMode.Turn;
        }

        // Hunts start (or switch targets) at decisions, not while turning around. Arenas roam otherwise.
        void Decide(Kart kart, TrackRuntime track, bool armed, float leash, GameConfig cfg)
        {
            if (Mode != BattleMode.Turn && TryHunt(kart, armed, cfg)) return;
            if (arena) Roam(kart, track, leash, cfg);
        }

        /// <summary>An item held that hurts opponents (shots, melee, ramming boosts...), not a trap or a dig.</summary>
        static bool Armed(Kart kart)
        {
            var inv = kart.Inventory;
            return inv != null && inv.HasItem && inv.Charges > 0 && Attacks(inv.Held);
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

        // In the endgame every opponent is hunted, however far, on whichever level and out of sight, rammed when unarmed.
        bool TryHunt(Kart kart, bool armed, GameConfig cfg)
        {
            if (Mode == BattleMode.Hunt && ramming && !armed) return true;
            Kart best = null;
            var bestCost = float.PositiveInfinity;
            var pos = kart.transform.position;
            var forward = Flat(kart.transform.forward);
            var range = endgame ? float.PositiveInfinity : armed ? skill.huntRange : skill.ramRange;
            if (range <= 0f || (!armed && !endgame && !rng.Roll(skill.aggression))) return false;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var o = karts[i];
                if (!AIKartQueries.IsOpponent(kart, o) || o.Effects.IsProtected || Ignored(o)) continue;
                var to = o.transform.position - pos;
                if (!endgame && Mathf.Abs(to.y) > LevelHeight) continue;
                var flat = Flat(to);
                var distance = flat.magnitude;
                if (distance > range) continue;
                var behind = Vector3.Dot(flat, forward) < 0f;
                // A ram goes for an opponent right in front only, never one to turn around for (but for the last ones).
                if (!armed && !endgame && Vector3.Angle(forward, flat) > RamCone) continue;
                var cost = distance + (behind ? BehindCost : 0f) + HuntedCost * Hunters(kart, o)
                    - skill.aggression * WoundedBonus * (1f - o.Health / (float)Mathf.Max(1, cfg.maxHealth))
                    - (o == board.aimTarget ? AimTargetBonus : 0f) - (o == opponent && Mode == BattleMode.Hunt ? KeepTargetBonus : 0f);
                if (cost >= bestCost || (!endgame && !AIKartQueries.LineOfSight(kart, o))) continue;
                best = o;
                bestCost = cost;
            }
            if (best == null)
            {
                if (Mode == BattleMode.Hunt) targetVisible = opponent != null && AIKartQueries.LineOfSight(kart, opponent);
                return Mode == BattleMode.Hunt;
            }
            if (Mode != BattleMode.Hunt || best != opponent) loseTimer = 0f;
            opponent = best;
            ramming = !armed;
            targetVisible = true;
            Mode = BattleMode.Hunt;
            box = null;
            return true;
        }

        // Each step: the hunt goes on while armed (a ram: until contact or the target pulls away) and the target is
        // still there, not too far and in sight for longer than huntLoseSeconds. In the endgame distance and sight don't count.
        bool KeepHunting(Kart kart, bool armed, GameConfig cfg, float dt)
        {
            var o = opponent;
            if (o == null || !AIKartQueries.IsOpponent(kart, o) || o.Effects.IsProtected || Ignored(o)) return false;
            var distance = Flat(o.transform.position - kart.transform.position).magnitude;
            if (ramming && !armed)
            {
                // A rammed kart takes no ramming damage for GameConfig.ramCooldown: race on and come back later.
                if (distance < RamContact)
                {
                    Ignore(o, cfg.ramCooldown);
                    return false;
                }
                return endgame || distance <= skill.ramRange * RamLeaveShare;
            }
            if (!armed) return false;
            ramming = false;
            if (endgame) return true;
            var lost = distance > skill.huntRange * skill.huntLeaveShare || !targetVisible;
            loseTimer = lost ? loseTimer + dt : 0f;
            return loseTimer < skill.huntLoseSeconds;
        }

        // Back to racing the way the kart faces now (no turning around for a direction it had before the hunt).
        void EndHunt(Kart kart, TrackRuntime track)
        {
            opponent = null;
            ramming = false;
            pursued = null;
            Mode = arena ? BattleMode.Roam : BattleMode.Race;
            if (!arena) Direction = FacingSign(kart, track);
            roamTimer = decideTimer = 0f;
        }

        /// <summary>
        /// Gives up a target or box the kart does not get closer to (walls, other karts, a faster target) for a while.
        /// <paramref name="way"/> is the distance left along the way the navigator takes (<paramref name="alongLine"/>: along
        /// the lines), so following a winding road towards the goal counts as progress.
        /// </summary>
        public void WatchProgress(float way, bool alongLine, float dt)
        {
            Object goal = Mode == BattleMode.Hunt ? opponent : Mode == BattleMode.Roam ? box : null;
            if (goal != pursued || way < closest - ProgressStep)
            {
                pursued = goal;
                closest = way;
                stallTimer = 0f;
                return;
            }
            stallTimer += dt;
            if (goal == null || stallTimer < GiveUpSeconds) return;
            if (goal == box)
            {
                ignoredBox = box;
                boxIgnoreTimer = IgnoreSeconds;
                box = null;
            }
            else Ignore(opponent, IgnoreSeconds);
            pursued = null;
            decideTimer = 0f;
        }

        void Ignore(Kart o, float seconds)
        {
            ignoredKart = o;
            kartIgnoreTimer = seconds;
        }

        bool Ignored(Kart o) => o == ignoredKart && kartIgnoreTimer > 0f;

        bool RoamGoalGone(Kart kart) => Mode == BattleMode.Roam &&
            (box != null ? !box.Available || !box.isActiveAndEnabled : roamTimer <= 0f || Flat(roamPoint - kart.transform.position).magnitude < ArriveDistance);

        // An item box with empty hands (one with the character's special preferred, or a health / power pickup that
        // helps), else an open spot ahead. Right after the reversal only ones behind the kart.
        void Roam(Kart kart, TrackRuntime track, float leash, GameConfig cfg)
        {
            var inv = kart.Inventory;
            if (inv != null && inv.Held == null && !inv.Rolling && PowerupRoller.ItemsEnabled && TryBox(kart, cfg))
            {
                turnBack = false;
                return;
            }
            box = null;
            if (!turnBack && roamTimer > 0f && Flat(roamPoint - kart.transform.position).magnitude >= ArriveDistance) return;
            PickSpot(kart, track, leash, cfg);
            turnBack = false;
        }

        bool TryBox(Kart kart, GameConfig cfg)
        {
            ItemBox best = null;
            var bestCost = float.PositiveInfinity;
            var pos = kart.transform.position;
            var forward = Flat(kart.transform.forward);
            var hurt = kart.Health < cfg.maxHealth;
            var powered = kart.Effects.DamageDealtMultiplier > 1f;
            var boxes = ItemBox.Active;
            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                if (!b.Available || (b == ignoredBox && boxIgnoreTimer > 0f) || (b != box && Claimed(kart, b))) continue;
                var kind = AIBattleBoxes.KindOf(b);
                if (!(kind == BattleBoxKind.Item || (kind == BattleBoxKind.Health && hurt) || (kind == BattleBoxKind.Power && !powered))) continue;
                var to = Flat(b.transform.position - pos);
                if (turnBack && Vector3.Dot(to, forward) > 0f) continue;
                var cost = to.magnitude + Vector3.Angle(forward, to) * TurnCostPerDegree + Contest(kart, b);
                if (b == box) cost *= KeepShare;
                if (b.GivesSpecial(kart)) cost *= SpecialShare;
                if (cost >= bestCost) continue;
                best = b;
                bestCost = cost;
            }
            box = best;
            return best != null;
        }

        // Another computer driver roams to b: boxes are shared out.
        static bool Claimed(Kart kart, ItemBox b)
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
                if (karts[i] != kart && !karts[i].Eliminated && karts[i].Driver is AIKartDriver d && d.BattleBox == b)
                    return true;
            return false;
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

        // A safe spot aiBattleWanderRadius to twice that ahead (behind after the reversal), within RoamCone of the heading,
        // the cone widening to all around when none is safe. Kept until reached or out of time.
        void PickSpot(Kart kart, TrackRuntime track, float leash, GameConfig cfg)
        {
            var pos = kart.transform.position;
            var heading = kart.Physics.ForwardSpeed > MinMoveSpeed ? Flat(kart.Physics.Body.linearVelocity) : Flat(kart.transform.forward);
            heading = heading.sqrMagnitude > 1e-4f ? heading.normalized : Vector3.forward;
            if (turnBack) heading = -heading;
            var minNormalY = AIBattleArea.MinNormalY(cfg);
            for (var i = 0; i < RoamTries; i++)
            {
                var cone = Mathf.Lerp(RoamCone, 180f, i / (float)(RoamTries - 1));
                var distance = cfg.aiBattleWanderRadius * rng.Range(1f, 2f);
                var p = pos + Quaternion.AngleAxis(rng.Range(-cone, cone), Vector3.up) * heading * distance;
                roamHints.Reset();
                if (!AIBattleArea.Safe(track, p, pos.y, leash, minNormalY, roamHints, out var y)) continue;
                roamPoint = new Vector3(p.x, y, p.z);
                roamTimer = Mathf.Max(MinRoamSeconds, distance / RoamSpeed);
                return;
            }
            var path = track.path;
            roamPoint = path.GetPoint(path.WrapDistance(kart.Progress.PathDistance + cfg.aiBattleWanderRadius));
            roamTimer = MinRoamSeconds;
        }

        // Other computer drivers hunting o: hunters spread over the opponents.
        static int Hunters(Kart kart, Kart o)
        {
            var n = 0;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
                if (karts[i] != kart && !karts[i].Eliminated && karts[i].Driver is AIKartDriver d && d.BattleTarget == o)
                    n++;
            return n;
        }

        Vector3 Lead(Kart kart, Kart target)
        {
            var p = target.transform.position;
            var distance = Flat(p - kart.transform.position).magnitude;
            var seconds = Mathf.Min(MaxLeadSeconds, distance / Mathf.Max(MinLeadSpeed, kart.Physics.ForwardSpeed));
            return p + Flat(target.Physics.Body.linearVelocity) * (seconds * skill.aimLead);
        }

        // Direction along the track the kart's nose points to.
        static int FacingSign(Kart kart, TrackRuntime track) => Vector3.Dot(kart.transform.forward, LineTangent(kart, track)) >= 0f ? 1 : -1;

        // Track direction where the kart is: along the alternative route it is on, else the main line.
        static Vector3 LineTangent(Kart kart, TrackRuntime track)
        {
            var prog = kart.Progress;
            var route = prog.CurrentBranch;
            return route != null ? route.GetTangent(prog.BranchDistance) : track.path.GetTangent(prog.PathDistance);
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    }
}
