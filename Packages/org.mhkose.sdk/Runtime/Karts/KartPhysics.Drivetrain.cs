using MHK.Core;
using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        /// <summary>Lower clamp of <see cref="SpeedScale"/> wherever speeds convert to original units.</summary>
        public const float MinSpeedScale = 0.05f;
        const float MaxSpeedScale = 4f;
        const float MinClassFactor = 0.1f;
        const float RpmPerWheelSpin = 0.08333f;
        const float RadPerSecToRpm = 9.5493f;
        const float FallbackMaxSpeed = 30f, FallbackMaxRpm = 6000f;
        const int BisectionSteps = 40;
        /// <summary>RW body linear damping (force = −d·m·v, solver default 0x4d5960).</summary>
        const float LinearDamping = 0.01f;

        /// <summary>
        /// Speed scale of the current session's engine class (<see cref="GameConfig.SpeedScale"/>), 100cc without a
        /// session, 1 outside play mode. For code without a kart at hand: every kart takes it on spawn.
        /// </summary>
        public static float GlobalSpeedScale
        {
            get
            {
                var root = Application.isPlaying ? GameRoot.Instance : null;
                if (root == null || root.Config == null) return 1f;
                return root.Config.SpeedScale(root.Session != null ? root.Session.difficulty : Difficulty.Medium);
            }
        }

        static float OffroadResistance => TryConfig(out var c) ? c.offroadResistance : 0.15f;
        static float AirHeadingAlign => TryConfig(out var c) ? c.airHeadingAlign : 0f;
        static float DriveTorqueScale => TryConfig(out var c) ? Mathf.Max(0.1f, c.driveTorqueScale) : 1f;

        static bool TryConfig(out GameConfig config)
        {
            var root = GameRoot.Instance;
            config = root != null ? root.Config : null;
            return config != null;
        }

        static float ClampClassFactor(float classFactor) => Mathf.Max(MinClassFactor, classFactor);
        static float ClampSpeedScale(float speedScale) => Mathf.Clamp(speedScale, MinSpeedScale, MaxSpeedScale);
        static float DiffRatio(KartPhysicsProfile p, float classFactor, float speedScale) => p.diffRatio / (classFactor * speedScale);

        // t·T(x)·torqueScale − (1−t)·E(x), T scaled by class factor and mass, E by mass (0x440110). torqueScale
        // (GameConfig.driveTorqueScale, remake) only scales the drive torque.
        static float TorqueCurve(KartPhysicsProfile p, float rpm, float throttle, float classFactor, float torqueScale)
        {
            p.EvaluateTorque(rpm, out var t, out var e);
            throttle = Mathf.Clamp01(throttle);
            return throttle * t * classFactor * torqueScale * p.mass - (1f - throttle) * e * p.mass;
        }

        /// <summary>Theoretical top speed (m/s) in top gear at the end of the torque curve (section 7).</summary>
        public float MaxSpeed() => RedlineSpeed(effects != null ? effects.MaxSpeedMultiplier : 1f);

        float RedlineSpeed(float maxSpeedMultiplier)
        {
            var p = profile;
            if (p == null) return FallbackMaxSpeed;
            var maxRpm = p.torqueCurve.Length > 0 ? p.torqueCurve[^1].rpm : FallbackMaxRpm;
            var top = p.gears.Length > 0 ? p.gears[^1] : 1f;
            var diff = DiffRatio(p, ClampClassFactor(ClassFactor), ClampSpeedScale(SpeedScale));
            return maxRpm * p.wheelsRadiusRear / (top * diff * RadPerSecToRpm) * maxSpeedMultiplier;
        }

        // Remake: above MaxSpeed the original engine gives no force at full throttle, so a boost's excess speed only fades
        // through linear damping. After a speed boost the speed above MaxSpeed (at least the unmodified one, hits that
        // lower the top speed brake nothing extra) decays at the profile's rate. Overspeed without a boost stays untouched.
        void ApplyBoostOverspeed(ref StepFrame s)
        {
            if (s.fx.speedBoost) boostOverspeed = true;
            if (!boostOverspeed) return;
            var excess = ForwardSpeed - RedlineSpeed(Mathf.Max(1f, s.fx.maxSpeed));
            if (excess <= 0f)
            {
                boostOverspeed = s.fx.speedBoost;
                return;
            }
            if (s.contacts > 0) s.force -= s.fwd * (s.p.boostOverspeedDecay * s.m * excess * (s.contacts / 4f));
        }

        /// <summary>
        /// Top speed (m/s) of <paramref name="p"/> at full throttle on flat ground without effects: the highest speed at
        /// which the drive force of the gear the box settles in balances linear damping, air drag and the ground's
        /// <paramref name="resistance"/> (0 = road, <see cref="GameConfig.offroadResistance"/> offroad), the equilibrium
        /// <see cref="Step"/> converges to.
        /// </summary>
        public static float FlatTopSpeed(KartPhysicsProfile p, float classFactor, float speedScale, float torqueScale, float resistance = 0f)
        {
            if (p == null || p.gears == null || p.gears.Length == 0) return 0f;
            var model = new FlatSpeedModel(p, ClampClassFactor(classFactor), ClampSpeedScale(speedScale), torqueScale, resistance);
            var g = 0;
            while (true)
            {
                var v = model.Equilibrium(g);
                if (g < p.gears.Length - 1 && model.Rpm(g, v) > p.changeUpRpm) g++;
                else return v;
            }
        }

        /// <summary>
        /// Speed scale at which <paramref name="reference"/> reaches <paramref name="topSpeed"/> m/s
        /// (<see cref="FlatTopSpeed"/>) in engine class <paramref name="classFactor"/> with drive torque ×
        /// <paramref name="torqueScale"/>.
        /// </summary>
        public static float SpeedScaleFor(KartPhysicsProfile reference, float classFactor, float topSpeed, float torqueScale)
        {
            float lo = MinSpeedScale, hi = MaxSpeedScale;
            for (var i = 0; i < BisectionSteps; i++)
            {
                var mid = 0.5f * (lo + hi);
                if (FlatTopSpeed(reference, classFactor, mid, torqueScale) < topSpeed) lo = mid;
                else hi = mid;
            }
            return 0.5f * (lo + hi);
        }

        readonly struct FlatSpeedModel
        {
            const float MaxSearchSpeed = 1000f;
            const float SearchSteps = 400f;

            readonly KartPhysicsProfile p;
            readonly float f, s, diff, wheelForce, drag, torqueScale;

            public FlatSpeedModel(KartPhysicsProfile p, float f, float s, float torqueScale, float resistance)
            {
                this.p = p;
                this.f = f;
                this.s = s;
                this.torqueScale = torqueScale;
                diff = DiffRatio(p, f, s);
                wheelForce = p.torqueCoefficientFront / Mathf.Max(0.01f, p.wheelsRadiusFront)
                             + p.torqueCoefficientRear / Mathf.Max(0.01f, p.wheelsRadiusRear);
                drag = Mathf.Abs(p.dragCoefficient) + Mathf.Max(0f, resistance) / s;
            }

            /// <summary>Post-step rpm at <paramref name="v"/>: |ratio · diff · (spin RL + spin RR) · 0.08333|, spin = v / r · Rad2Deg.</summary>
            public float Rpm(int gear, float v) => Mathf.Abs(p.gears[gear] * diff * 2f * v / Mathf.Max(0.01f, p.wheelsRadiusRear) * Mathf.Rad2Deg * RpmPerWheelSpin);

            float Net(int gear, float v)
            {
                var drive = p.gears[gear] * diff * TorqueCurve(p, Rpm(gear, v), 1f, f, torqueScale) * (s * s) * wheelForce;
                return drive - (LinearDamping * p.mass * v + drag * v * v);
            }

            /// <summary>Highest balance point: step down from above to the first speed with spare drive force, then bisect.</summary>
            public float Equilibrium(int gear)
            {
                var hi = 1f;
                while (Net(gear, hi) >= 0f && hi < MaxSearchSpeed) hi *= 2f;
                var step = hi / SearchSteps;
                var lo = hi;
                while (lo > 0f && Net(gear, lo) < 0f) lo -= step;
                if (lo <= 0f) return 0f;
                var up = lo + step;
                for (var i = 0; i < BisectionSteps; i++)
                {
                    var mid = 0.5f * (lo + up);
                    if (Net(gear, mid) >= 0f) lo = mid;
                    else up = mid;
                }
                return lo;
            }
        }
    }
}
