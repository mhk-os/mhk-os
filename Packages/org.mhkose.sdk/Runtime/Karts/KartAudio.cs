using System;
using System.Collections.Generic;
using MHK.Audio;
using MHK.Core;
using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Kart sounds: engine pitch from rpm, skid and surface loops, crash/scratch/landing one-shots, horn and voice lines
    /// (MHK2 Car update rules).
    /// </summary>
    [RequireComponent(typeof(Kart))]
    public class KartAudio : MonoBehaviour
    {
        [SerializeField] private AudioClip defaultEngineLoop;
        [SerializeField] private AudioClip skidLoop;
        [SerializeField] private AudioClip offroadLoop;
        [SerializeField] private AudioClip crashSound;
        [SerializeField] private AudioClip scratchSound;
        [SerializeField] private AudioClip landSound;
        [Tooltip("Engine loop volume at full throttle.")]
        [SerializeField] private float engineVolume = 0.5f;
        [Tooltip("Skid loop volume at full skid intensity.")]
        [SerializeField] private float skidVolume = 0.5f;
        [Tooltip("Surface loop volume at full speed (× the surface's own sound volume).")]
        [SerializeField] private float surfaceVolume = 1f;
        [Tooltip("Speed (original m/s) at which the surface loop reaches full volume (MHK2: 30).")]
        [SerializeField] private float surfaceFullSpeed = 30f;
        [Tooltip("Engine loop: full volume within this distance (m).")]
        [SerializeField] private float engineMinDistance = 3f;
        [Tooltip("Engine loop: silent beyond this distance (m).")]
        [SerializeField] private float engineMaxDistance = 60f;
        [Tooltip("Skid and surface loops: silent beyond this distance (m).")]
        [SerializeField] private float loopMaxDistance = 40f;
        [Tooltip("Contacts whose normal points up more than this are landings, not crashes (normal y).")]
        [SerializeField] private float landingNormalY = 0.7f;
        [Tooltip("Engine pitch peaks at the profile's change-up rpm × this.")]
        [SerializeField] private float pitchRpmHeadroom = 1.3f;
        [Tooltip("Rpm of the pitch peak for karts without a physics profile.")]
        [SerializeField] private float fallbackPitchRpm = 5000f;
        [Tooltip("Pitch added at full throttle.")]
        [SerializeField] private float throttlePitch = 0.1f;
        [Tooltip("How fast the pitch follows the rpm (1/s).")]
        [SerializeField] private float pitchFollowRate = 8f;
        [Tooltip("Share of the engine volume at zero throttle.")]
        [SerializeField] private float engineIdleVolumeShare = 0.6f;
        [Tooltip("Share of the engine volume added at full throttle.")]
        [SerializeField] private float engineThrottleVolumeShare = 0.4f;
        [Tooltip("Volume of the landing sound.")]
        [SerializeField] private float landVolume = 0.7f;
        [Tooltip("Seconds between two horn voice lines.")]
        [SerializeField] private float hornCooldownSeconds = 1.2f;
        [Tooltip("Loops stop below this intensity.")]
        [SerializeField] private float loopMinIntensity = 0.05f;

        const float LoopMinDistance = 1f;
        const float DefaultPitchLow = 0.6f, DefaultPitchHigh = 1.6f;
        const float MinFullSpeed = 0.1f;

        Kart kart;
        AudioSource engine, skid, surfaceLoop;
        float hornCooldown, collisionCooldown;
        KartWheelFx fx;

        void Awake()
        {
            kart = GetComponent<Kart>();
            fx = GetComponent<KartWheelFx>();
            engine = AudioSources.Create3D(gameObject, true, engineMinDistance, engineMaxDistance);
            skid = AudioSources.Create3D(gameObject, true, LoopMinDistance, loopMaxDistance);
            surfaceLoop = AudioSources.Create3D(gameObject, true, LoopMinDistance, loopMaxDistance);
        }

        void Start()
        {
            ResolveFallbackClips();
            var clip = kart.KartDef != null && kart.KartDef.engineLoop != null ? kart.KartDef.engineLoop : defaultEngineLoop;
            engine.clip = clip;
            if (clip != null) engine.Play();
            kart.Collided += OnCollided;
            kart.Effects.Started += OnEffectStarted;
        }

        void ResolveFallbackClips()
        {
            var packs = GameRoot.Instance.Content.Packs;
            // Sounds of the kart's own game first (a Thunder kart sounds like Thunder on any track).
            var owner = kart.KartDef != null ? kart.KartDef.OwnerPack : kart.Character != null ? kart.Character.OwnerPack : null;
            defaultEngineLoop = Fallback(defaultEngineLoop, packs, p => p.engineLoop, owner);
            skidLoop = Fallback(skidLoop, packs, p => p.skidLoop, owner);
            offroadLoop = Fallback(offroadLoop, packs, p => p.offroadLoop, owner);
            crashSound = Fallback(crashSound, packs, p => p.crashSound, owner);
            scratchSound = Fallback(scratchSound, packs, p => p.scratchSound, owner);
            landSound = Fallback(landSound, packs, p => p.landSound, owner);
        }

        static AudioClip Fallback(AudioClip own, IReadOnlyList<ContentPack> packs, Func<ContentPack, AudioClip> pick, ContentPack owner) =>
            own != null ? own : ContentPack.Find(packs, pick, owner);

        void OnDestroy()
        {
            if (kart == null) return;
            kart.Collided -= OnCollided;
            if (kart.Effects != null) kart.Effects.Started -= OnEffectStarted;
        }

        void OnEffectStarted(StatusEffectDefinition def, Kart source)
        {
            if (def.hostile) Voice(CharacterVoice.Hit);
        }

        void OnCollided(Kart k, Collision c)
        {
            if (collisionCooldown > 0f || !KartImpact.TryClassify(kart, c, out var impact)) return;
            if (impact.contact.normal.y > landingNormalY) return;
            collisionCooldown = KartImpact.CooldownSeconds;
            AudioManager.PlaySfx(impact.IsScratch && scratchSound != null ? scratchSound : crashSound, impact.contact.point);
        }

        /// <summary>Plays one of the character's voice samples at the kart (no-op when the character has none).</summary>
        public void Voice(CharacterVoice voice)
        {
            if (kart.Character == null) return;
            var clip = kart.Character.GetVoice(voice);
            if (clip != null) AudioManager.PlaySfx(clip, transform.position);
        }

        /// <summary>Plays the character's win or lose voice line.</summary>
        public void PlayWin(bool won) => Voice(won ? CharacterVoice.Win : CharacterVoice.Lose);

        void Update()
        {
            var phys = kart.Physics;
            var dt = Time.deltaTime;
            // Own levels only: the effects setting and mix gain apply through AudioListener.volume (AudioManager).
            if (engine.clip != null) UpdateEngine(phys, dt);
            UpdateGroundLoops(phys);
            collisionCooldown -= dt;

            if (phys.JustLanded) AudioManager.PlaySfx(landSound, transform.position, landVolume);

            hornCooldown -= dt;
            if (kart.CurrentInput.horn && hornCooldown <= 0f)
            {
                hornCooldown = hornCooldownSeconds;
                Voice(CharacterVoice.Horn);
            }
        }

        void UpdateEngine(KartPhysics phys, float dt)
        {
            var def = kart.KartDef;
            var low = def != null ? def.enginePitchRange.x : DefaultPitchLow;
            var high = def != null ? def.enginePitchRange.y : DefaultPitchHigh;
            var max = phys.Profile != null ? phys.Profile.changeUpRpm * pitchRpmHeadroom : fallbackPitchRpm;
            var t = Mathf.Clamp01(phys.Rpm / max);
            engine.pitch = Mathf.Lerp(engine.pitch, Mathf.Lerp(low, high, t) + phys.Throttle * throttlePitch, Smoothing.Factor(pitchFollowRate, dt));
            engine.volume = engineVolume * (engineIdleVolumeShare + engineThrottleVolumeShare * phys.Throttle);
        }

        void UpdateGroundLoops(KartPhysics phys)
        {
            // Skid loop at the skid intensity (marks rule), surface loop on the ground under the kart, louder with speed.
            var grounded = phys.IsGrounded && fx != null;
            UpdateLoop(skid, skidLoop, grounded ? fx.SkidIntensity : 0f, skidVolume);
            var surface = grounded ? fx.SoundSurface : null;
            var surfaceClip = surface == null ? null : surface.surfaceSound != null ? surface.surfaceSound : surface.offroad ? offroadLoop : null;
            var clipVolume = surface != null && surface.surfaceSound != null ? surface.surfaceSoundVolume : 1f;
            var speedLevel = Mathf.Abs(phys.ForwardSpeedOriginal) / Mathf.Max(MinFullSpeed, surfaceFullSpeed);
            UpdateLoop(surfaceLoop, surfaceClip, speedLevel, surfaceVolume * clipVolume);
        }

        void UpdateLoop(AudioSource source, AudioClip clip, float intensity, float volume)
        {
            var on = clip != null && intensity > loopMinIntensity;
            if (!on)
            {
                if (source.isPlaying) source.Stop();
                return;
            }
            if (source.clip != clip)
            {
                source.clip = clip;
                source.Play();
            }
            else if (!source.isPlaying) source.Play();
            source.volume = Mathf.Clamp01(intensity) * volume;
        }
    }
}
