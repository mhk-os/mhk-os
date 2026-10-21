using System;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Vehicle parameters in the units of Moorhuhn Kart 2's <c>settings/karts/karts.txt</c> ("class Car"), field names
    /// mirror the original keys. Original axes: x forward, y up, z right (KartPhysics maps them).
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Kart Physics Profile", fileName = "KartPhysics")]
    public class KartPhysicsProfile : ScriptableObject
    {
        [Serializable]
        public struct TorquePoint
        {
            public float rpm;
            public float torque;
            [Tooltip("Engine braking factor at this rpm ('e braking N').")]
            public float engineBraking;
        }

        static readonly Vector3[] DefaultWheelOffsets =
        {
            new(0.542f, -0.1f, -0.46f), new(0.542f, -0.1f, 0.46f),
            new(-0.487f, -0.1f, -0.46f), new(-0.487f, -0.1f, 0.46f),
        };

        [Header("Chassis")]
        [Tooltip("'mass' (kg).")]
        public float mass = 20f;
        [Tooltip("'chassis size': half extents (x forward, y up, z side) in original axes.")]
        public Vector3 chassisSize = new(0.625f, 0.2f, 0.555f);
        [Tooltip("'center of mass' in original axes.")]
        public Vector3 centerOfMass = new(0.05f, 0f, 0f);
        [Tooltip("'chassis coll offset': centre of the chassis box in original axes.")]
        public Vector3 chassisCollOffset = new(0.0875f, 0f, 0f);

        [Header("Wheels")]
        [Tooltip("Front wheel contact sphere radius (m).")]
        public float wheelsRadiusFront = 0.2f;
        [Tooltip("Rear wheel contact sphere radius (m).")]
        public float wheelsRadiusRear = 0.2f;
        [Tooltip("Front suspension spring stiffness per kg of mass.")]
        public float wheelsStiffnessFront = 20000f;
        [Tooltip("Rear suspension spring stiffness per kg of mass.")]
        public float wheelsStiffnessRear = 20000f;
        [Tooltip("Front suspension damping per kg of mass.")]
        public float wheelsDampingFront = 200f;
        [Tooltip("Rear suspension damping per kg of mass.")]
        public float wheelsDampingRear = 200f;
        [Tooltip("Front tyre side friction coefficient (μ × load).")]
        public float lateralFrictionCoeffFront = 2.2f;
        [Tooltip("Rear tyre side friction coefficient (μ × load).")]
        public float lateralFrictionCoeffRear = 2.2f;
        [Tooltip("Front rolling friction coefficient: braking and traction limit.")]
        public float rollFrictionCoeffFront = 4.4f;
        [Tooltip("Rear rolling friction coefficient: braking and traction limit.")]
        public float rollFrictionCoeffRear = 2.2f;
        [Tooltip("Front friction coefficient once the brake force exceeds maxBrakeFront (locked wheel).")]
        public float rollFrictionCoeffLockedFront = 1f;
        [Tooltip("Rear friction coefficient once the brake force exceeds maxBrakeRear (locked wheel).")]
        public float rollFrictionCoeffLockedRear = 0.5f;
        [Tooltip("'wheels N coll offset' (0 = front left, 1 = front right, 2 = rear left, 3 = rear right).")]
        public Vector3[] wheelCollOffsets = (Vector3[])DefaultWheelOffsets.Clone();
        public float brakeCoefficientFront = 1f;
        public float brakeCoefficientRear = 1f;
        public float maxBrakeFront = 1f;
        public float maxBrakeRear = 1f;
        public float torqueCoefficientFront;
        public float torqueCoefficientRear = 1f;

        [Header("Gears")]
        public float[] gears = { 1f, 1f, 0.75f, 0.5f };
        public float gearReverse = 3f;
        public float diffRatio = 6f;
        public float changeUpRpm = 4000f;

        [Header("Torque curve")]
        public TorquePoint[] torqueCurve =
        {
            new() { rpm = 0, torque = 1.4f, engineBraking = 0f },
            new() { rpm = 2000, torque = 1f, engineBraking = 0f },
            new() { rpm = 4000, torque = 0.6f, engineBraking = 0.2f },
            new() { rpm = 6000, torque = 0f, engineBraking = 2f },
        };

        [Header("Misc")]
        [Tooltip("Steering wash-out: the wheel angle is divided by 1 + washout × |speed|^power.")]
        public float steeringWashout = 0.03f;
        [Tooltip("Speed exponent of the steering wash-out.")]
        public float steeringWashoutPower = 1.6f;
        public float dragCoefficient;
        public float downforceCoefficient = 0.5f;
        [Tooltip("Degrees per second the steering wheel turns while steering.")]
        public float steeringSpeed = 170f;
        [Tooltip("Degrees per second the steering returns to centre when released.")]
        public float steeringRestSpeed = 600f;
        public float maxSteeringAngle = 40f;
        [Tooltip("Throttle ramp speed (1/s).")]
        public float accelerationSpeed = 10f;
        [Tooltip("Brake ramp speed (1/s).")]
        public float brakeSpeed = 100f;
        public float frictionEqualizer = 0.8f;
        public float angularVelDamping = 2f;
        public float angularVelCollisionLimit = 2f;
        public float angularVelCollisionDamping = 12f;
        public float extraSteeringTorque = 3f;
        public float extraSteeringTorqueImpulse;
        public float uprightTorqueFactor = 100f;

        [Header("Remake handling (not in karts.txt)")]
        [Tooltip("Tyre slip angle (deg) at which the lateral friction reaches its limit on gravel-like ground ('lat dir " +
                 "friction' 0.5). Below it the lateral force grows linearly with the slip. Each surface scales it with its " +
                 "grip (TrackSurface.SlipAngleScaleFor: hard ground ×0.44 ≈ 3°, sand ×1.3, snow ×1.85). 0 = the original rigid " +
                 "stick-slip friction (no slip at all until the limit, then free sliding).")]
        public float lateralSlipAngle = 7f;
        [Tooltip("Scale the 'extra steering torque' with the wheels' ground contact and the surface's lateral friction: " +
                 "no yaw torque in the air, less on sand / ice (the original applies it fully everywhere, which spins the " +
                 "kart on low-grip roads and after bumps).")]
        public bool steeringTorqueFollowsGrip = true;
        [Tooltip("Rear lateral grip usage (0..1) above which the extra steering torque fades out, reaching 0 when the rear " +
                 "tyres slide. Keeps a hard corner a controlled drift instead of a spin. 1 = never fade (original).")]
        [Range(0f, 1f)] public float steeringTorqueFadeStart = 0.6f;
        [Tooltip("Share of the gravity component along the ground that acts while the wheels touch it (uphill braking, " +
                 "downhill pull, sliding down sideways). 1 = original physics, 0.25 keeps steep ramps drivable. Tracks can " +
                 "override it (TrackDefinition.physics, MHK3: 0). The normal component and airborne gravity are unchanged.")]
        [Range(0f, 1f)] public float slopeGravityFactor = 0.25f;
        [Tooltip("Rate (1/s) at which the forward speed above the kart's current top speed decays after a speed boost " +
                 "(an effect with a max speed or acceleration multiplier above 1 or a forward impulse: turbo can, speed pad, " +
                 "power start) while the wheels touch the ground. The excess shrinks to 37 % every 1/rate seconds. The top " +
                 "speed is the end of the torque curve in top gear times the active max speed multipliers (at least 1), so " +
                 "a running boost still reaches its speed and hits that lower it brake nothing extra. Overspeed without a " +
                 "boost (downhill) is not braked. 2.5 = within 2 % of the normal top speed 0.7 to 1.5 s after a boost " +
                 "ends. 0 = MHK2 (only linear damping, about 13 s after a turbo can).")]
        [Min(0f)] public float boostOverspeedDecay = 2.5f;

        /// <summary>Default offset of wheel <paramref name="wheel"/> (0..3, see wheelCollOffsets), original axes.</summary>
        public static Vector3 DefaultWheelOffset(int wheel) => DefaultWheelOffsets[wheel];

        /// <summary>Linear interpolation of the torque curve at <paramref name="rpm"/>.</summary>
        public void EvaluateTorque(float rpm, out float torque, out float engineBraking)
        {
            var c = torqueCurve;
            if (c == null || c.Length == 0) { torque = 0; engineBraking = 0; return; }
            if (rpm <= c[0].rpm) { torque = c[0].torque; engineBraking = c[0].engineBraking; return; }
            for (var i = 1; i < c.Length; i++)
            {
                if (rpm <= c[i].rpm)
                {
                    var t = Mathf.InverseLerp(c[i - 1].rpm, c[i].rpm, rpm);
                    torque = Mathf.Lerp(c[i - 1].torque, c[i].torque, t);
                    engineBraking = Mathf.Lerp(c[i - 1].engineBraking, c[i].engineBraking, t);
                    return;
                }
            }
            torque = c[^1].torque;
            engineBraking = c[^1].engineBraking;
        }
    }
}
