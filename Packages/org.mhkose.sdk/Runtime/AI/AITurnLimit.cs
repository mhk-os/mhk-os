using MHK.Karts;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>
    /// How tight a bend a kart can hold at a given speed with the player's keys: the lower of the full-lock steering
    /// radius (with speed wash-out as in <see cref="KartPhysics"/>) and the grip limit.
    /// </summary>
    internal sealed class AITurnLimit
    {
        // Steering table entries from standstill to TopSpeedRange times the kart's top speed.
        const int Samples = 48;
        const float TopSpeedRange = 2.5f;
        const float MinWheelbase = 0.1f, WheelbaseSmoothing = 0.05f;
        const float MinMargin = 0.1f, MinMass = 0.1f;
        const float RebuildTolerance = 0.02f;
        const float MinSteerAngle = 0.5f, MaxSteerAngle = 80f;

        readonly float[] steerRadius = new float[Samples + 1];
        float speedStep;
        float wheelbase;
        float margin = 1f;
        float gripPerFriction;
        float gravity, downforcePerMass;
        KartPhysicsProfile builtProfile;
        float builtWheelbase, builtSteer, builtTop;

        /// <summary>True once the wheelbase is known and the steering table is built.</summary>
        public bool Ready => builtProfile != null && speedStep > 0f;

        /// <summary>
        /// Measures the wheelbase while all four wheels touch the ground, takes the grip state and rebuilds the steering
        /// table when the kart's profile, steering effects or <paramref name="topSpeed"/> changed. Bend radii are judged
        /// <paramref name="radiusMargin"/> times tighter, <paramref name="gripShare"/> is <see cref="Core.GameConfig.aiCornerGrip"/>.
        /// </summary>
        public void Refresh(Kart kart, float topSpeed, float radiusMargin, float gripShare)
        {
            var p = kart.Physics.Profile;
            if (p == null) return;
            MeasureWheelbase(kart);
            if (wheelbase <= 0f) return;
            margin = Mathf.Max(MinMargin, radiusMargin);
            UpdateGrip(kart, p, gripShare);
            var steer = kart.Effects.SteeringMultiplier;
            if (TableUpToDate(p, steer, topSpeed)) return;
            BuildSteerTable(p, steer, topSpeed);
        }

        /// <summary>
        /// Highest speed (m/s) at which a bend of radius <paramref name="bendRadius"/> m on ground of lateral friction
        /// <paramref name="friction"/> can be held. Positive infinity when any speed can, 0 when it is tighter than the
        /// kart turns at walking pace.
        /// </summary>
        public float SpeedFor(float bendRadius, float friction)
        {
            var r = bendRadius / margin;
            return Mathf.Min(SteeringSpeed(r), GripSpeed(r, friction));
        }

        void MeasureWheelbase(Kart kart)
        {
            var phys = kart.Physics;
            if (!phys.WheelOnGround(0) || !phys.WheelOnGround(1) || !phys.WheelOnGround(2) || !phys.WheelOnGround(3)) return;
            var axle = 0.5f * (phys.WheelContactPoint(0) + phys.WheelContactPoint(1) - phys.WheelContactPoint(2) - phys.WheelContactPoint(3));
            var measured = Mathf.Abs(Vector3.Dot(axle, kart.transform.forward));
            if (measured > MinWheelbase) wheelbase = wheelbase <= 0f ? measured : Mathf.Lerp(wheelbase, measured, WheelbaseSmoothing);
        }

        void UpdateGrip(Kart kart, KartPhysicsProfile p, float gripShare)
        {
            gripPerFriction = gripShare * p.lateralFrictionCoeffRear * kart.Effects.GripMultiplier;
            gravity = Physics.gravity.magnitude * kart.Effects.GravityMultiplier;
            downforcePerMass = Mathf.Max(0f, p.downforceCoefficient) / Mathf.Max(MinMass, kart.Physics.Body.mass);
        }

        bool TableUpToDate(KartPhysicsProfile p, float steer, float topSpeed) =>
            p == builtProfile && Mathf.Abs(wheelbase - builtWheelbase) <= RebuildTolerance * builtWheelbase && steer == builtSteer
            && Mathf.Abs(topSpeed - builtTop) <= RebuildTolerance * builtTop;

        void BuildSteerTable(KartPhysicsProfile p, float steer, float topSpeed)
        {
            builtProfile = p;
            builtWheelbase = wheelbase;
            builtSteer = steer;
            builtTop = topSpeed;
            speedStep = Mathf.Max(1f, topSpeed) * TopSpeedRange / Samples;
            for (var i = 0; i <= Samples; i++)
            {
                var v = i * speedStep;
                var angle = p.maxSteeringAngle * steer / (1f + p.steeringWashout * Mathf.Pow(v, p.steeringWashoutPower));
                steerRadius[i] = wheelbase / Mathf.Tan(Mathf.Clamp(angle, MinSteerAngle, MaxSteerAngle) * Mathf.Deg2Rad);
                // Monotonic by construction, guards against odd profiles (negative wash-out power).
                if (i > 0 && steerRadius[i] < steerRadius[i - 1]) steerRadius[i] = steerRadius[i - 1];
            }
        }

        float SteeringSpeed(float r)
        {
            if (r >= steerRadius[Samples]) return float.PositiveInfinity;
            if (r <= steerRadius[0]) return 0f;
            int lo = 0, hi = Samples;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) >> 1;
                if (steerRadius[mid] <= r) lo = mid; else hi = mid;
            }
            var span = steerRadius[hi] - steerRadius[lo];
            var t = span > 1e-5f ? (r - steerRadius[lo]) / span : 0f;
            return (lo + t) * speedStep;
        }

        // v^2 / r = k (g + c v^2) solved for v, with k = grip per friction times friction, c = downforce per mass.
        float GripSpeed(float r, float friction)
        {
            var k = gripPerFriction * Mathf.Max(0f, friction);
            if (k <= 0f) return 0f;
            var den = 1f - r * k * downforcePerMass;
            return den <= 1e-4f ? float.PositiveInfinity : Mathf.Sqrt(r * k * gravity / den);
        }
    }
}
