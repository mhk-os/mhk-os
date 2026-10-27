using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Poses an animated held item model (MHK Extra *_chr: wooden hammer, spike strip) at an explicit clip time,
    /// without an Animator. Put it on the model root. While idle it shows <see cref="idleTime"/> (the held pose).
    /// </summary>
    public class HeldItemAnimation : MonoBehaviour
    {
        const float EndMargin = 0.0001f;

        [Tooltip("Must be a legacy clip: players and mod bundles keep no sampleable curves of a non-legacy clip, " +
                 "so the model would stay in its bind pose.")]
        [SerializeField] AnimationClip clip;
        [Tooltip("Clip time shown when enabled (held pose).")]
        [SerializeField] float idleTime;

        public float Length => clip != null ? clip.length : 0f;

        void OnEnable() => Sample(idleTime);

        /// <summary>Applies the clip's pose at <paramref name="time"/> (clamped to the clip, its last frame at the end) at once.</summary>
        public void Sample(float time)
        {
            if (clip == null) return;
            // The imported clips are legacy clips with wrap mode Loop: SampleAnimation wraps their end time back to the first
            // frame (the spike strip's rest pose would become the held pose). clip.isLooping cannot tell in a built bundle (it
            // reads the loop time setting, which legacy clips do not keep), so the end is always sampled just before it.
            var end = Mathf.Max(0f, clip.length - EndMargin);
            clip.SampleAnimation(gameObject, Mathf.Clamp(time, 0f, end));
        }
    }
}
