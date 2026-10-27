using MHK.Assets;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Audio;

namespace MHK.Audio
{
    /// <summary>
    /// Plays pooled 3D one-shots (at a place, or following a transform such as a kart) and 2D UI sounds and applies the
    /// volume settings to the audio mix (music: <see cref="MusicPlayer"/>). Lives on the GameRoot object.
    /// </summary>
    public partial class AudioManager : MonoBehaviour
    {
        const int PoolSize = 24;
        const float SfxMinDistance = 4f;
        const float SfxMaxDistance = 120f;

        [AutoStaticsCleanup] static AudioManager instance;
        [AutoStaticsCleanup] static bool worldMuted;
        AudioSource ui;
        AudioSource[] pool;
        // Transform each pooled one-shot follows while it plays, null for one placed in the world.
        Transform[] follow;
        int next;

        static AudioManager Instance
        {
            get
            {
                if (instance == null)
                {
                    var root = GameRoot.Instance;
                    instance = root.GetComponent<AudioManager>();
                    if (instance == null) instance = root.gameObject.AddComponent<AudioManager>();
                }
                return instance;
            }
        }

        void Awake()
        {
            instance = this;
            ui = AudioSources.Create2D(gameObject, false);
            ui.ignoreListenerVolume = true;
            CreateSfxPool();
        }

        void CreateSfxPool()
        {
            pool = new AudioSource[PoolSize];
            follow = new Transform[PoolSize];
            for (var i = 0; i < PoolSize; i++)
            {
                var go = new GameObject("Sfx" + i);
                go.transform.SetParent(transform);
                pool[i] = AudioSources.Create3D(go, false, SfxMinDistance, SfxMaxDistance);
            }
        }

        /// <summary>
        /// One-shot 3D sound effect at <paramref name="position"/>. <paramref name="sound"/> is a clip or any other
        /// AudioResource (e.g. an Audio Random Container), <paramref name="volume"/> is the sound's own level.
        /// <paramref name="range"/> sets how far it carries (null = full within 4 m, silent at 120 m).
        /// </summary>
        public static void PlaySfx(AudioResource sound, Vector3 position, float volume = 1f, float pitch = 1f, SoundRange range = null)
        {
            if (sound == null) return;
            Instance.Play(sound, position, null, volume, pitch, range);
        }

        /// <summary>
        /// <see cref="PlaySfx(AudioResource, Vector3, float, float, SoundRange)"/> that moves with <paramref name="target"/>
        /// (a kart's own sounds: voices, effect sounds) and stops when it is destroyed.
        /// </summary>
        public static void PlaySfx(AudioResource sound, Transform target, float volume = 1f, float pitch = 1f, SoundRange range = null)
        {
            if (sound == null || target == null) return;
            Instance.Play(sound, target.position, target, volume, pitch, range);
        }

        void Play(AudioResource sound, Vector3 position, Transform target, float volume, float pitch, SoundRange range)
        {
            var s = pool[next];
            follow[next] = target;
            next = (next + 1) % PoolSize;
            s.transform.position = position;
            s.pitch = pitch;
            s.volume = volume;
            if (range != null) range.ApplyTo(s);
            else
            {
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = SfxMinDistance;
                s.maxDistance = SfxMaxDistance;
            }
            s.resource = sound is AudioClip clip ? GameAssets.Audio(clip) : sound;
            s.Play();
        }

        // After the karts moved (interpolated) this frame. A paused game (AudioListener.pause) keeps its followers.
        void LateUpdate()
        {
            for (var i = 0; i < PoolSize; i++)
            {
                var target = follow[i];
                if (ReferenceEquals(target, null)) continue;
                var s = pool[i];
                if (target == null)
                {
                    s.Stop();
                    follow[i] = null;
                }
                else if (!s.isPlaying && !AudioListener.pause) follow[i] = null;
                else s.transform.position = target.position;
            }
        }

        /// <summary>2D UI sound (button clicks). <paramref name="volume"/> is the sound's own level.</summary>
        public static void PlayUi(AudioClip clip, float volume = 1f)
        {
            if (clip == null) return;
            Instance.ui.PlayOneShot(GameAssets.Audio(clip), volume);
        }

        /// <summary>
        /// Silences every sound but the music and the UI (the main menu's background race). Unmuting stops the pooled
        /// one-shots, so nothing of the silenced race plays on.
        /// </summary>
        public static bool WorldMuted
        {
            get => worldMuted;
            set
            {
                if (worldMuted == value) return;
                worldMuted = value;
                if (!value) Instance.StopPool();
                RefreshVolumes();
            }
        }

        /// <summary>
        /// Applies the volume settings and <see cref="GameConfig"/> mix gains live. Every sound effect plays through
        /// AudioListener.volume (master × sfx gain × effects setting, 0 while <see cref="WorldMuted"/>), so any
        /// AudioSource follows the effects slider without code. UI sounds get the same level on their own source. The
        /// <see cref="MusicPlayer"/> ignores the listener volume and applies master × music gain × music setting itself.
        /// Called by <see cref="GameRoot.ApplySettings"/>.
        /// </summary>
        public static void RefreshVolumes()
        {
            var root = GameRoot.Instance;
            var sfx = Mathf.Clamp01(root.Settings.masterVolume * root.Config.sfxMixGain * root.Settings.sfxVolume);
            AudioListener.volume = worldMuted ? 0f : sfx;
            var inst = Instance;
            inst.ui.volume = sfx;
            MusicPlayer.RefreshVolume();
        }

        void StopPool()
        {
            foreach (var s in pool) s.Stop();
            System.Array.Clear(follow, 0, PoolSize);
        }
    }
}
