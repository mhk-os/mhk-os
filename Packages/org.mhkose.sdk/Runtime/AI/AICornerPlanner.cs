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
        readonly AITurnLimit turn = new();

        public AICornerPlanner(AIRouteFollower line) => this.line = line;

        /// <summary>Updates what the kart can turn and grip (<see cref="AITurnLimit"/>) for the driver's engine class.</summary>
        public void Refresh(Kart kart, DifficultySettings difficulty, GameConfig cfg) =>
            turn.Refresh(kart, kart.NormalTopSpeed, cfg.aiTurnRadiusMargin * Mathf.Lerp(cfg.aiImpreciseTurnMargin, 1f, difficulty.aiPrecision),
                cfg.aiCornerGrip);

        /// <summary>
        /// Highest speed now from which the kart can still slow, at <see cref="GameConfig.aiPlanBraking"/>, to every bend of
        /// the followed racing line up to the braking horizon, less the distance its lane <paramref name="laneU"/> runs
        /// inside the line. Infinity when nothing ahead needs braking.
        /// </summary>
        public float CornerSpeed(Kart kart, float speed, float speedScale, float laneU, GameConfig cfg)
        {
            if (!turn.Ready) return float.PositiveInfinity;
            var decel = Mathf.Max(1f, cfg.aiPlanBraking);
            var horizon = Mathf.Max(MinHorizon, speed * speed / (2f * decel) + HorizonMargin);
            var aside = OffRacingLine(kart, laneU);
            var floor = BendSpeedFloor * speedScale;
            var best = float.PositiveInfinity;
            for (var d = Step; d <= horizon; d += Step)
            {
                var radius = line.Radius(d, out var friction);
                var bend = turn.SpeedFor(Mathf.Max(MinBendRadius, radius - aside), friction);
                if (float.IsPositiveInfinity(bend)) continue;
                bend = Mathf.Max(floor, bend);
                best = Mathf.Min(best, Mathf.Sqrt(bend * bend + 2f * decel * (d - Step)));
            }
            return best;
        }

        // The planned lane's distance from the racing line plus how far the kart is off its planned lane (it ran wide and
        // still has to get back, MHK3 Rome).
        float OffRacingLine(Kart kart, float laneU)
        {
            var side = TrackPath.RightOf(line.Tangent(0f, SideTangentSmoothing));
            var miss = Mathf.Abs(Vector3.Dot(kart.transform.position - line.Point(0f, laneU), side));
            return Mathf.Abs(laneU - AILanes.Centre) * line.Width(0f) + miss;
        }
    }
}
