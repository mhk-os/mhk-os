using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// Item use and rear awareness of a computer driver. Items are judged by <see cref="AIItemProfile"/>, the engine class
    /// decides how cleverly they are aimed or kept, and every item is used before the next item box cluster (in a battle
    /// after <see cref="GameConfig.aiBattleItemMaxHoldSeconds"/> at the latest).
    /// </summary>
    internal sealed class AIItemBrain
    {
        const float FirstUseMin = 1f, FirstUseSpread = 3f;
        const float PlantTimeout = 35f;
        const float MinReaction = 0.6f, MaxReaction = 1.4f, RandomFireChance = 0.3f;
        const float SlowAimInterval = 0.3f, FastAimInterval = 0.1f;
        const float OffensiveRange = 60f, OffensiveHalfWidth = 20f, OffensiveBlindChance = 0.2f, ProtectChance = 0.4f;
        const float DropBehind = 3f, BlindDropChance = 0.3f, LookAfterRearUse = 0.3f;

        readonly DifficultySettings difficulty;
        readonly AIBlackboard board;
        readonly System.Random rng;
        readonly AILookBack lookBack;
        readonly AIMissileThreat missile;
        readonly AIItemJudge judge;
        float itemTimer;
        float aimTimer;
        PowerupDefinition heldDef;
        AIItemProfile held;
        int heldReceived;
        int givenCharges;
        float heldTime;
        float useBy = float.PositiveInfinity, maxHold = float.PositiveInfinity;
        bool plantNarrow;
        bool firedLast;

        public AIItemBrain(DifficultySettings difficulty, AIBlackboard board, System.Random rng)
        {
            this.difficulty = difficulty;
            this.board = board;
            this.rng = rng;
            itemTimer = FirstUseMin + rng.NextFloat() * FirstUseSpread;
            lookBack = new AILookBack(difficulty, board, rng);
            missile = new AIMissileThreat(difficulty, board, rng);
            judge = new AIItemJudge(difficulty, board, missile);
        }

        /// <summary>Looks back, reacts to homing missiles and decides whether to fire the held item this physics step.</summary>
        public void Tick(Kart kart, ref KartInput input, float dt, GameConfig cfg)
        {
            Decide(kart, ref input, dt, cfg);
            firedLast = input.fire;
        }

        void Decide(Kart kart, ref KartInput input, float dt, GameConfig cfg)
        {
            var inv = kart.Inventory;
            TrackHeld(kart, inv, dt, cfg);
            var item = held;
            missile.Tick(kart, item, dt);
            lookBack.Tick(kart, item != null && RearCapable(item), plantNarrow, dt);
            if (lookBack.Active) input.lookBack = true;

            var canUse = item != null && kart.ControlsEnabled && !kart.Effects.ControlsLocked && !kart.Effects.ItemUseBlocked;
            if (!canUse) judge.ClearAim();
            judge.PublishAim();
            if (!canUse) return;

            if (difficulty.aiDefendMissile && item.Protects && missile.Imminent(kart) && !kart.Effects.IsProtected)
            {
                input.fire = true;
                return;
            }
            if (UseDue(kart, inv, cfg))
            {
                BlindUse(kart, inv, item, ref input);
                return;
            }
            if (inv.Charges >= givenCharges && heldTime < cfg.aiItemMinHoldSeconds) return;
            if (lookBack.Active) RearUse(kart, item, ref input, dt);
            else if (difficulty.aiAimedShots && item.Kind != AIItemKind.Unknown) SmartUse(kart, item, ref input, dt);
            else RandomUse(kart, item, ref input, dt);
        }

        void TrackHeld(Kart kart, PowerupInventory inv, float dt, GameConfig cfg)
        {
            var def = inv.HasItem ? inv.Held : null;
            if (def != heldDef || (def != null && inv.Received != heldReceived))
            {
                heldDef = def;
                heldReceived = inv.Received;
                held = def != null ? AIItemProfile.Of(def) : null;
                givenCharges = def != null ? inv.Charges : 0;
                useBy = def != null ? UseBy(kart, cfg) : float.PositiveInfinity;
                maxHold = def != null ? MaxHold(cfg) : float.PositiveInfinity;
                heldTime = 0f;
                judge.ClearAim();
                // Rolled once per item: keep this trap for a narrow segment.
                plantNarrow = held != null && held.Kind == AIItemKind.Trap && held.CanGoBehind && rng.Roll(difficulty.aiNarrowPlantChance);
                return;
            }
            heldTime += dt;
            if (plantNarrow && heldTime > PlantTimeout) plantNarrow = false;
        }

        static float UseBy(Kart kart, GameConfig cfg)
        {
            var race = RaceManager.Current;
            var path = AIKartQueries.MainPath();
            if (race == null || race.IsBattle || path == null) return float.PositiveInfinity;
            return kart.Progress.RaceDistance + AIItemBoxes.ToNextCluster(path, kart.Progress.PathDistance, cfg.aiItemBoxClusterGap);
        }

        static float MaxHold(GameConfig cfg)
        {
            var race = RaceManager.Current;
            return race != null && race.IsBattle ? cfg.aiBattleItemMaxHoldSeconds : float.PositiveInfinity;
        }

        bool UseDue(Kart kart, PowerupInventory inv, GameConfig cfg)
        {
            if (heldTime >= maxHold) return true;
            if (float.IsPositiveInfinity(useBy)) return false;
            var lead = cfg.aiItemUseBeforeBoxes + Mathf.Max(0f, kart.Physics.ForwardSpeed) * (inv.Charges - 1) * heldDef.useCooldown;
            return useBy - kart.Progress.RaceDistance < lead;
        }

        // Traps go behind, shots ahead (behind for a leader when they can), the rest at once. The homing missile hunts the
        // leader, its user included (MHK2): a leader keeps it.
        void BlindUse(Kart kart, PowerupInventory inv, AIItemProfile item, ref KartInput input)
        {
            if (firedLast || inv.CoolingDown) return;
            var leading = AIKartQueries.IsLeading(kart);
            if (item.Kind == AIItemKind.HomingMissile && leading) return;
            if (item.Kind == AIItemKind.Dig && !AIItemJudge.DigClear(kart, item, board.direction)) return;
            input.lookBack = IsTrap(item) ? RearCapable(item) : item.CanGoBehind && leading;
            input.fire = true;
        }

        bool RearCapable(AIItemProfile item) =>
            item.CanGoBehind || (item.Kind == AIItemKind.Unknown && heldDef.aiUsage == PowerupAiUsage.Defensive);

        bool IsTrap(AIItemProfile item) =>
            item.Kind == AIItemKind.Trap || (item.Kind == AIItemKind.Unknown && heldDef.aiUsage == PowerupAiUsage.Defensive);

        // The originals' AI (and 50cc): a random decision every reaction time, traps only after a look back.
        void RandomUse(Kart kart, AIItemProfile item, ref KartInput input, float dt)
        {
            itemTimer -= dt;
            if (itemTimer > 0f) return;
            itemTimer = difficulty.aiItemReactionTime * rng.Range(MinReaction, MaxReaction);
            if (item.Kind == AIItemKind.HomingMissile && AIKartQueries.IsLeading(kart)) return;
            if (IsTrap(item) && RearCapable(item)) return;
            if (item.Kind == AIItemKind.Unknown && difficulty.aiAimedShots)
            {
                if (UnknownUse(kart, input.steer)) input.fire = true;
                return;
            }
            if (rng.NextFloat() < RandomFireChance) input.fire = true;
        }

        void SmartUse(Kart kart, AIItemProfile item, ref KartInput input, float dt)
        {
            aimTimer -= dt;
            if (aimTimer > 0f) return;
            aimTimer = Mathf.Lerp(SlowAimInterval, FastAimInterval, difficulty.aiPrecision);
            judge.ClearAim();
            if (judge.WorthFiring(kart, item, heldTime, input.steer)) input.fire = true;
        }

        bool UnknownUse(Kart kart, float steer) => heldDef.aiUsage switch
        {
            PowerupAiUsage.OffensiveForward => AIKartQueries.CountAhead(kart, OffensiveRange, OffensiveHalfWidth) > 0
                || (!AIKartQueries.IsLeading(kart) && rng.NextFloat() < OffensiveBlindChance),
            PowerupAiUsage.Defensive => false,
            PowerupAiUsage.Boost => AIKartQueries.OnStraight(kart, steer, board.direction),
            PowerupAiUsage.Protect => rng.NextFloat() < ProtectChance,
            _ => true,
        };

        // During a look back: at a kart seen in line behind, or a trap planted in a narrow segment.
        void RearUse(Kart kart, AIItemProfile item, ref KartInput input, float dt)
        {
            if (!RearCapable(item) || lookBack.Elapsed < AILookBack.Warning) return;
            var trap = IsTrap(item);
            var go = lookBack.PlantLook
                ? AIKartQueries.NarrowRoad(kart, board.route, kart.Progress.PathDistance - board.direction * DropBehind)
                : RearTargetOrBlindDrop(kart, item, trap, dt);
            if (!go) return;
            input.fire = true;
            lookBack.CutShort(LookAfterRearUse);
        }

        bool RearTargetOrBlindDrop(Kart kart, AIItemProfile item, bool trap, float dt)
        {
            var seen = lookBack.SeenBehind;
            var aimed = seen != null && AILookBack.InRearLine(kart, seen, trap)
                && (!difficulty.aiAimedShots || (!seen.Effects.IsProtected && (!item.NeedsLineOfSight || AIKartQueries.LineOfSight(kart, seen))));
            // A trap nobody was seen for is still dropped now and then when the look ends (not one kept for a narrow spot).
            var blind = trap && !plantNarrow && lookBack.Remaining <= dt && rng.NextFloat() < BlindDropChance;
            return aimed || blind;
        }
    }
}
