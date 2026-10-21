using MHK.Karts;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Visual-only earthquake that makes a kart's visual hop (MHK Extra 16-ton weight impact). It sits on the visual root
    /// and applies its offset as a delta, and the offset is a pure function of time (<see cref="VisualOffset"/>).
    /// </summary>
    public class KartQuake : MonoBehaviour
    {
        const float EdgeAmplitudeLoss = 0.7f;

        float duration, amplitude, frequency, startTime, decay;
        Vector3 applied;

        /// <summary>
        /// Shakes the visuals of all karts within <paramref name="radius"/> of <paramref name="center"/> for
        /// <paramref name="duration"/> seconds. The hop height follows <paramref name="falloff"/> over the distance.
        /// </summary>
        /// <param name="center">Impact point.</param>
        /// <param name="radius">Karts farther away do not shake (m).</param>
        /// <param name="duration">Length of the quake (s).</param>
        /// <param name="amplitude">Peak hop height (m) at the centre.</param>
        /// <param name="frequency">Hops per second (duration × frequency = number of hops).</param>
        /// <param name="waveSpeed">The quake spreads outwards at this speed (m/s, 0 = everywhere at once).</param>
        /// <param name="decay">Share of the amplitude lost by the end of the quake (0 = equal hops, 1 = fade to nothing).</param>
        /// <param name="falloff">Share of <paramref name="amplitude"/> over the distance divided by the radius (0 to 1).
        /// Null or empty = linear fade to 30 % at the radius.</param>
        /// <param name="innerRadius">Karts closer than this do not shake (m), e.g. a kart pinned under the impacting object.</param>
        public static void Shake(Vector3 center, float radius, float duration, float amplitude, float frequency,
            float waveSpeed = 0f, float decay = 0.5f, AnimationCurve falloff = null, float innerRadius = 0f)
        {
            if (radius <= 0f || duration <= 0f || amplitude <= 0f) return;
            foreach (var k in Kart.All)
            {
                if (k == null || k.Visual == null) continue;
                var dist = Vector3.Distance(k.transform.position, center);
                if (dist > radius || dist < innerRadius) continue;
                var height = amplitude * Falloff(falloff, dist / radius);
                if (height <= 0f) continue;
                var go = k.Visual.gameObject;
                if (!go.TryGetComponent<KartQuake>(out var q)) q = go.AddComponent<KartQuake>();
                q.Begin(duration, height, frequency, waveSpeed > 0f ? dist / waveSpeed : 0f, decay);
            }
        }

        static float Falloff(AnimationCurve falloff, float x) =>
            falloff != null && falloff.length > 0 ? Mathf.Max(0f, falloff.Evaluate(x)) : 1f - EdgeAmplitudeLoss * x;

        /// <summary>Current world-space quake offset of a kart's visual (zero when it is not shaking).</summary>
        public static Vector3 VisualOffset(Kart kart)
        {
            if (kart == null || kart.Visual == null || !kart.Visual.TryGetComponent<KartQuake>(out var q) || !q.enabled) return Vector3.zero;
            return q.transform.parent != null ? q.transform.parent.TransformVector(q.Offset(Time.time)) : q.Offset(Time.time);
        }

        void Begin(float seconds, float height, float hz, float startDelay, float fade)
        {
            // A new quake replaces a weaker running one.
            var remaining = enabled && duration > 0f ? amplitude * Mathf.Clamp01(1f - decay * (Time.time - startTime) / duration) : 0f;
            if (remaining > height) return;
            duration = seconds;
            amplitude = height;
            frequency = hz;
            decay = Mathf.Clamp01(fade);
            startTime = Time.time + Mathf.Max(0f, startDelay);
            enabled = true;
        }

        // |sin| bounces starting on the ground, each lower than the last. Farther karts start later, out of step.
        Vector3 Offset(float time)
        {
            var t = time - startTime;
            if (t < 0f || t >= duration) return Vector3.zero;
            var envelope = 1f - decay * t / duration;
            return new Vector3(0f, amplitude * envelope * Mathf.Abs(Mathf.Sin(t * frequency * Mathf.PI)), 0f);
        }

        void LateUpdate()
        {
            var offset = Offset(Time.time);
            transform.localPosition += offset - applied;
            applied = offset;
            if (Time.time - startTime >= duration) enabled = false;
        }

        void OnDisable()
        {
            transform.localPosition -= applied;
            applied = Vector3.zero;
        }
    }
}
