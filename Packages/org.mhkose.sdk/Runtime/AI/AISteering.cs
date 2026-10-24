using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>Steering of a computer driver towards its planned lane, with a calm player's tolerance and slide control.</summary>
    internal sealed class AISteering
    {
        // MHK2 look-ahead: (0.1 + speed / 200) of a 45 m plan, plus 2 m. Bends shorten it to about half their radius.
        const float PlanLength = 45f, AheadBase = 0.1f, AheadSpeedDivisor = 200f, AheadExtra = 2f;
        const float MinBend = 0.05f, MinAhead = 3f, TangentSmoothing = 4f, NearTangent = 1f;
        const float YawRateSmoothing = 0.1f, MinPursuitReach = 1f, MinTargetDepth = 0.01f, MinFullLockError = 0.5f;
        const float MinSlideSpeedSqr = 4f;

        readonly AIRouteFollower line;
        float smoothYawRate;
        bool correcting;

        public AISteering(AIRouteFollower line) => this.line = line;

        /// <summary>Look-ahead (m) of the steering target at <paramref name="speed"/>, shortened in bends so the AI does not cut into inner walls.</summary>
        public float Ahead(float speed)
        {
            var ahead = PlanLength * (AheadBase + Mathf.Abs(speed) / AheadSpeedDivisor) + AheadExtra;
            var t0 = line.Tangent(NearTangent, TangentSmoothing);
            var t1 = line.Tangent(ahead, TangentSmoothing);
            var bend = Vector3.Angle(new Vector3(t0.x, 0f, t0.z), new Vector3(t1.x, 0f, t1.z)) * Mathf.Deg2Rad;
            if (bend > MinBend) ahead = Mathf.Min(ahead, Mathf.Max(MinAhead, 0.5f * ahead / bend + AheadExtra));
            return ahead;
        }

        /// <summary>
        /// Steering wish towards lane <paramref name="laneU"/> <paramref name="ahead"/> metres along the line. The wheel stays
        /// straight until the predicted heading error exceeds <see cref="GameConfig.aiSteerTolerance"/>, then corrects until
        /// it is back within <see cref="GameConfig.aiSteerRelease"/>, and never steers further into a slide.
        /// </summary>
        public void Steer(Kart kart, float ahead, float laneU, GameConfig cfg, float dt, ref KartInput input) =>
            SteerAt(kart, line.SmoothPoint(ahead, laneU, ahead * cfg.aiSteerSmoothing), cfg, dt, ref input);

        /// <summary>The same steering wish towards a world point (battles, where drivers leave the racing line).</summary>
        public void SteerAt(Kart kart, Vector3 target, GameConfig cfg, float dt, ref KartInput input)
        {
            var error = HeadingError(kart, target, cfg, dt);
            var size = Mathf.Abs(error);
            correcting = correcting ? size > cfg.aiSteerRelease : size > cfg.aiSteerTolerance;
            var beyond = size - cfg.aiSteerRelease;
            input.steer = !correcting ? 0f : Mathf.Sign(error) * Mathf.Clamp01(beyond / Mathf.Max(MinFullLockError, cfg.aiSteerFullLockError));
            SuppressSlide(kart, cfg, ref input);
        }

        // Angle to the target minus how much faster the kart turns than the arc through the target (pure pursuit) needs,
        // judged aiSteerDamping seconds ahead.
        float HeadingError(Kart kart, Vector3 target, GameConfig cfg, float dt)
        {
            var local = kart.transform.InverseTransformPoint(target);
            var alpha = Mathf.Atan2(local.x, Mathf.Max(MinTargetDepth, local.z)) * Mathf.Rad2Deg;
            if (local.z < 0f) alpha = local.x >= 0f ? 90f : -90f;
            var reach = Mathf.Max(MinPursuitReach, new Vector2(local.x, local.z).magnitude);
            var needed = kart.Physics.ForwardSpeed * 2f * Mathf.Sin(Mathf.Clamp(alpha, -90f, 90f) * Mathf.Deg2Rad) / reach * Mathf.Rad2Deg;
            var yawRate = kart.Physics.Body.angularVelocity.y * Mathf.Rad2Deg;
            smoothYawRate = Mathf.Lerp(smoothYawRate, yawRate, 1f - Mathf.Exp(-dt / YawRateSmoothing));
            return alpha - (smoothYawRate - needed) * cfg.aiSteerDamping;
        }

        // While the nose points more than aiSlideAngle into the turn from the direction of travel (the rear breaking away on
        // snow or ice), it only steers straight or against the slide.
        static void SuppressSlide(Kart kart, GameConfig cfg, ref KartInput input)
        {
            var v = kart.Physics.Body.linearVelocity;
            if (input.steer == 0f || v.x * v.x + v.z * v.z < MinSlideSpeedSqr) return;
            var forward = kart.transform.forward;
            var drift = Vector3.SignedAngle(new Vector3(v.x, 0f, v.z), new Vector3(forward.x, 0f, forward.z), Vector3.up);
            if (Mathf.Abs(drift) > cfg.aiSlideAngle && input.steer * drift > 0f) input.steer = 0f;
        }
    }
}
