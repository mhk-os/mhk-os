using System.Collections.Generic;
using MHK.Audio;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Hank's molehill: the kart digs in, tunnels a stretch under the track steered by its driver, and pops up again.
    /// A <see cref="MolehillMound"/> ploughs above it and launches karts it passes under.
    /// </summary>
    [DefaultExecutionOrder(100)] // after Kart / KartEffects: the last word on the body's velocity before the physics step
    public class MolehillPowerup : PowerupBehaviour
    {
        enum Phase { Waiting, Digging, Surfacing, Done }

        const float MaxRideHeight = 1.5f;
        const float RotationSharpness = 25f;
        const float LaunchGrace = 0.1f;
        const float SurfaceLift = 0.05f;
        const float FinishedLinger = 1.5f;
        const float SpringBack = 1.6f;
        const float MinTime = 0.01f;
        const float LaunchBelow = 0.5f;
        const float CentreDistance = 0.05f;
        const float MinSideDistance = 0.3f;
        const float UpLossBeside = 0.45f;
        const float TumbleRoll = 3f;
        const float SideBurstShare = 0.5f;

        [Header("Dig")]
        [Tooltip("Distance travelled underground (m).")]
        public float digDistance = 56f;
        [Tooltip("Underground speed in original m/s (× the kart's speed scale).")]
        [SerializeField] float digSpeed = 30f;
        [Tooltip("...but at least this × the kart's speed when it started digging.")]
        [SerializeField] float entrySpeedFactor = 1.2f;
        [Tooltip("Seconds to reach the underground speed.")]
        [SerializeField] float accelerationTime = 0.3f;
        [Tooltip("Turn rate underground at full steering (degrees per second).")]
        [SerializeField] float turnRate = 80f;
        [Tooltip("Seconds for the turn rate to follow the steering input from straight to full.")]
        [SerializeField] float turnResponse = 0.15f;
        [Tooltip("Used in the air: digging starts once the kart is on the ground, or the item fizzles after this many seconds.")]
        [SerializeField] float waitForGround = 1f;
        [Tooltip("Effect on the digger while underground (ghost: untouchable, no item use), removed when it surfaces. The rumble is tunnelLoop.")]
        [SerializeField] StatusEffectDefinition tunnelEffect;

        [Header("Ground checks")]
        [SerializeField] LayerMask groundMask = ~0;
        [Tooltip("Probe step along the heading (m).")]
        [SerializeField] float probeStep = 0.5f;
        [Tooltip("Allowed height change per metre travelled (slopes).")]
        [SerializeField] float maxClimb = 0.9f;
        [Tooltip("Allowed height change per step on top of the slope (bumps, small steps) (m).")]
        [SerializeField] float stepTolerance = 0.3f;
        [Tooltip("Steeper ground ends the dig (degrees).")]
        [SerializeField] float maxSlope = 40f;
        [Tooltip("A wall at this height above the ground between two steps ends the dig (m).")]
        [SerializeField] float wallCheckHeight = 0.6f;
        [Tooltip("The checks run this far ahead of the kart along its heading: where the dig cannot go on, it surfaces this far before the spot (m).")]
        [SerializeField] float surfaceClearance = 2f;

        [Header("Sinking and surfacing")]
        [Tooltip("How deep the kart's visual sinks into the ground (m).")]
        [SerializeField] float sinkDepth = 1.8f;
        [Tooltip("Seconds to sink in.")]
        [SerializeField] float sinkTime = 0.35f;
        [Tooltip("Seconds to rise out of the ground.")]
        [SerializeField] float popTime = 0.3f;
        [Tooltip("Upward speed when popping out (m/s).")]
        [SerializeField] float popHop = 4f;
        [Tooltip("Forward speed when popping out, as a share of the kart's normal top speed (full throttle on flat road, no effects, current engine class). 1 = exactly that top speed, never a boost.")]
        [SerializeField, Min(0f)] float exitSpeedShare = 1f;

        [Header("Karts it passes under")]
        [SerializeField] StatusEffectDefinition hitEffect;
        [Tooltip("Karts whose centre comes this close to the mound's head are launched (m, horizontal).")]
        [SerializeField] float hitRadius = 1.9f;
        [Tooltip("...within this height of it (m).")]
        [SerializeField] float hitHeight = 2f;
        [Tooltip("Upward launch speed for a kart right above the head (m/s).")]
        [SerializeField] float launchUp = 7.5f;
        [Tooltip("Sideways launch speed for a kart beside the head (m/s). Karts in between get a mix.")]
        [SerializeField] float launchSide = 7f;
        [Tooltip("Extra push in the dig direction (m/s).")]
        [SerializeField] float launchForward = 2f;
        [Tooltip("One-shot at each kart launched by the mound.")]
        [SerializeField] AudioClip hitSound;

        [Header("Presentation")]
        [SerializeField] MolehillMound mound;
        [Tooltip("Paths (from full left to full right steering) sampled to find the ground the mound may need next. Their area is captured as its texture.")]
        [SerializeField, Min(1)] int captureRays = 7;
        [Tooltip("Dust thrown up continuously at the head while digging (world-space particles, tinted with the surface's dust colour).")]
        [SerializeField] ParticleSystem dirtSpray;
        [Tooltip("Dust burst when digging in and popping out.")]
        [SerializeField] ParticleSystem dirtBurst;
        [Tooltip("Clods of earth (mesh particles), drawn with the mound's ground material.")]
        [SerializeField] ParticleSystem clods;
        [SerializeField] int burstCount = 45;
        [SerializeField] int clodBurstCount = 16;
        [Tooltip("Clods thrown per metre dug.")]
        [SerializeField] float clodsPerMetre = 0.8f;
        [Tooltip("Dust colour where the ground has no surface settings.")]
        [SerializeField] Color defaultDust = new(0.55f, 0.42f, 0.28f, 0.85f);

        [Header("Sounds")]
        [Tooltip("One-shot where the kart digs into the ground.")]
        [SerializeField] AudioClip digSound;
        [Tooltip("Loop that follows the digging kart while it tunnels (stops when it surfaces).")]
        [SerializeField] LoopSound tunnelLoop = new();
        [Tooltip("One-shot where the kart pops out of the ground.")]
        [SerializeField] AudioClip popSound;

        Phase phase = Phase.Waiting;
        float phaseTime;
        float travelled, speed, entrySpeed, rideHeight, yawRate;
        bool hidden;
        float visualOffset, appliedOffset;
        Transform visualRoot;
        MoleTunnel tunnel;
        MolehillDirtFx dirt;
        readonly List<Renderer> hiddenRenderers = new();
        readonly HashSet<Kart> launched = new();
        Effects.EffectLoopSound loop;

        bool Underground => phase == Phase.Digging || phase == Phase.Surfacing;

        protected override void OnLaunch()
        {
            if (Owner == null) { Destroy(gameObject); return; }
            tunnel = new MoleTunnel(new MoleTunnel.Settings
            {
                groundMask = groundMask, probeStep = probeStep, maxClimb = maxClimb, stepTolerance = stepTolerance,
                maxSlope = maxSlope, wallCheckHeight = wallCheckHeight,
            });
            dirt = new MolehillDirtFx(dirtSpray, dirtBurst, clods, mound, burstCount, clodBurstCount, clodsPerMetre, defaultDust, groundMask);
            Owner.Respawned += OnOwnerRespawned;
            dirt.Spray(false);
        }

        void OnDestroy()
        {
            if (Owner != null) Owner.Respawned -= OnOwnerRespawned;
            if (Underground) RestoreOwner();
        }

        // Respawned while underground (out of the world, manual respawn): drop out of the dig on the spot.
        void OnOwnerRespawned(Kart k)
        {
            if (!Underground) return;
            RestoreOwner();
            EndDig();
        }

        void FixedUpdate()
        {
            if (Owner == null) { if (phase != Phase.Done) EndDig(); return; }
            var dt = Time.fixedDeltaTime;
            phaseTime += dt;
            switch (phase)
            {
                case Phase.Waiting:
                    if (Owner.Physics.IsGrounded && TryBeginDig()) break;
                    if (phaseTime > waitForGround) Destroy(gameObject);
                    break;
                case Phase.Digging:
                    Dig(dt);
                    break;
                case Phase.Surfacing:
                    LaunchKartsAtHead();
                    if (phaseTime >= popTime) EndDig();
                    break;
                case Phase.Done:
                    if (mound == null || mound.Finished) Destroy(gameObject, FinishedLinger);
                    break;
            }
        }

        // Visual sink as a delta on the visual's local position (composes with KartQuake and others).
        void LateUpdate()
        {
            if (visualRoot == null) return;
            visualOffset = phase switch
            {
                Phase.Digging => SinkOffset(),
                Phase.Surfacing => -sinkDepth * (1f - EaseOutBack(Mathf.Clamp01(phaseTime / Mathf.Max(MinTime, popTime)))),
                _ => 0f,
            };
            ApplyVisualOffset();
        }

        float SinkOffset()
        {
            var t = Mathf.Clamp01(phaseTime / Mathf.Max(MinTime, sinkTime));
            if (t >= 1f && !hidden) Hide(true);
            return -sinkDepth * t * t;
        }

        static float EaseOutBack(float t)
        {
            var u = t - 1f;
            return 1f + (SpringBack + 1f) * u * u * u + SpringBack * u * u;
        }

        void ApplyVisualOffset()
        {
            if (visualRoot == null) return;
            visualRoot.localPosition += Vector3.up * (visualOffset - appliedOffset);
            appliedOffset = visualOffset;
        }

        bool TryBeginDig()
        {
            var kt = Owner.transform;
            if (!tunnel.TryBegin(kt, out var hit)) return false;
            yawRate = 0f;
            rideHeight = Mathf.Clamp(Vector3.Dot(kt.position - hit.point, hit.normal), 0f, MaxRideHeight);
            entrySpeed = Mathf.Max(0f, Owner.Physics.ForwardSpeed);
            speed = entrySpeed;
            travelled = 0f;
            Suspend();
            if (mound != null) mound.Begin(tunnel.Ground, tunnel.Normal, tunnel.Forward, CaptureArea(), tunnel.Heading);
            dirt.Burst(tunnel.Ground, tunnel.Normal, tunnel.Forward);
            PlaySfx(digSound, tunnel.Ground);
            loop = Effects.EffectLoopSound.Play(Owner.gameObject, tunnelLoop);
            dirt.Spray(true);
            phase = Phase.Digging;
            phaseTime = 0f;
            return true;
        }

        void Suspend()
        {
            Owner.Physics.Suspended = true;
            Owner.Physics.Body.detectCollisions = false;
            if (tunnelEffect != null) Owner.Effects.Apply(tunnelEffect, Owner);
            visualRoot = Owner.Visual != null ? Owner.Visual.transform : null;
        }

        float TargetSpeed() => Mathf.Max(entrySpeed * entrySpeedFactor, digSpeed * SpeedScale);

        float SteerInput()
        {
            if (!Owner.ControlsEnabled || Owner.Effects.ControlsLocked) return 0f;
            var s = Mathf.Clamp(Owner.CurrentInput.steer, -1f, 1f);
            return Owner.Effects.SteeringInverted ? -s : s;
        }

        List<Vector3> CaptureArea() =>
            tunnel.CaptureArea(digDistance - travelled + surfaceClearance, mound != null ? mound.CaptureReach : digDistance,
                turnRate, TargetSpeed(), Mathf.Max(1, captureRays));

        void Dig(float dt)
        {
            var target = TargetSpeed();
            var accel = Mathf.Max(MinTime, target - entrySpeed) / Mathf.Max(MinTime, accelerationTime);
            speed = Mathf.MoveTowards(speed, target, accel * dt);
            yawRate = Mathf.MoveTowards(yawRate, SteerInput() * turnRate, turnRate / Mathf.Max(MinTime, turnResponse) * dt);
            tunnel.Turn(yawRate * dt);

            // The checks run surfaceClearance ahead of the kart, so it stops that far before any edge or wall.
            var advance = Mathf.Min(speed * dt, Mathf.Max(0f, digDistance - travelled));
            var reach = advance + surfaceClearance;
            var clear = tunnel.Probe(reach);
            var move = clear >= reach - 1e-4f ? advance : Mathf.Clamp(clear - surfaceClearance, 0f, advance);
            var pose = tunnel.Advance(move);
            travelled += move;
            tunnel.SetHead(pose);
            DriveBodyTo(pose, dt);

            if (mound != null)
            {
                if (mound.NeedsCapture(tunnel.Ground)) mound.Capture(CaptureArea(), tunnel.Heading);
                mound.MoveHead(tunnel.Ground, tunnel.Normal, tunnel.Forward);
            }
            dirt.Dig(tunnel.Ground, tunnel.Normal, tunnel.Forward, speed, dt);
            if (phaseTime > LaunchGrace) LaunchKartsAtHead();

            var ended = travelled >= digDistance - 1e-3f || move < advance - 1e-4f;
            if (ended && phaseTime >= sinkTime) Surface(pose);
        }

        void DriveBodyTo(Pose pose, float dt)
        {
            var body = Owner.Physics.Body;
            var targetPos = tunnel.Ground + tunnel.Normal * rideHeight;
            body.linearVelocity = (targetPos - body.position) / dt;
            body.angularVelocity = Vector3.zero;
            body.MoveRotation(Quaternion.Slerp(body.rotation, pose.rotation, Core.Smoothing.Factor(RotationSharpness, dt)));
        }

        void Surface(Pose head)
        {
            var pose = tunnel.SurfacePose(head, Owner.Physics.Chassis, rideHeight);
            PlaceOwnerAt(pose);
            RestoreOwner();
            tunnel.SetHead(pose);
            if (mound != null)
            {
                mound.MoveHead(tunnel.Ground, tunnel.Normal, tunnel.Forward);
                mound.Release();
            }
            dirt.Burst(tunnel.Ground, tunnel.Normal, tunnel.Forward);
            PlaySfx(popSound, tunnel.Ground);
            dirt.Spray(false);
            phase = Phase.Surfacing;
            phaseTime = 0f;
            LaunchKartsAtHead();
        }

        void PlaceOwnerAt(Pose pose)
        {
            var body = Owner.Physics.Body;
            var up = pose.rotation * Vector3.up;
            var pos = pose.position + up * (rideHeight + SurfaceLift);
            body.position = pos;
            body.rotation = pose.rotation;
            Owner.transform.SetPositionAndRotation(pos, pose.rotation);
            body.linearVelocity = pose.rotation * Vector3.forward * (Owner.NormalTopSpeed * exitSpeedShare) + up * popHop;
            body.angularVelocity = Vector3.zero;
        }

        void RestoreOwner()
        {
            Effects.EffectLoopSound.Stop(ref loop);
            if (Owner == null) return;
            Owner.Physics.Suspended = false;
            Owner.Physics.Body.detectCollisions = true;
            if (tunnelEffect != null) Owner.Effects.Remove(tunnelEffect);
            Hide(false);
        }

        void EndDig()
        {
            if (phase == Phase.Digging && mound != null) mound.Release();
            phase = Phase.Done;
            phaseTime = 0f;
            visualOffset = 0f;
            ApplyVisualOffset();
            dirt?.Spray(false);
        }

        void Hide(bool hide)
        {
            if (hide == hidden) return;
            hidden = hide;
            if (!hide)
            {
                foreach (var r in hiddenRenderers)
                    if (r != null) r.enabled = true;
                hiddenRenderers.Clear();
                return;
            }
            hiddenRenderers.Clear();
            if (visualRoot == null) return;
            visualRoot.GetComponentsInChildren(hiddenRenderers);
            hiddenRenderers.RemoveAll(r => !r.enabled);
            foreach (var r in hiddenRenderers) r.enabled = false;
        }

        void LaunchKartsAtHead()
        {
            var karts = Kart.All;
            var ground = tunnel.Ground;
            var normal = tunnel.Normal;
            var right = Vector3.Cross(normal, tunnel.Forward).normalized;
            for (var i = karts.Count - 1; i >= 0; i--)
            {
                var k = karts[i];
                if (k == Owner || !IsTangible(k) || launched.Contains(k)) continue;
                var d = k.transform.position - ground;
                var h = Vector3.Dot(d, normal);
                if (h < -LaunchBelow || h > hitHeight) continue;
                var flat = d - normal * h;
                var dist = flat.magnitude;
                if (dist > hitRadius) continue;
                launched.Add(k);
                if (hitEffect != null && !KartReactions.Apply(k, hitEffect, Owner)) continue;
                Launch(k, flat, dist, right);
            }
        }

        // Right above the head: straight up. Beside it: thrown to that side with a little roll away from the head.
        void Launch(Kart k, Vector3 flat, float dist, Vector3 right)
        {
            var normal = tunnel.Normal;
            var forward = tunnel.Forward;
            var side = Vector3.Dot(flat, right);
            var sideways = dist > CentreDistance ? Mathf.Clamp01(Mathf.Abs(side) / Mathf.Max(MinSideDistance, dist)) * Mathf.Clamp01(dist / (hitRadius * 0.5f)) : 0f;
            var sign = side >= 0f ? 1f : -1f;
            var v = normal * (launchUp * (1f - UpLossBeside * sideways)) + right * (sign * launchSide * sideways) + forward * launchForward;
            var rb = k.Physics.Body;
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, Mathf.Max(0f, rb.linearVelocity.y), rb.linearVelocity.z) + v;
            rb.angularVelocity += forward * (-sign * TumbleRoll * sideways);
            PlaySfx(hitSound, k.transform.position);
            dirt.Burst(tunnel.Ground, normal, forward, SideBurstShare);
        }
    }
}
