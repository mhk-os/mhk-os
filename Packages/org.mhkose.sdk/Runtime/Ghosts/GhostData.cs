using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>A recorded run: header, one <see cref="GhostFrame"/> per physics tick from GO, and the visual events.</summary>
    public sealed class GhostData
    {
        // A time within this fraction of the next tick is that tick (double rounding of the race clock).
        const float TickSnapFraction = 0.99999f;

        public GhostHeader header = new();
        public GhostFrame[] frames = Array.Empty<GhostFrame>();
        /// <summary>Events in time order.</summary>
        public List<GhostEvent> events = new();

        /// <summary>Race time of the last recorded tick.</summary>
        public float Duration => Mathf.Max(0, frames.Length - 1) * header.tickSeconds;

        /// <summary>
        /// The recorded state at race time <paramref name="time"/> (s from GO): position by cubic Hermite interpolation
        /// with the recorded velocities, rotation by slerp, the other values as at the last tick. Never interpolates into
        /// a <see cref="GhostFrameFlags.Cut"/> tick. Before GO and after the end: the first and last tick.
        /// </summary>
        public void Sample(double time, out GhostFrame state)
        {
            var n = frames.Length;
            if (n == 0)
            {
                state = default;
                state.rotation = Quaternion.identity;
                return;
            }
            var dt = header.tickSeconds;
            if (!TryTickPosition(time, dt, n, out var i, out var u))
            {
                state = frames[i];
                return;
            }
            state = frames[i];
            if (u <= 0f || i + 1 >= n) return;
            var b = frames[i + 1];
            if (b.Has(GhostFrameFlags.Cut)) return;
            Interpolate(ref state, frames[i], b, dt, u);
        }

        static bool TryTickPosition(double time, float dt, int n, out int i, out float u)
        {
            var x = dt > 0f ? time / dt : 0.0;
            u = 0f;
            if (x <= 0.0 || n == 1)
            {
                i = 0;
                return false;
            }
            if (x >= n - 1)
            {
                i = n - 1;
                return false;
            }
            i = (int)x;
            u = (float)(x - i);
            if (u > TickSnapFraction)
            {
                i++;
                u = 0f;
            }
            return true;
        }

        static void Interpolate(ref GhostFrame state, in GhostFrame a, in GhostFrame b, float dt, float u)
        {
            state.position = Hermite(a.position, a.velocity * dt, b.position, b.velocity * dt, u);
            state.rotation = Quaternion.Slerp(a.rotation, b.rotation, u);
            state.velocity = Vector3.Lerp(a.velocity, b.velocity, u);
        }

        static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float u)
        {
            var u2 = u * u;
            var u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * p0 + (u3 - 2f * u2 + u) * m0 + (-2f * u3 + 3f * u2) * p1 + (u3 - u2) * m1;
        }
    }
}
