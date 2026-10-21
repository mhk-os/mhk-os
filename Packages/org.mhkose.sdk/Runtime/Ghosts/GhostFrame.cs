using System;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>Per-tick flags of a <see cref="GhostFrame"/>.</summary>
    [Flags]
    public enum GhostFrameFlags
    {
        None = 0,
        /// <summary>The kart took its driver's input (false before GO).</summary>
        ControlsEnabled = 1,
        /// <summary>Past the finish line.</summary>
        RaceOver = 2,
        LookBack = 4,
        Burned = 8,
        /// <summary>The kart was teleported (respawn) at this tick: no interpolation from the tick before.</summary>
        Cut = 16,
    }

    /// <summary>
    /// The effective input the physics stepped with at one tick, as the keys MHK2's input model knows (steering sign,
    /// throttle and brake on), enough to re-simulate the run exactly.
    /// </summary>
    [Flags]
    public enum GhostInputKeys
    {
        None = 0,
        SteerLeft = 1,
        SteerRight = 2,
        Throttle = 4,
        Brake = 8,
        /// <summary>Held by the race (countdown, frozen): full brake.</summary>
        Parked = 16,
        Fire = 32,
        LookBack = 64,
        Respawn = 128,
    }

    /// <summary>
    /// State of the recorded kart at one physics tick: pose, velocity and everything <see cref="Karts.IKartView"/> exposes.
    /// Tick n is the state at race time n × <see cref="GhostHeader.tickSeconds"/>.
    /// </summary>
    public struct GhostFrame
    {
        public Vector3 position;
        public Quaternion rotation;
        /// <summary>World velocity (m/s).</summary>
        public Vector3 velocity;
        /// <summary>Speed along the kart's forward axis (m/s).</summary>
        public float forwardSpeed;
        /// <summary>Front wheel angle (degrees).</summary>
        public float steerAngle;
        /// <summary>Steering command, share of full lock (-1..1).</summary>
        public float steerCommand;
        /// <summary>The driver's input as the visuals read it (analog values, look-back is a flag).</summary>
        public float inputSteer, inputThrottle, inputBrake;
        /// <summary>The effective input the physics stepped with at this tick (validation by re-simulation).</summary>
        public GhostInputKeys keys;
        public GhostFrameFlags flags;
        /// <summary>Wheels on the ground.</summary>
        public int contacts;
        /// <summary>Visual yaw offset of spinning effects (degrees).</summary>
        public float spinAngle;
        public float scale, verticalScale, opacity;
        /// <summary>Camera field-of-view boost of the active effects (degrees).</summary>
        public float fovBoost;

        public bool Has(GhostFrameFlags f) => (flags & f) != 0;
    }
}
