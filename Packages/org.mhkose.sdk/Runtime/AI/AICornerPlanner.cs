using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>Braking plan of a computer driver: the highest speed from which it can still slow to every bend ahead.</summary>
    internal sealed class AICornerPlanner
    {
        const float Step = 2f;
        const float MinHorizon = 12f, HorizonMargin = 10f;
        const float SideTangentSmoothing = 2f, MinBendRadius = 0.5f;
        // MHK2 original-unit floor (times the speed scale): never plan to crawl through a bend.
        const float BendSpeedFloor = 12f;

        readonly AIRouteFollower line;
        readonly AITurnLimit turn;

        public AICornerPlanner(AIRouteFollower line, AITurnLimit turn)
        {
            this.line = line;
            this.turn = turn;
        }

        /// <summary>Updates what the kart can turn, grip and brake (<see cref="AITurnLimit"/>) for the driver's engine class.</summary>
        public void Refresh(Kart kart, DifficultySettings difficulty, GameConfig cfg) =>
            turn.Refresh(kart, kart.NormalTopSpeed, cfg.aiTurnRadiusMargin * Mathf.Lerp(cfg.aiImpreciseTurnMargin, 1f, difficulty.aiPrecision),
                cfg.aiCornerGrip, cfg.aiBrakeGrip);

        /// <summary>
        /// Highest speed now from which the kart can still slow, braking as hard as <see cref="GameConfig.aiBrakeGrip"/>
        /// allows, to every bend of the followed racing line up to the braking horizon, reaching each bend's speed as long
        /// before it as the wheel needs to turn to full lock (braking while turning in pushes the kart wide). A lane
        /// <paramref name="laneU"/> inside a bend counts as that much tighter, as does the distance the kart is off its
        /// lane. Infinity when nothing ahead needs braking.
        /// </summary>
        public float CornerSpeed(Kart kart, float speed, float speedScale, float laneU)
        {
            if (!turn.Ready) return float.PositiveInfinity;
            var floor = BendSpeedFloor * speedScale;
            var turnIn = turn.LockTime;
            var horizon = Mathf.Max(MinHorizon, turn.BrakingDistance(speed, floor) + speed * turnIn + HorizonMargin);
            var aside = (laneU - AILanes.Centre) * line.Width(0f);
            var miss = OffLane(kart, laneU);
            var best = float.PositiveInfinity;
            for (var d = Step; d <= horizon; d += Step)
            {
                var radius = line.Radius(d, out var friction, out var side);
                // Inside the racing line the arc is tighter; a lane outside it is not counted on (the lane may return).
                var bend = turn.SpeedFor(Mathf.Max(MinBendRadius, radius - Mathf.Max(0f, aside * side) - miss), friction);
                if (float.IsPositiveInfinity(bend)) continue;
                bend = Mathf.Max(floor, bend);
                best = Mathf.Min(best, turn.SpeedBefore(bend, d - Step - bend * turnIn));
            }
            return best;
        }

        // How far the kart is off its planned lane (it ran wide and still has to get back, MHK3 Rome).
        float OffLane(Kart kart, float laneU)
        {
            var side = TrackPath.RightOf(line.Tangent(0f, SideTangentSmoothing));
            return Mathf.Abs(Vector3.Dot(kart.transform.position - line.Point(0f, laneU), side));
        }
    }
}
