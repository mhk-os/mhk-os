using UnityEngine;

namespace MHK.Audio
{
    /// <summary>Factory for the code-created AudioSources, the one place that sets up how a sound is heard.</summary>
    public static class AudioSources
    {
        /// <summary>
        /// Adds a 3D source to <paramref name="host"/>: full volume within <paramref name="minDistance"/>, linear fade to
        /// silence at <paramref name="maxDistance"/>, not playing on awake.
        /// </summary>
        public static AudioSource Create3D(GameObject host, bool loop, float minDistance, float maxDistance)
        {
            var s = host.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            return s;
        }

        /// <summary>Adds a 2D source (music, UI) to <paramref name="host"/>, not playing on awake.</summary>
        public static AudioSource Create2D(GameObject host, bool loop)
        {
            var s = host.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = 0f;
            return s;
        }
    }
}
