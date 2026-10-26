using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.Validation
{
    /// <summary>The physics a validated kart drives with and the limits derived from it.</summary>
    public sealed class KartSpec
    {
        public readonly KartPhysicsProfile profile;
        public readonly float classFactor, speedScale, torqueScale;
        /// <summary>Top speed at full throttle on flat road without effects (m/s).</summary>
        public readonly float flatTopSpeed;
        /// <summary>Top-gear redline speed without effects (m/s): what speed pads raise a kart to.</summary>
        public readonly float redlineSpeed;
        /// <summary>Largest engine acceleration without effects (m/s²).</summary>
        public readonly float maxDriveAcceleration;
        /// <summary>Chassis box in kart space.</summary>
        public readonly Vector3 chassisCentre, chassisHalfExtents;

        public KartSpec(KartPhysicsProfile profile, float classFactor, float speedScale, float torqueScale)
        {
            this.profile = profile;
            this.classFactor = classFactor;
            this.speedScale = speedScale;
            this.torqueScale = torqueScale;
            flatTopSpeed = Mathf.Max(1f, KartPhysics.FlatTopSpeed(profile, classFactor, speedScale, torqueScale));
            redlineSpeed = Mathf.Max(flatTopSpeed, KartPhysics.RedlineSpeed(profile, classFactor, speedScale));
            maxDriveAcceleration = KartPhysics.MaxDriveAcceleration(profile, classFactor, speedScale, torqueScale);
            KartPhysics.ChassisBox(profile, out chassisCentre, out chassisHalfExtents);
        }

        /// <summary>The spec of <paramref name="profile"/> in engine class <paramref name="engineClass"/> with this game's config.</summary>
        public static KartSpec ForEngineClass(KartPhysicsProfile profile, int engineClass, float speedScale)
        {
            var cfg = GameRoot.Instance.Config;
            return new KartSpec(profile, cfg.GetDifficulty((Difficulty)engineClass).engineClassFactor, speedScale, cfg.driveTorqueScale);
        }

        /// <summary>Same physics (re-simulation karts are shared between equal specs).</summary>
        public bool SamePhysics(KartSpec o) =>
            o != null && o.profile == profile && o.classFactor == classFactor && o.speedScale == speedScale && o.torqueScale == torqueScale;
    }
}
