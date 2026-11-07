using System.Collections;
using MHK.Assets;
using MHK.Audio;
using MHK.Core;
using MHK.Race;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Effects
{
    /// <summary>
    /// Sound of the weather around the race: a looping 2D ambience (rain, wind) that fades in when enabled and out when
    /// disabled, and optionally a thunderstorm. Each lightning strike flashes the scene (one or two quick flickers through
    /// the global <see cref="MHKLitIds.Flash"/> that MHK/Lit surfaces, sky domes and weather particles add to their light),
    /// then thunder follows after a delay that grows with the strike's distance, quieter and weaker the further it is. Runs
    /// on scaled time, so it holds with the race pause (the sources pause with AudioListener.pause), and never strikes in
    /// the menu's background race or while the race loads. Plays through the listener volume like every sound effect.
    /// </summary>
    [DisallowMultipleComponent]
    public class WeatherAmbience : MonoBehaviour
    {
        [Header("Ambience")]
        [Tooltip("Looping ambience (rain, wind), played in 2D while enabled. Empty = none.")]
        [SerializeField] AudioClip loop;
        [Tooltip("Ambience volume (the sound's own level, the effects setting and mix gain apply on top).")]
        [Range(0f, 1f)] [SerializeField] float loopVolume = 0.5f;
        [Tooltip("Seconds the ambience takes to fade in when enabled (race start).")]
        [Min(0f)] [SerializeField] float fadeIn = 2f;
        [Tooltip("Seconds the ambience takes to fade out when disabled.")]
        [Min(0f)] [SerializeField] float fadeOut = 1f;

        [Header("Thunder (no clips = no thunderstorm)")]
        [Tooltip("Thunder clips, one picked at random per strike (never the same twice in a row).")]
        [SerializeField] AudioClip[] thunderClips;
        [Tooltip("Seconds from enabling to the first strike (random between x and y).")]
        [SerializeField] Vector2 firstStrike = new(5f, 15f);
        [Tooltip("Seconds between strikes (random between x and y).")]
        [SerializeField] Vector2 strikeInterval = new(10f, 35f);
        [Tooltip("Seconds from the flash to the thunder (x = a close strike, y = a distant one).")]
        [SerializeField] Vector2 thunderDelay = new(0.3f, 3f);
        [Tooltip("Thunder volume (x = a distant strike, y = a close one).")]
        [SerializeField] Vector2 thunderVolume = new(0.45f, 1f);
        [Tooltip("Thunder pitch (random between x and y; distant strikes sound a little lower).")]
        [SerializeField] Vector2 thunderPitch = new(0.85f, 1.1f);
        [Tooltip("Colour of the lightning flash.")]
        [SerializeField] Color flashColor = new(0.82f, 0.87f, 1f);
        [Tooltip("Light the flash adds at its peak (x = a distant strike, y = a close one; 1 = a fully lit surface).")]
        [SerializeField] Vector2 flashIntensity = new(0.35f, 1.2f);

        // A flash: a first flicker and maybe a second one, all within MaxFlashSeconds.
        const float MaxFlashSeconds = 0.25f;
        const float SecondFlickerChance = 0.6f;
        // Share of a flicker's length spent brightening; the rest fades out.
        const float FlickerAttack = 0.15f;
        const float FarPitch = 0.92f;
        const int ThunderVoices = 2;

        readonly System.Random random = new();
        GameObject loopObject;
        AudioSource loopSource;
        AudioSource[] thunderSources;
        int fadeToken;
        int nextVoice;
        int lastClip = -1;
        float untilStrike;
        // The current flash (time since it began, < 0 = none) and its flickers: start, length, peak.
        float flashTime = -1f;
        Vector3 flicker1, flicker2;
        Color flashPeak;
        bool flashSet;
        // The thunder waiting for its delay (< 0 = none).
        float untilThunder = -1f;
        AudioClip pendingClip;
        float pendingVolume, pendingPitch;

        bool HasThunder => thunderClips != null && thunderClips.Length > 0;

        void Awake()
        {
            if (loop != null)
            {
                // Its own object in this scene, so the loop can fade out after this one is deactivated.
                loopObject = new GameObject(name + " ambience");
                SceneManager.MoveGameObjectToScene(loopObject, gameObject.scene);
                loopSource = AudioSources.Create2D(loopObject, true);
                loopSource.clip = GameAssets.Audio(loop);
                loopSource.dopplerLevel = 0f;
            }
            if (!HasThunder) return;
            thunderSources = new AudioSource[ThunderVoices];
            for (var i = 0; i < ThunderVoices; i++)
            {
                thunderSources[i] = AudioSources.Create2D(gameObject, false);
                thunderSources[i].dopplerLevel = 0f;
            }
        }

        void OnEnable()
        {
            fadeToken++;
            if (loopSource != null)
            {
                // Still fading out (enabled again right away): it fades back in from where it is.
                if (!loopSource.isPlaying)
                {
                    loopSource.volume = fadeIn > 0f ? 0f : loopVolume;
                    loopSource.Play();
                }
            }
            untilStrike = Pick(firstStrike);
            untilThunder = -1f;
            flashTime = -1f;
        }

        void OnDisable()
        {
            ClearFlash();
            untilThunder = -1f;
            if (loopSource == null) return;
            var root = fadeOut > 0f && loopSource.isPlaying ? GameRoot.Instance : null;
            if (root != null && root.isActiveAndEnabled) root.StartCoroutine(FadeOut(fadeToken));
            else loopSource.Stop();
        }

        void OnDestroy()
        {
            ClearFlash();
            if (loopObject != null) Destroy(loopObject);
        }

        // Runs on the GameRoot: this component is disabled. Ends early when it is enabled again or destroyed.
        IEnumerator FadeOut(int token)
        {
            var from = loopSource.volume;
            for (var t = 0f; t < fadeOut; t += Time.unscaledDeltaTime)
            {
                if (this == null || loopSource == null || token != fadeToken) yield break;
                loopSource.volume = from * (1f - t / fadeOut);
                yield return null;
            }
            if (this != null && loopSource != null && token == fadeToken) loopSource.Stop();
        }

        void Update()
        {
            var dt = Time.deltaTime;
            if (loopSource != null && loopSource.volume < loopVolume)
                loopSource.volume = fadeIn > 0f ? Mathf.Min(loopVolume, loopSource.volume + loopVolume * dt / fadeIn) : loopVolume;
            if (!HasThunder) return;
            if (!StormAllowed())
            {
                ClearFlash();
                return;
            }
            untilStrike -= dt;
            if (untilStrike <= 0f) Strike();
            UpdateFlash(dt);
            UpdateThunder(dt);
        }

        // Not in the menu's background race and not behind the loading screen; outside a race (a scene played on its
        // own) always.
        static bool StormAllowed()
        {
            var race = RaceManager.Current;
            return race == null || (!race.IsAttract && race.Phase != RacePhase.Loading);
        }

        void Strike()
        {
            untilStrike = Pick(strikeInterval);
            var distance = (float)random.NextDouble();
            var intensity = Mathf.Lerp(flashIntensity.y, flashIntensity.x, distance) * Mathf.Lerp(0.85f, 1.15f, (float)random.NextDouble());
            flashPeak = flashColor * intensity;
            var first = Mathf.Lerp(0.07f, 0.11f, (float)random.NextDouble());
            flicker1 = new Vector3(0f, first, 1f);
            if (random.NextDouble() < SecondFlickerChance)
            {
                var start = first + Mathf.Lerp(0.02f, 0.05f, (float)random.NextDouble());
                var length = Mathf.Min(Mathf.Lerp(0.06f, 0.1f, (float)random.NextDouble()), MaxFlashSeconds - start);
                flicker2 = new Vector3(start, length, Mathf.Lerp(0.5f, 1f, (float)random.NextDouble()));
            }
            else flicker2 = Vector3.zero;
            flashTime = 0f;

            var clip = random.Next(thunderClips.Length);
            if (clip == lastClip && thunderClips.Length > 1) clip = (clip + 1 + random.Next(thunderClips.Length - 1)) % thunderClips.Length;
            lastClip = clip;
            pendingClip = thunderClips[clip];
            pendingVolume = Mathf.Lerp(thunderVolume.y, thunderVolume.x, distance);
            pendingPitch = Pick(thunderPitch) * Mathf.Lerp(1f, FarPitch, distance);
            untilThunder = Mathf.Lerp(thunderDelay.x, thunderDelay.y, distance);
        }

        void UpdateFlash(float dt)
        {
            if (flashTime < 0f) return;
            flashTime += dt;
            if (flashTime >= MaxFlashSeconds)
            {
                ClearFlash();
                return;
            }
            var level = Mathf.Max(Flicker(flicker1, flashTime), Flicker(flicker2, flashTime));
            // Paused (time stands still): no frozen flash over the pause menu.
            if (Time.timeScale <= 0f) level = 0f;
            Shader.SetGlobalVector(MHKLitIds.Flash, flashPeak * level);
            flashSet = true;
        }

        // Brightness of a flicker (start, length, peak) at time t: a quick rise, then a fade.
        static float Flicker(Vector3 f, float t)
        {
            if (f.y <= 0f || t < f.x || t >= f.x + f.y) return 0f;
            var x = (t - f.x) / f.y;
            if (x < FlickerAttack) return f.z * x / FlickerAttack;
            var fade = 1f - (x - FlickerAttack) / (1f - FlickerAttack);
            return f.z * fade * fade;
        }

        void UpdateThunder(float dt)
        {
            if (untilThunder < 0f) return;
            untilThunder -= dt;
            if (untilThunder > 0f) return;
            untilThunder = -1f;
            if (pendingClip == null) return;
            var source = thunderSources[nextVoice];
            nextVoice = (nextVoice + 1) % thunderSources.Length;
            source.clip = GameAssets.Audio(pendingClip);
            source.volume = pendingVolume;
            source.pitch = pendingPitch;
            source.Play();
        }

        void ClearFlash()
        {
            flashTime = -1f;
            if (!flashSet) return;
            Shader.SetGlobalVector(MHKLitIds.Flash, Vector4.zero);
            flashSet = false;
        }

        float Pick(Vector2 r) => Mathf.Lerp(r.x, r.y, (float)random.NextDouble());

        void OnValidate()
        {
            firstStrike = Vector2.Max(firstStrike, Vector2.zero);
            strikeInterval = Vector2.Max(strikeInterval, Vector2.one);
            thunderDelay = Vector2.Max(thunderDelay, Vector2.zero);
            thunderPitch = Vector2.Max(thunderPitch, new Vector2(0.1f, 0.1f));
            if (loopSource != null && loopSource.volume > loopVolume) loopSource.volume = loopVolume;
        }
    }
}
