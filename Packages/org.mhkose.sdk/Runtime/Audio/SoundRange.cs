using System;
using UnityEngine;

namespace MHK.Audio
{
    /// <summary>
    /// How far a one-shot sound carries (<see cref="AudioManager.PlaySfx"/>): full volume near the listener, a shaped fade
    /// to <see cref="minVolume"/>, and that floor at any greater distance, so a floor above 0 makes the sound global.
    /// </summary>
    [Serializable]
    public class SoundRange
    {
        const int RolloffSamples = 16;

        [Tooltip("Full volume within this distance of the listener (m).")]
        [Min(0f)] public float fullVolumeDistance = 4f;
        [Tooltip("The fade reaches the floor volume at this distance (m) and stays there beyond it.")]
        [Min(0f)] public float fadeDistance = 120f;
        [Tooltip("Volume floor beyond the fade distance, as a share of the sound's volume (0 = silent there, above 0 = heard anywhere).")]
        [Range(0f, 1f)] public float minVolume;
        [Tooltip("Shape of the fade over the distance from full volume (x 0) to the fade distance (x 1): 1 = full volume, " +
                 "0 = floor. Empty = linear.")]
        public AnimationCurve fade = new();

        [NonSerialized] AnimationCurve rolloff;

        /// <summary>Share of the sound's volume heard at <paramref name="distance"/> metres from the listener.</summary>
        public float Gain(float distance)
        {
            if (distance <= fullVolumeDistance) return 1f;
            var span = fadeDistance - fullVolumeDistance;
            var x = span > 0f ? Mathf.Clamp01((distance - fullVolumeDistance) / span) : 1f;
            var shape = fade != null && fade.length > 0 ? Mathf.Clamp01(fade.Evaluate(x)) : 1f - x;
            return Mathf.Lerp(minVolume, 1f, shape);
        }

        /// <summary>
        /// Sets <paramref name="source"/> up to attenuate like <see cref="Gain"/>: a custom rolloff over
        /// <see cref="fadeDistance"/>, beyond which Unity holds the last value.
        /// </summary>
        public void ApplyTo(AudioSource source)
        {
            var max = Mathf.Max(fadeDistance, fullVolumeDistance, 0.01f);
            source.rolloffMode = AudioRolloffMode.Custom;
            source.minDistance = Mathf.Min(fullVolumeDistance, max);
            source.maxDistance = max;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, Rolloff(max));
        }

        // Piecewise linear through Gain samples. Cached once: the settings are content data and do not change in play.
        AnimationCurve Rolloff(float max)
        {
            if (rolloff != null) return rolloff;
            var values = new float[RolloffSamples + 1];
            for (var i = 0; i <= RolloffSamples; i++) values[i] = Gain(max * i / RolloffSamples);
            var keys = new Keyframe[RolloffSamples + 1];
            for (var i = 0; i <= RolloffSamples; i++)
            {
                var slopeIn = i > 0 ? (values[i] - values[i - 1]) * RolloffSamples : 0f;
                var slopeOut = i < RolloffSamples ? (values[i + 1] - values[i]) * RolloffSamples : 0f;
                keys[i] = new Keyframe((float)i / RolloffSamples, values[i], slopeIn, slopeOut);
            }
            rolloff = new AnimationCurve(keys);
            return rolloff;
        }
    }
}
