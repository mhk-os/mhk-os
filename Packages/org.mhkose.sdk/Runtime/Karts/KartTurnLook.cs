using System;
using MHK.Core;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Visual-only drift look of a kart, live or ghost: while its rear slides out at speed the body turns further into
    /// the slide. Measured from the motion of the kart root, so physics and gameplay never see it.
    /// </summary>
    public sealed class KartTurnLook
    {
        /// <summary>Tunables of the turn look (GameConfig.kartTurnLook).</summary>
        [Serializable]
        public class Settings
        {
            [Tooltip("Largest yaw (degrees) of the kart body into a drift, on top of the physics heading the race camera " +
                     "follows, so the camera sees the kart's side. The physics, colliders, minimap and item targeting keep " +
                     "the real heading. 0 = off.")]
            [Range(0f, 45f)] public float maxYaw = 20f;
            [Tooltip("Body roll (degrees) towards the outside of the drift at maxYaw, less with a smaller yaw. 0 = none.")]
            [Range(0f, 10f)] public float maxRoll = 3f;
            [Tooltip("Slip angle of the rear axle (degrees between its direction of travel and the kart's heading) where " +
                     "the yaw starts. Cornering on grip stays below it (about 3 on road at full speed), so only drifts and " +
                     "slides show the look.")]
            [Min(0f)] public float slipStart = 5f;
            [Tooltip("Rear slip angle (degrees) where the yaw reaches maxYaw. Drifts on snow and ice slip 10 to 20.")]
            [Min(0f)] public float slipFull = 15f;
            [Tooltip("Steering (share of full lock, computer drivers' smoothed steering) needed to keep the look. Letting " +
                     "go of the steering ends it even while the kart still slides. 0 = slides show it without steering.")]
            [Range(0f, 1f)] public float steerStart = 0.1f;
            [Tooltip("Seconds the look stays after the steering dropped below steerStart, so tapping the steering key " +
                     "through a drift keeps it.")]
            [Min(0f)] public float steerHold = 0.1f;
            [Tooltip("Speed as a share of the kart's normal top speed where the yaw starts. Slower karts show none.")]
            [Min(0f)] public float speedStart = 0.2f;
            [Tooltip("Speed share where the yaw reaches its full size.")]
            [Min(0f)] public float speedFull = 0.5f;
            [Tooltip("Rate (1/s) at which the yaw builds up when the slide grows.")]
            [Min(0.1f)] public float buildRate = 6f;
            [Tooltip("Rate (1/s) at which the yaw returns when the slide or the steering ends (20: 95 % back in 0.15 s).")]
            [Min(0.1f)] public float returnRate = 20f;
            [Tooltip("Rate (1/s) of the filter on the measured slip angle: lower ignores short wiggles, higher reacts sooner.")]
            [Min(0.1f)] public float slipFilter = 15f;
            [Tooltip("Point the body turns about, between the rear axle (-1), the middle of the axles (0) and the front axle " +
                     "(1). Towards the front the nose stays on its line and the rear swings out further.")]
            [Range(-1f, 1f)] public float pivot = 0.6f;
            [Tooltip("Seconds to fade the turn look out while the chassis plays an animation (hits, collisions, landings, " +
                     "drops, respawns, spin-outs, flattening, the finish pose) and back in after it.")]
            [Min(0.01f)] public float fadeSeconds = 0.25f;
        }

        const float TeleportSpeed = 150f;
        const float MinTravelSpeed = 0.5f;
        const float NoModelRearAxle = -0.5f;

        readonly Settings settings;
        readonly Transform root;
        readonly Vector3 pivot, rearAxle;
        Vector3 lastRear;
        bool tracking;
        float slip, yaw, weight = 1f, sinceSteer = float.MaxValue;

        /// <summary>
        /// Turn look of the kart whose root is <paramref name="root"/>, turning <paramref name="body"/> about a point between
        /// the axles of <paramref name="model"/> (its wheels, at rest in body space), about the body's origin without them.
        /// </summary>
        public KartTurnLook(Settings settings, Transform root, KartModel model, Transform body)
        {
            this.settings = settings;
            this.root = root;
            var front = 0f;
            var rear = NoModelRearAxle;
            var hasAxles = model != null && TryAxleZ(model.frontWheels, body, out front) & TryAxleZ(model.rearWheels, body, out rear);
            if (!hasAxles) rear = NoModelRearAxle;
            pivot = hasAxles ? PivotPoint(settings.pivot, front, rear) : Vector3.zero;
            rearAxle = new Vector3(0f, 0f, rear);
        }

        /// <summary>Filtered slip angle of the rear axle (degrees, positive = sliding to the kart's right).</summary>
        public float Slip => slip;

        /// <summary>Yaw (degrees, positive = right) shown now, faded out while the chassis animates.</summary>
        public float Yaw => yaw * weight;

        /// <summary>Local rotation of the body about its pivot: the yaw and the outward roll.</summary>
        public Quaternion Rotation
        {
            get
            {
                var shown = Yaw;
                var roll = settings.maxYaw > 0f ? settings.maxRoll * shown / settings.maxYaw : 0f;
                return Quaternion.Euler(0f, shown, roll);
            }
        }

        /// <summary>Local position of the body that keeps its pivot in place under <see cref="Rotation"/>.</summary>
        public Vector3 PivotOffset => pivot - Rotation * pivot;

        /// <summary>Forgets the motion and drops the yaw at once (respawns and other teleports).</summary>
        public void Reset()
        {
            tracking = false;
            slip = yaw = 0f;
        }

        /// <summary>
        /// Advances by <paramref name="dt"/> real seconds at <paramref name="speedFraction"/> of the kart's top speed with
        /// the shown <paramref name="steer"/> (share of full lock). <paramref name="chassisAnimating"/> fades the look out.
        /// </summary>
        public void Update(float dt, float speedFraction, float steer, bool chassisAnimating)
        {
            if (dt <= 0f) return;
            MeasureSlip(dt);
            sinceSteer = Mathf.Abs(steer) >= settings.steerStart ? 0f : sinceSteer + dt;
            var target = 0f;
            if (sinceSteer <= settings.steerHold)
                target = -Mathf.Sign(slip) * settings.maxYaw * Ramp(Mathf.Abs(slip), settings.slipStart, settings.slipFull)
                         * Ramp(speedFraction, settings.speedStart, settings.speedFull);
            var building = Mathf.Abs(target) > Mathf.Abs(yaw) && target * yaw >= 0f;
            yaw = Mathf.Lerp(yaw, target, Smoothing.Factor(building ? settings.buildRate : settings.returnRate, dt));
            weight = Mathf.MoveTowards(weight, chassisAnimating ? 0f : 1f, dt / settings.fadeSeconds);
        }

        // The rear axle's direction of travel against the heading: zero when the tyres grip, the drift angle when it slides.
        void MeasureSlip(float dt)
        {
            var rear = root.TransformPoint(rearAxle);
            var velocity = (rear - lastRear) / dt;
            var wasTracking = tracking;
            lastRear = rear;
            tracking = true;
            if (!wasTracking) return;
            if (velocity.sqrMagnitude > TeleportSpeed * TeleportSpeed)
            {
                Reset();
                return;
            }
            var along = Vector3.Dot(velocity, root.forward);
            var side = Vector3.Dot(velocity, root.right);
            var angle = Mathf.Abs(along) + Mathf.Abs(side) > MinTravelSpeed ? Mathf.Atan2(side, Mathf.Abs(along)) * Mathf.Rad2Deg : 0f;
            slip = Mathf.Lerp(slip, angle, Smoothing.Factor(settings.slipFilter, dt));
        }

        static float Ramp(float x, float start, float full) =>
            full > start ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, full, x)) : x >= start ? 1f : 0f;

        static Vector3 PivotPoint(float share, float front, float rear)
        {
            var middle = 0.5f * (front + rear);
            return new Vector3(0f, 0f, share >= 0f ? Mathf.Lerp(middle, front, share) : Mathf.Lerp(middle, rear, -share));
        }

        static bool TryAxleZ(Transform[] wheels, Transform body, out float z)
        {
            z = 0f;
            var n = 0;
            foreach (var w in wheels)
            {
                if (w == null) continue;
                z += body.InverseTransformPoint(w.position).z;
                n++;
            }
            if (n > 0) z /= n;
            return n > 0;
        }
    }
}
