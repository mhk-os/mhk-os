using MHK.Core;
using MHK.Karts;
using MHK.Powerups;

namespace MHK.AI
{
    /// <summary>
    /// Homing missiles hunting a computer driver: the nearest one, whether it is about to hit, and the missile yield
    /// (a hunted leader without a defence lets a close follower pass so the missile takes that kart instead).
    /// </summary>
    internal sealed class AIMissileThreat
    {
        const float DefendDistance = 30f;
        const float YieldReach = 15f, YieldMaxTime = 5f, YieldMaxDistance = 30f;

        readonly DifficultySettings difficulty;
        readonly AIBlackboard board;
        readonly System.Random rng;
        TrackHomingMissile rolledMissile;
        bool yielding;
        Kart yieldTo;
        float yieldTime;

        public AIMissileThreat(DifficultySettings difficulty, AIBlackboard board, System.Random rng)
        {
            this.difficulty = difficulty;
            this.board = board;
            this.rng = rng;
        }

        /// <summary>Nearest missile hunting the kart (null = none).</summary>
        public TrackHomingMissile Incoming { get; private set; }

        /// <summary>Finds the hunting missile, rolls for a yield once per missile and publishes the yield on the blackboard.</summary>
        public void Tick(Kart kart, AIItemProfile item, float dt)
        {
            Incoming = NearestHunting(kart);
            if (yielding) KeepYielding(kart, dt);
            else if (CanYield(kart, item)) RollYield(kart);
            board.yieldRequested = yielding;
            board.yieldTo = yielding ? yieldTo : null;
        }

        /// <summary>The hunting missile has locked on, or is armed and close: a protecting item now outlasts the hit.</summary>
        public bool Imminent(Kart kart)
        {
            var m = Incoming;
            if (m == null) return false;
            return m.Locked || (m.Armed && (m.transform.position - kart.transform.position).sqrMagnitude < DefendDistance * DefendDistance);
        }

        static TrackHomingMissile NearestHunting(Kart kart)
        {
            TrackHomingMissile incoming = null;
            var best = float.MaxValue;
            var missiles = TrackHomingMissile.Active;
            for (var i = 0; i < missiles.Count; i++)
            {
                var m = missiles[i];
                if (m == null || m.Hunted != kart) continue;
                var d2 = (m.transform.position - kart.transform.position).sqrMagnitude;
                if (d2 < best) { best = d2; incoming = m; }
            }
            return incoming;
        }

        void KeepYielding(Kart kart, float dt)
        {
            yieldTime += dt;
            var incoming = Incoming;
            var keep = incoming != null && incoming == rolledMissile && !incoming.Locked && yieldTo != null && !yieldTo.Eliminated
                && yieldTo.Progress.Position > kart.Progress.Position && yieldTime < YieldMaxTime && AIKartQueries.Distance(kart, yieldTo) < YieldMaxDistance;
            if (keep) return;
            yielding = false;
            yieldTo = null;
        }

        bool CanYield(Kart kart, AIItemProfile item)
        {
            var incoming = Incoming;
            return incoming != null && incoming != rolledMissile && !incoming.Locked && (item == null || !item.Protects)
                && !kart.Effects.IsProtected && difficulty.aiMissileYieldChance > 0f;
        }

        void RollYield(Kart kart)
        {
            var behind = AIKartQueries.ClosestBehind(kart, YieldReach);
            if (behind == null) return;
            rolledMissile = Incoming;
            if (!rng.Roll(difficulty.aiMissileYieldChance)) return;
            yielding = true;
            yieldTo = behind;
            yieldTime = 0f;
        }
    }
}
