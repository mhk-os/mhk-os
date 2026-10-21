using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Visual-only bobbing of a kart's body while a status effect is active (MHK Extra spike strip: the flat tyres make
    /// the kart hop along). Physics, colliders and the ghost fingerprint never see it.
    /// </summary>
    [Serializable]
    public class BodyBob
    {
        [Tooltip("Peak lift (m) of the kart body, wheels and driver above its normal ride height. 0 = no bob. MHK Extra " +
                 "flat tyres: 0.073.")]
        [Min(0f)] public float height;
        [Tooltip("Bob cycles per second, independent of the kart's speed. MHK Extra flat tyres: 6 (two hops per cycle).")]
        [Min(0f)] public float frequency = 6f;
        [Tooltip("Lift over one cycle (time 0 to 1) as a share of height. Start and end at 0 so the cycles join and the " +
                 "bob starts on the ground. Empty = one smooth hop per cycle.")]
        public AnimationCurve shape = new();
        [Tooltip("Sideways rock (m) of the body, once per cycle. MHK Extra flat tyres: 0.012.")]
        [Min(0f)] public float sway;
        [Tooltip("Nose up and down wobble (degrees), once per cycle. 0 = none.")]
        [Min(0f)] public float pitch;
        [Tooltip("Side to side wobble (degrees), once per cycle, a quarter cycle after the pitch. 0 = none.")]
        [Min(0f)] public float roll;
        [Tooltip("Seconds the bob takes to build up when the effect starts or the kart speeds up again.")]
        [Min(0f)] public float fadeIn = 0.1f;
        [Tooltip("Seconds the bob takes to settle when the effect ends, the kart stops or another hit animates the chassis.")]
        [Min(0f)] public float fadeOut = 0.3f;

        /// <summary>Whether these settings move the body at all.</summary>
        public bool Moves => frequency > 0f && (height > 0f || sway > 0f || pitch > 0f || roll > 0f);

        /// <summary>The one of <paramref name="a"/> and <paramref name="b"/> that moves with the larger height (either may be null).</summary>
        public static BodyBob Stronger(BodyBob a, BodyBob b)
        {
            if (b == null || !b.Moves) return a;
            return a == null || b.height > a.height ? b : a;
        }

        /// <summary>Lift at <paramref name="cycle"/> (0 to 1) of the cycle as a share of <see cref="height"/>.</summary>
        public float Lift(float cycle) =>
            shape != null && shape.length > 0 ? shape.Evaluate(cycle) : 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * cycle);
    }
}
