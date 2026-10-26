using System;
using MHK.Karts;
using UnityEngine;

namespace MHK.Validation
{
    /// <summary>Per-sample flags of a <see cref="KartSample"/>.</summary>
    [Flags]
    public enum KartSampleFlags : byte
    {
        None = 0,
        /// <summary>The kart was put somewhere (respawn) since the previous sample: no motion connects the two.</summary>
        Cut = 1,
        /// <summary>The kart took its driver's input (false before GO and while held by the race).</summary>
        ControlsEnabled = 2,
        /// <summary>Past the finish line.</summary>
        RaceOver = 4,
    }

    /// <summary>
    /// The effective input the physics stepped with, as the keys of MHK2's input model. The bit values equal the
    /// matching ones of the ghost format's input keys, so recorded keys convert by masking.
    /// </summary>
    [Flags]
    public enum KartKeys : byte
    {
        None = 0,
        SteerLeft = 1,
        SteerRight = 2,
        Throttle = 4,
        Brake = 8,
        /// <summary>Held by the race (countdown, frozen): full brake.</summary>
        Parked = 16,
        All = SteerLeft | SteerRight | Throttle | Brake | Parked,
    }

    /// <summary>
    /// One observed state of a kart at a physics tick: from a recorded ghost every tick, from network snapshots of a remote
    /// kart every few ticks. <see cref="velocity"/> is the velocity the kart moves on with from this tick (the rigidbody's
    /// velocity after the kart's step, before the physics simulation moved it).
    /// </summary>
    public struct KartSample
    {
        /// <summary>Physics ticks since GO (race time = tick × the tick length).</summary>
        public int tick;
        public Vector3 position;
        public Quaternion rotation;
        /// <summary>World velocity (m/s).</summary>
        public Vector3 velocity;
        /// <summary>Wheels on the ground, -1 when unknown.</summary>
        public int contacts;
        /// <summary>Steering command, share of full lock (-1..1, <see cref="KartPhysics.SteerCommand01"/>).</summary>
        public float steerCommand;
        /// <summary>Keys the step of this tick used (held over the ticks up to the next sample).</summary>
        public KartKeys keys;
        public KartSampleFlags flags;
        /// <summary>Status effects active at this tick, as bits of the validator's <see cref="EffectSet"/>.</summary>
        public ulong effects;

        public bool Has(KartSampleFlags f) => (flags & f) != 0;

        /// <summary>The kart input the keys stand for.</summary>
        public KartInput Input => new()
        {
            steer = (keys & KartKeys.SteerLeft) != 0 ? -1f : (keys & KartKeys.SteerRight) != 0 ? 1f : 0f,
            throttle = (keys & KartKeys.Throttle) != 0 ? 1f : 0f,
            brake = (keys & KartKeys.Brake) != 0 ? 1f : 0f,
            parked = (keys & KartKeys.Parked) != 0,
        };
    }

    /// <summary>A problem a <see cref="KartValidator"/> found: why, when (race seconds from GO) and a technical detail.</summary>
    public readonly struct Violation
    {
        public readonly Online.FlagReason reason;
        public readonly int tick;
        public readonly float time;
        public readonly string detail;

        public Violation(Online.FlagReason reason, int tick, float time, string detail)
        {
            this.reason = reason;
            this.tick = tick;
            this.time = time;
            this.detail = detail;
        }

        public override string ToString() => $"{reason} at {time:0.00} s: {detail}";
    }
}
