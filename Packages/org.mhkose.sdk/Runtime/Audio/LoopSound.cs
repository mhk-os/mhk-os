using System;
using UnityEngine;

namespace MHK.Audio
{
    /// <summary>
    /// Settings of a looping 3D sound a powerup plays during one phase, started with
    /// <see cref="Effects.EffectLoopSound.Play"/>. Empty <see cref="clip"/> = no loop.
    /// </summary>
    [Serializable]
    public class LoopSound
    {
        [Tooltip("Looping clip (mono for 3D). Empty = no loop.")]
        public AudioClip clip;
        [Tooltip("Loop volume (multiplied by the sound-effects volume setting).")]
        [Range(0f, 1f)] public float volume = 1f;
        [Tooltip("Full volume within this distance (m).")]
        [Min(0f)] public float minDistance = 1f;
        [Tooltip("Silent beyond this distance (m), linear fade in between.")]
        [Min(0f)] public float maxDistance = 30f;
    }
}
