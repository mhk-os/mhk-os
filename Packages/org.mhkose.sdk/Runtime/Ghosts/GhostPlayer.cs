using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using Unity.Profiling;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Replays a recorded ghost through <see cref="KartVisual"/> via <see cref="IKartView"/>: visual only, no physics,
    /// invisible to items, AI and rankings. Samples its <see cref="GhostData"/> at <see cref="RaceManager.RenderSeconds"/> every frame.
    /// </summary>
    public class GhostPlayer : MonoBehaviour, IKartView
    {
        const float HiddenOpacity = 0.01f;

        static readonly ProfilerMarker UpdateMarker = new("Ghost.Update");

        RaceManager race;
        KartVisual visual;
        GhostFrame state;
        int nextEvent;
        float baseOpacity = 1f, fade = 1f, fadeDelay;
        float topSpeed = 1f;
        readonly List<(StatusEffectDefinition def, GameObject vfx)> effects = new();

        public GhostData Data { get; private set; }
        /// <summary>Where the ghost is stored, null for a run that was never saved.</summary>
        public GhostEntry Entry { get; private set; }
        /// <summary>Watched in a replay (opaque, the camera's target), rather than raced against.</summary>
        public bool Spectated { get; private set; }
        /// <summary>Race time the ghost shows now.</summary>
        public float ShownTime { get; private set; }
        /// <summary>Past its finish line.</summary>
        public bool Finished => ShownTime >= Data.header.finishTime;

        /// <summary>Lap it is on (1..laps), from its lap times.</summary>
        public int Lap
        {
            get
            {
                var h = Data.header;
                var lap = 1;
                for (var i = 1; i < h.lapTimes.Length; i++)
                    if (ShownTime >= h.LapEndTime(i)) lap = i + 1;
                return Mathf.Clamp(lap, 1, Mathf.Max(1, h.laps));
            }
        }

        /// <summary>
        /// Creates a ghost under <paramref name="parent"/> with its character and kart from the content (falling back to
        /// the first character and its kart when they are not installed), placed at its first tick.
        /// </summary>
        public static GhostPlayer Spawn(GhostData data, GhostEntry entry, Transform parent, RaceManager race, bool spectated)
        {
            var root = GameRoot.Instance;
            var cfg = root.Config;
            var go = new GameObject($"Ghost ({data.header.playerName}, {UI.UiText.Clock(data.header.finishTime)})");
            go.transform.SetParent(parent, false);
            var ghost = go.AddComponent<GhostPlayer>();
            ghost.race = race;
            ghost.Data = data;
            ghost.Entry = entry;
            ghost.Spectated = spectated;
            ghost.baseOpacity = spectated ? 1f : cfg.ghostOpacity;
            ghost.fadeDelay = cfg.ghostFadeDelaySeconds;
            var character = ResolveCharacter(root.Content, data.header.characterId);
            var kart = root.Content.ResolveKart(data.header.kartId, character);
            ghost.topSpeed = FlatTopSpeed(cfg, kart, data.header);
            data.Sample(0f, out ghost.state);
            go.transform.SetPositionAndRotation(ghost.state.position, ghost.state.rotation);
            ghost.visual = KartVisual.BuildReplay(ghost, character, kart);
            StripPhysics(go);
            AddMinimapBlip(go, spectated, cfg);
            return ghost;
        }

        static CharacterDefinition ResolveCharacter(ContentRegistry content, string id)
        {
            var character = content.Get<CharacterDefinition>(id);
            if (character != null) return character;
            var all = content.All<CharacterDefinition>();
            return all.Count > 0 ? all[0] : null;
        }

        static float FlatTopSpeed(GameConfig cfg, KartDefinition kart, GhostHeader h)
        {
            var profile = kart != null && kart.physics != null ? kart.physics : cfg.defaultPhysics;
            var engineClass = cfg.GetDifficulty((Difficulty)h.engineClass);
            return Mathf.Max(1f, KartPhysics.FlatTopSpeed(profile, engineClass.engineClassFactor, h.speedScale, cfg.driveTorqueScale));
        }

        static void AddMinimapBlip(GameObject go, bool spectated, GameConfig cfg)
        {
            var blip = go.AddComponent<MinimapBlip>();
            blip.ring = !spectated;
            blip.color = spectated ? cfg.ghostSpectatedMinimapColor : cfg.ghostMinimapColor;
            blip.size = spectated ? 1f : cfg.ghostMinimapSize;
        }

        // Model prefabs and effect visuals may carry colliders and bodies: a ghost touches nothing.
        static void StripPhysics(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var b in go.GetComponentsInChildren<Rigidbody>(true)) Destroy(b);
        }

        void Update()
        {
            using (UpdateMarker.Auto())
            {
                var seconds = race != null ? race.RenderSeconds : 0.0;
                ShownTime = (float)seconds;
                Data.Sample(seconds, out state);
                transform.SetPositionAndRotation(state.position, state.rotation);
                if (race != null && race.ClockRunning) PlayEvents();
                if (!Spectated && ShownTime > Data.Duration + fadeDelay) FadeOut();
            }
        }

        void FadeOut()
        {
            fade = 0f;
            if (visual != null && visual.ShownOpacity <= HiddenOpacity) gameObject.SetActive(false);
        }

        void PlayEvents()
        {
            var events = Data.events;
            while (nextEvent < events.Count && events[nextEvent].time <= ShownTime) Play(events[nextEvent++]);
        }

        void Play(in GhostEvent e)
        {
            switch (e.kind)
            {
                case GhostEventKind.Action:
                    visual.PlayRecordedAction(e.role, e.hold);
                    break;
                case GhostEventKind.Release:
                    visual.ReleaseRecordedAction(e.role);
                    break;
                case GhostEventKind.Snap:
                    visual.SnapRecordedPose();
                    break;
                case GhostEventKind.EffectApplied:
                    ApplyEffect(GhostEffectLookup.Find(e.effectId));
                    break;
                case GhostEventKind.EffectEnded:
                    EndEffect(GhostEffectLookup.Find(e.effectId));
                    break;
            }
        }

        void ApplyEffect(StatusEffectDefinition def)
        {
            if (def == null) return;
            if (def.refreshOnReapply)
                foreach (var e in effects)
                    if (e.def == def) return;
            GameObject vfx = null;
            if (def.attachedVfxPrefab != null)
            {
                vfx = Instantiate(def.attachedVfxPrefab, def.attachToVisual && visual != null ? visual.Body : transform);
                StripPhysics(vfx);
                // Ghosts are silent.
                foreach (var a in vfx.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            }
            effects.Add((def, vfx));
        }

        void EndEffect(StatusEffectDefinition def)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                if (effects[i].def != def) continue;
                if (effects[i].vfx != null) Destroy(effects[i].vfx);
                effects.RemoveAt(i);
                return;
            }
        }

        public KartVisual Visual => visual;
        public bool IsHuman => true;
        /// <summary>Before GO the ghost waits on the grid like the player (the 'start' pose).</summary>
        public bool ControlsEnabled => race != null && race.ClockRunning && state.Has(GhostFrameFlags.ControlsEnabled);
        public bool RaceOver => state.Has(GhostFrameFlags.RaceOver);
        public KartInput CurrentInput => new()
        {
            steer = state.inputSteer,
            throttle = state.inputThrottle,
            brake = state.inputBrake,
            lookBack = state.Has(GhostFrameFlags.LookBack),
        };
        public float SteerAngle => state.steerAngle;
        public float SteerCommand01 => state.steerCommand;
        public float ForwardSpeed => state.forwardSpeed;
        public float ForwardSpeedOriginal => state.forwardSpeed / Mathf.Max(KartPhysics.MinSpeedScale, Data.header.speedScale);
        public float SpeedFraction => state.forwardSpeed / topSpeed;
        /// <summary>Landings are replayed as the recorded action.</summary>
        public bool JustLanded => false;
        public int GroundContacts => state.contacts;
        public float SpinAngle => state.spinAngle;
        public float ScaleMultiplier => state.scale;
        public float VerticalScale => state.verticalScale;
        public float Opacity => Mathf.Min(state.opacity, baseOpacity) * fade;
        public bool Burned => state.Has(GhostFrameFlags.Burned);
        public bool Hidden => false;
        /// <summary>A replayed effect hides the wheels (<see cref="StatusEffectDefinition.hideWheels"/>).</summary>
        public bool WheelsHidden
        {
            get
            {
                foreach (var e in effects)
                    if (e.def.hideWheels) return true;
                return false;
            }
        }
        /// <summary>Strongest body bob of the replayed effects (<see cref="StatusEffectDefinition.bodyBob"/>).</summary>
        public BodyBob BodyBob
        {
            get
            {
                BodyBob strongest = null;
                foreach (var e in effects) strongest = BodyBob.Stronger(strongest, e.def.bodyBob);
                return strongest;
            }
        }
        public float CameraFovBoost => state.fovBoost;
        public Vector3 QuakeOffset => Vector3.zero;

        public bool IsEffectAttachment(Transform t)
        {
            foreach (var e in effects)
                if (e.vfx != null && t.IsChildOf(e.vfx.transform)) return true;
            return false;
        }
    }
}
