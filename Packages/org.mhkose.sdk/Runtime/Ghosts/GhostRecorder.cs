using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Records a kart's ghost: one <see cref="GhostFrame"/> per physics tick from GO plus visual events. Runs after kart,
    /// effects and progress each step (execution order 1000), and keeps recording <see cref="GameConfig.ghostTailSeconds"/> past the finish.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class GhostRecorder : MonoBehaviour
    {
        // A jump larger than this (m) beyond the velocity's reach is a teleport even without a respawn event.
        const float TeleportSlack = 2f;
        const float TeleportReachTicks = 2f;

        Kart kart;
        RaceManager race;
        readonly List<GhostFrame> frames = new();
        readonly List<GhostEvent> events = new();
        bool recording, cutPending;
        float stopAt = -1f;

        /// <summary>Recorded ticks.</summary>
        public int FrameCount => frames.Count;

        /// <summary>Adds a recorder to <paramref name="target"/> and listens to its events (events before GO count as at GO).</summary>
        public static GhostRecorder Attach(Kart target, RaceManager raceManager)
        {
            var r = target.gameObject.AddComponent<GhostRecorder>();
            r.kart = target;
            r.race = raceManager;
            target.Respawned += r.OnRespawned;
            target.Effects.Applied += r.OnEffectApplied;
            target.Effects.Ended += r.OnEffectEnded;
            if (target.Visual != null)
            {
                target.Visual.ActionPlayed += r.OnActionPlayed;
                target.Visual.ActionReleased += r.OnActionReleased;
                target.Visual.PoseSnapped += r.OnPoseSnapped;
            }
            return r;
        }

        void OnDestroy()
        {
            if (kart == null) return;
            kart.Respawned -= OnRespawned;
            kart.Effects.Applied -= OnEffectApplied;
            kart.Effects.Ended -= OnEffectEnded;
            if (kart.Visual == null) return;
            kart.Visual.ActionPlayed -= OnActionPlayed;
            kart.Visual.ActionReleased -= OnActionReleased;
            kart.Visual.PoseSnapped -= OnPoseSnapped;
        }

        /// <summary>At GO (after the start signal's power start or stall was applied): records tick 0 and starts.</summary>
        public void Begin()
        {
            frames.Clear();
            recording = true;
            stopAt = -1f;
            cutPending = false;
            frames.Add(Capture());
        }

        /// <summary>Stops recording (the tail ran out, or the race ended).</summary>
        public void Stop() => recording = false;

        void FixedUpdate()
        {
            if (!recording) return;
            frames.Add(Capture());
            if (stopAt < 0f && kart.Progress.Finished) stopAt = race.RaceTime + GameRoot.Instance.Config.ghostTailSeconds;
            if (stopAt >= 0f && race.RaceTime >= stopAt - 0.5f * Time.fixedDeltaTime) Stop();
        }

        GhostFrame Capture()
        {
            var phys = kart.Physics;
            var fx = kart.Effects;
            var body = phys.Body;
            var input = kart.CurrentInput;
            var f = new GhostFrame
            {
                position = body.position,
                rotation = body.rotation,
                velocity = body.linearVelocity,
                forwardSpeed = phys.ForwardSpeed,
                steerAngle = phys.SteerAngle,
                steerCommand = phys.SteerCommand01,
                inputSteer = input.steer,
                inputThrottle = input.throttle,
                inputBrake = input.brake,
                keys = Keys(kart.EffectiveInput),
                contacts = phys.ContactCount,
                spinAngle = fx.SpinAngle,
                scale = fx.ScaleMultiplier,
                verticalScale = fx.VerticalScale,
                opacity = fx.Opacity,
                fovBoost = kart.CameraFovBoost,
            };
            f.flags = Flags(input, fx);
            if (cutPending || IsTeleport(f)) f.flags |= GhostFrameFlags.Cut;
            cutPending = false;
            return f;
        }

        GhostFrameFlags Flags(in KartInput input, KartEffects fx)
        {
            var flags = GhostFrameFlags.None;
            if (kart.ControlsEnabled) flags |= GhostFrameFlags.ControlsEnabled;
            if (kart.Progress.Finished || kart.Eliminated) flags |= GhostFrameFlags.RaceOver;
            if (input.lookBack) flags |= GhostFrameFlags.LookBack;
            if (fx.Burned) flags |= GhostFrameFlags.Burned;
            return flags;
        }

        bool IsTeleport(in GhostFrame f)
        {
            if (frames.Count == 0) return false;
            var last = frames[frames.Count - 1];
            var reach = last.velocity.magnitude * Time.fixedDeltaTime * TeleportReachTicks + TeleportSlack;
            return (f.position - last.position).sqrMagnitude > reach * reach;
        }

        static GhostInputKeys Keys(in KartInput i)
        {
            var k = GhostInputKeys.None;
            if (i.steer < -KartPhysics.SteerDeadzone) k |= GhostInputKeys.SteerLeft;
            else if (i.steer > KartPhysics.SteerDeadzone) k |= GhostInputKeys.SteerRight;
            if (i.throttle > 0f) k |= GhostInputKeys.Throttle;
            if (i.brake > 0f) k |= GhostInputKeys.Brake;
            if (i.parked) k |= GhostInputKeys.Parked;
            if (i.fire) k |= GhostInputKeys.Fire;
            if (i.lookBack) k |= GhostInputKeys.LookBack;
            if (i.respawn) k |= GhostInputKeys.Respawn;
            return k;
        }

        // Stamped with the race time they show at (physics step: the tick, frame: between ticks).
        void AddEvent(GhostEvent e)
        {
            if (frames.Count > 0 && !recording) return;
            e.time = race.RenderTime;
            events.Add(e);
        }

        void OnRespawned(Kart k) => cutPending = true;
        void OnActionPlayed(CharacterAnimRole role, bool hold) => AddEvent(new GhostEvent { kind = GhostEventKind.Action, role = role, hold = hold });
        void OnActionReleased(CharacterAnimRole role) => AddEvent(new GhostEvent { kind = GhostEventKind.Release, role = role });
        void OnPoseSnapped() => AddEvent(new GhostEvent { kind = GhostEventKind.Snap });
        void OnEffectApplied(StatusEffectDefinition def, Kart source) => AddEvent(new GhostEvent { kind = GhostEventKind.EffectApplied, effectId = def.Id });
        void OnEffectEnded(StatusEffectDefinition def) => AddEvent(new GhostEvent { kind = GhostEventKind.EffectEnded, effectId = def.Id });

        /// <summary>The recording with <paramref name="header"/>: frames copied, events in time order (stable, so same-instant events keep their order).</summary>
        public GhostData Build(GhostHeader header)
        {
            var data = new GhostData { header = header, frames = frames.ToArray(), events = events.OrderBy(e => e.time).ToList() };
            header.frameCount = data.frames.Length;
            return data;
        }
    }
}
