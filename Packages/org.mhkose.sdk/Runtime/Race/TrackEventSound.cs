using MHK.Audio;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// One-shot track sound on a fixed cycle of the <see cref="RaceClock"/> (TINcan's musket shot and lightning): plays
    /// whenever the race time modulo <see cref="period"/> enters the window (<see cref="windowStart"/>,
    /// <see cref="windowEnd"/>), in step with the track's <see cref="PropAnimator"/>s, which run on the same clock.
    /// </summary>
    public class TrackEventSound : MonoBehaviour
    {
        [Tooltip("One-shot clip.")]
        public AudioClip clip;
        [Tooltip("Cycle length (s).")]
        [Min(0.01f)] public float period = 1f;
        [Tooltip("The sound starts when the cycle time passes this (s).")]
        public float windowStart;
        [Tooltip("End of the trigger window within the cycle (s).")]
        public float windowEnd = 0.1f;
        [Tooltip("The sound's own level.")]
        [Range(0f, 1f)] public float volume = 1f;
        [Tooltip("How far the sound carries.")]
        public SoundRange range = new();

        bool inside;

        // Loading inside the window does not fire: the shot belongs to the window's start.
        void OnEnable() => inside = true;

        void Update()
        {
            var phase = RaceClock.Cycle(RaceClock.RenderSeconds, period, false);
            var now = phase > windowStart && phase < windowEnd;
            if (now && !inside) AudioManager.PlaySfx(clip, transform.position, volume, 1f, range);
            inside = now;
        }
    }
}
