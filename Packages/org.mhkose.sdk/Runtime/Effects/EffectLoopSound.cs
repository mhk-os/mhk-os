using MHK.Audio;
using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Looping 3D sound that plays while this component is enabled. Placed on prefabs and scenes, or started by
    /// powerup behaviours through <see cref="Play"/> and <see cref="Stop"/>.
    /// </summary>
    public class EffectLoopSound : MonoBehaviour
    {
        [Tooltip("Looping clip (mono for 3D).")]
        [SerializeField] AudioClip clip;
        [Tooltip("Loop volume (the sound's own level, the effects setting and mix gain apply on top).")]
        [Range(0f, 1f)] [SerializeField] float volume = 1f;
        [Tooltip("Full volume within this distance (m).")]
        [SerializeField] float minDistance = 1f;
        [Tooltip("Silent beyond this distance (m), linear fade in between.")]
        [SerializeField] float maxDistance = 20f;

        AudioSource source;

        /// <summary>Sets the sound before the loop starts (importers, <see cref="Play"/>).</summary>
        public void Configure(AudioClip loopClip, float loopVolume, float fullVolumeDistance, float silentDistance)
        {
            clip = loopClip;
            volume = loopVolume;
            minDistance = fullVolumeDistance;
            maxDistance = silentDistance;
        }

        /// <summary>
        /// Starts <paramref name="sound"/> as a loop on <paramref name="host"/> (following it, ending with it).
        /// Returns null when no clip is set.
        /// </summary>
        public static EffectLoopSound Play(GameObject host, LoopSound sound)
        {
            if (host == null || sound == null || sound.clip == null) return null;
            var loop = host.AddComponent<EffectLoopSound>();
            loop.enabled = false;
            loop.Configure(sound.clip, sound.volume, sound.minDistance, sound.maxDistance);
            loop.enabled = true;
            return loop;
        }

        /// <summary>Stops and removes a loop started with <see cref="Play"/> (no-op for null) and clears the reference.</summary>
        public static void Stop(ref EffectLoopSound loop)
        {
            if (loop != null) Destroy(loop);
            loop = null;
        }

        void Init()
        {
            if (source != null || clip == null) return;
            source = AudioSources.Create3D(gameObject, true, minDistance, Mathf.Max(minDistance + 0.01f, maxDistance));
            source.clip = MHK.Assets.GameAssets.Audio(clip);
            source.dopplerLevel = 0f;
        }

        void OnEnable()
        {
            Init();
            if (source == null) return;
            source.volume = volume;
            source.Play();
        }

        void OnDisable()
        {
            if (source != null) source.Stop();
        }

        void OnDestroy()
        {
            if (source != null) Destroy(source);
        }

        void OnValidate()
        {
            if (source != null) source.volume = volume;
        }
    }
}
