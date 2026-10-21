using UnityEngine;

namespace MHK.Core
{
    /// <summary>Frame-rate independent exponential smoothing.</summary>
    public static class Smoothing
    {
        /// <summary>Lerp factor that moves a value toward its target at <paramref name="rate"/> per second over <paramref name="dt"/>.</summary>
        public static float Factor(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);
    }
}
