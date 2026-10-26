using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>Kart root: feeds the driver's input to the physics each tick and owns race identity, health and respawning.</summary>
    [RequireComponent(typeof(KartPhysics), typeof(KartEffects), typeof(PowerupInventory))]
    [RequireComponent(typeof(RacerProgress))]
    public partial class Kart : MonoBehaviour, IKartView
    {
        const int MaxRespawnSpotTries = 10;
        const float RespawnCooldownSeconds = 1f;

        public KartPhysics Physics { get; private set; }
        public KartEffects Effects { get; private set; }
        public PowerupInventory Inventory { get; private set; }
        public RacerProgress Progress { get; private set; }
        public KartVisual Visual { get; private set; }

        public CharacterDefinition Character { get; private set; }
        public KartDefinition KartDef { get; private set; }
        public RacerSetup Setup { get; private set; }
        public int RacerIndex { get; private set; }
        public bool IsHuman => Setup != null && Setup.isHuman;
        public string DisplayName => Setup != null && !string.IsNullOrEmpty(Setup.displayName) ? Setup.displayName : Character != null ? Character.DisplayName : name;

        public IKartDriver Driver { get; set; }
        /// <summary>When false the kart ignores its driver (countdown, after finishing for humans until AI takes over).</summary>
        public bool ControlsEnabled { get; set; }
        /// <summary>
        /// Another online racer's kart: a kinematic stand-in that follows the received states (<see cref="Online.RemoteKart"/>).
        /// It runs no driver, physics step, items or respawns here; its client simulates it.
        /// </summary>
        public bool IsRemote { get; private set; }
        public KartInput CurrentInput => input;
        /// <summary>
        /// The input the physics stepped with in the last tick: the driver's, after the countdown / effects locks
        /// (recorded by time-trial ghosts for a future re-simulation).
        /// </summary>
        public KartInput EffectiveInput { get; private set; }

        /// <summary>
        /// Forward speed as a share of the kart's <see cref="NormalTopSpeed"/>: 0 standing, 1 at full throttle on flat
        /// road, above 1 boosted or downhill.
        /// </summary>
        public float SpeedFraction => Physics != null ? Physics.ForwardSpeed / NormalTopSpeed : 0f;

        /// <summary>
        /// Top speed (m/s) at full throttle on flat road without effects (<see cref="KartPhysics.FlatTopSpeed"/> of the
        /// kart's profile, engine class, speed scale and <see cref="GameConfig.driveTorqueScale"/>), recomputed only
        /// when one of them changes. At least 1.
        /// </summary>
        public float NormalTopSpeed
        {
            get
            {
                if (Physics == null) return 1f;
                var profile = Physics.Profile;
                var config = GameRoot.Instance != null ? GameRoot.Instance.Config : null;
                var torque = config != null ? config.driveTorqueScale : 1f;
                if (profile != topSpeedProfile || Physics.ClassFactor != topSpeedClass || Physics.SpeedScale != topSpeedScale || torque != topSpeedTorque)
                {
                    topSpeedProfile = profile;
                    topSpeedClass = Physics.ClassFactor;
                    topSpeedScale = Physics.SpeedScale;
                    topSpeedTorque = torque;
                    normalTopSpeed = Mathf.Max(1f, KartPhysics.FlatTopSpeed(profile, topSpeedClass, topSpeedScale, torque));
                }
                return normalTopSpeed;
            }
        }
        KartPhysicsProfile topSpeedProfile;
        float topSpeedClass, topSpeedScale, topSpeedTorque, normalTopSpeed = 1f;

        /// <summary>
        /// Extra horizontal field of view (degrees) of the active boost effects for the race camera: the largest
        /// <see cref="StatusEffectDefinition.cameraFovBoost"/> × the kart's <see cref="SpeedFraction"/> (at most 1).
        /// </summary>
        public float CameraFovBoost
        {
            get
            {
                if (Effects == null || Physics == null) return 0f;
                var degrees = 0f;
                var active = Effects.Active;
                for (var i = 0; i < active.Count; i++) degrees = Mathf.Max(degrees, active[i].Definition.cameraFovBoost);
                return degrees <= 0f ? 0f : degrees * Mathf.Clamp01(SpeedFraction);
            }
        }

        // IKartView: what the visuals and the race camera read (a replayed ghost provides the same from its recording).
        bool IKartView.RaceOver => Progress.Finished || Eliminated;
        float IKartView.SteerAngle => Physics.SteerAngle;
        float IKartView.SteerCommand01 => Physics.SteerCommand01;
        float IKartView.ForwardSpeed => Physics.ForwardSpeed;
        float IKartView.ForwardSpeedOriginal => Physics.ForwardSpeedOriginal;
        bool IKartView.JustLanded => Physics.JustLanded;
        int IKartView.GroundContacts => Physics.ContactCount;
        float IKartView.SpinAngle => Effects.SpinAngle;
        float IKartView.ScaleMultiplier => Effects.ScaleMultiplier;
        float IKartView.VerticalScale => Effects.VerticalScale;
        float IKartView.Opacity => Effects.Opacity * RemoteFade;
        /// <summary>Remote racers fade out to 0 when they leave the race.</summary>
        internal float RemoteFade { get; set; } = 1f;
        bool IKartView.Burned => Effects.Burned;
        Color IKartView.ModelTint => Effects.ModelTint;
        bool IKartView.Hidden => Effects.Hidden;
        bool IKartView.WheelsHidden => Effects.WheelsHidden;
        BodyBob IKartView.BodyBob => Effects.BodyBob;
        Vector3 IKartView.QuakeOffset => KartQuake.VisualOffset(this);
        bool IKartView.IsEffectAttachment(Transform t) => Effects.IsEffectAttachment(t);

        public event Action<Kart> Respawned;
        public event Action<Kart, Collision> Collided;

        /// <summary>
        /// Thunder health (0..GameConfig.maxHealth). Hits by other karts always cost health, but only battles eliminate
        /// karts at 0.
        /// </summary>
        public int Health { get; private set; }
        /// <summary>Knocked out of a battle: parked, ignores items and hits, not respawned.</summary>
        public bool Eliminated { get; private set; }

        [AutoStaticsCleanup] static readonly List<Kart> all = new();
        /// <summary>All karts currently active in the scene.</summary>
        public static IReadOnlyList<Kart> All => all;

        KartInput input;
        bool respawnRequested;
        float respawnCooldown;
        // Powerup uses (PowerupContext.usage) that already cost this kart health, the oldest overwritten first. Ids only
        // grow, so a slot never matches a newer use; 32 covers every use still able to hit (hazards lying around included).
        const int DamagedUsageMemory = 32;
        readonly int[] damagedUsages = new int[DamagedUsageMemory];
        int nextDamagedUsage;

        void Awake()
        {
            Physics = GetComponent<KartPhysics>();
            Effects = GetComponent<KartEffects>();
            Inventory = GetComponent<PowerupInventory>();
            Progress = GetComponent<RacerProgress>();
        }

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        /// <summary>The kart <paramref name="c"/> belongs to (its rigidbody's kart, else a Kart above it), or null.</summary>
        public static Kart FromCollider(Collider c)
        {
            if (c == null) return null;
            var body = c.attachedRigidbody;
            if (body != null && body.TryGetComponent(out Kart kart)) return kart;
            return c.GetComponentInParent<Kart>();
        }

        /// <summary>Configures identity and attaches visuals. Called once by <see cref="RaceManager"/> after instantiation.</summary>
        public void Initialize(int racerIndex, RacerSetup setup, CharacterDefinition character, KartDefinition kartDef, KartPhysicsProfile physicsProfile)
        {
            RacerIndex = racerIndex;
            Setup = setup;
            Character = character;
            KartDef = kartDef;
            name = $"Kart {racerIndex} ({(character != null ? character.DisplayName : null)})";
            Health = GameRoot.Instance.Config.maxHealth;
            Array.Clear(damagedUsages, 0, DamagedUsageMemory);
            nextDamagedUsage = 0;
            Physics.Profile = physicsProfile;
            Visual = KartVisual.Build(this, character, kartDef);
        }

        void FixedUpdate()
        {
            if (IsRemote) return;
            if (Driver != null) Driver.UpdateInput(this, ref input);
            var effective = FilterInput(input);
            EffectiveInput = effective;
            Physics.Step(effective, Time.fixedDeltaTime);
            if (ControlsEnabled) Inventory.HandleInput(effective);
            CheckRespawnTriggers(effective);
        }

        /// <summary>
        /// Turns this kart into a remote racer's stand-in: no driver, a kinematic body the local kart bumps into. Called
        /// once right after spawning.
        /// </summary>
        internal void BecomeRemote()
        {
            IsRemote = true;
            Driver = null;
            Physics.Body.isKinematic = true;
            Physics.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            Physics.Body.interpolation = RigidbodyInterpolation.Interpolate;
        }

        /// <summary>A remote racer's controls as received (animations and look-back read them).</summary>
        internal void SetRemoteInput(in KartInput remote)
        {
            input = remote;
            EffectiveInput = remote;
        }

        /// <summary>A remote racer's battle health as its client reports it (its own client decides hits on it).</summary>
        public void SetRemoteHealth(int health)
        {
            if (!IsRemote || Eliminated) return;
            Health = Mathf.Clamp(health, 0, GameRoot.Instance.Config.maxHealth);
        }

        KartInput FilterInput(KartInput effective)
        {
            if (!ControlsEnabled || Effects.ControlsLocked)
            {
                effective.steer = 0;
                effective.throttle = 0;
                effective.brake = 0;
                effective.parked = !ControlsEnabled || Effects.Frozen;
                effective.fire = false;
            }
            if (Effects.SteeringInverted) effective.steer = -effective.steer;
            if (Effects.ThrottleBlocked) effective.throttle = 0f;
            if (Effects.ItemUseBlocked) effective.fire = false;
            if (Effects.ForceThrottle && ControlsEnabled)
            {
                effective.throttle = 1f;
                effective.brake = 0f;
            }
            return effective;
        }

        void CheckRespawnTriggers(in KartInput effective)
        {
            respawnCooldown -= Time.fixedDeltaTime;
            var track = TrackRuntime.Current;
            if (TrackRuntime.IsBelowKillHeight(transform.position)) respawnRequested = true;
            if (Physics.IsGrounded) ReactToSurface(Physics.Surface);
            if (track != null && Falling(track)) TouchDropGeometry();
            if (CollapsingBridge.IsDownAt(Physics.Body.position)) TouchDropGeometry();
            if (effective.respawn && ControlsEnabled && !Effects.HasRespawnPending) respawnRequested = true;
            if (Eliminated) respawnRequested = false;
            if (respawnRequested && respawnCooldown <= 0f) RespawnBehind(GameRoot.Instance.Config.respawnBackDistance);
            respawnRequested = false;
        }

        void ReactToSurface(SurfaceKind kind)
        {
            if (kind == SurfaceKind.Deadly) respawnRequested = true;
            else if (kind == SurfaceKind.Drop) TouchDropGeometry();
        }

        public void RequestRespawn() => respawnRequested = true;

        /// <summary>
        /// Thunder damage formula: base × attacker's dealt multiplier (quad 4, hammer 2) × own taken multiplier
        /// (hammer 0.5), rounded down. <paramref name="usage"/> is the powerup use the hit comes from
        /// (<see cref="Powerups.PowerupContext.usage"/>, 0 = none): each use costs this kart health once, so its further
        /// hits (more soap bubbles, the jammer field again, other bananas of the same rain) do not.
        /// </summary>
        public void TakeDamage(int baseDamage, Kart attacker, int usage = 0)
        {
            if (Eliminated || baseDamage <= 0 || Progress.Finished || DamagedBy(usage)) return;
            var dealt = attacker != null ? attacker.Effects.DamageDealtMultiplier : 1f;
            var dmg = Mathf.FloorToInt(baseDamage * dealt * Effects.DamageTakenMultiplier);
            if (dmg <= 0) return;
            Health = Mathf.Clamp(Health - dmg, 0, GameRoot.Instance.Config.maxHealth);
            if (usage == 0) return;
            damagedUsages[nextDamagedUsage] = usage;
            nextDamagedUsage = (nextDamagedUsage + 1) % DamagedUsageMemory;
        }

        bool DamagedBy(int usage)
        {
            if (usage == 0) return false;
            for (var i = 0; i < DamagedUsageMemory; i++)
                if (damagedUsages[i] == usage) return true;
            return false;
        }

        public void Heal(int amount)
        {
            if (Eliminated || amount <= 0) return;
            Health = Mathf.Clamp(Health + amount, 0, GameRoot.Instance.Config.maxHealth);
        }

        /// <summary>Removes the kart from a battle: it stops where it is and plays its losing animation.</summary>
        public void Eliminate()
        {
            if (Eliminated) return;
            Eliminated = true;
            Health = 0;
            ControlsEnabled = false;
            Effects.ClearAll();
            Inventory.Clear();
            Celebrate(false);
        }

        /// <summary>Finish reaction: the character's win / lose animation and voice.</summary>
        public void Celebrate(bool won)
        {
            if (Visual != null) Visual.PlayWin(won);
            if (TryGetComponent<KartAudio>(out var audio)) audio.PlayWin(won);
        }

        /// <summary>
        /// Real-time length of an action animation on this kart: the longer of the driver's and the separate kart model's
        /// clip at its playback speed (a guest driver's own fall plus the kart's drop), 0 when neither has one.
        /// </summary>
        public float AnimationDuration(CharacterAnimRole role)
        {
            if (Visual != null) return Visual.ActionDuration(role);
            return Character != null && Character.TryResolve(role, out var b) ? b.Duration : 0f;
        }

        // MHK2 "drop geometry": freeze, play the falling animation, then respawn.
        void TouchDropGeometry()
        {
            var drop = GameRoot.Instance.Config.dropEffect;
            if (drop == null) { respawnRequested = true; return; }
            if (!Effects.HasRespawnPending) Effects.Apply(drop, this);
        }

        // In the air for TrackRuntime.fallMinAirTime with no ground within fallDepth below (TINcan robots falling off).
        bool Falling(TrackRuntime track)
        {
            if (track.fallDepth <= 0f || Physics.IsGrounded || Physics.AirTime < track.fallMinAirTime || Effects.HasRespawnPending)
                return false;
            return !GroundProbe.Any(transform.position, track.fallDepth, track.groundMask);
        }

        /// <summary>
        /// Puts the kart back on the racing line, <paramref name="backDistance"/> metres behind the last point
        /// where it was safely on the track (MHK2: 15 units, +2 drop height, buffs cancelled, item kept).
        /// Like MHK3, the spot moves further back while another kart occupies it, so respawns never stack.
        /// </summary>
        public void RespawnBehind(float backDistance)
        {
            if (!TrackRuntime.TryGetMainPath(out _)) return;
            var cfg = GameRoot.Instance.Config;
            var pose = RespawnPose(backDistance, cfg);
            Physics.Teleport(pose.position + Vector3.up * cfg.respawnDropHeight, pose.rotation);
            Progress.ResetProjection();
            Effects.ClearAll();
            Effects.ProtectedUntilTick = RaceClock.Ticks + Mathf.CeilToInt(cfg.respawnProtectionSeconds / RaceClock.Step);
            respawnCooldown = RespawnCooldownSeconds;
            respawnRequested = false;
            Respawned?.Invoke(this);
        }

        Pose RespawnPose(float backDistance, GameConfig cfg)
        {
            if (Progress.RespawnOverride.HasValue) return Progress.RespawnOverride.Value;
            var d = Progress.BeforeGaps(Progress.LastSafeDistance - backDistance, cfg.gapRunUp);
            var pose = Progress.GetRespawnPose(d);
            for (var tries = 0; tries < MaxRespawnSpotTries && Occupied(pose.position); tries++)
                pose = Progress.GetRespawnPose(d -= cfg.respawnOccupiedStep);
            return pose;
        }

        bool Occupied(Vector3 spot)
        {
            var r = GameRoot.Instance.Config.respawnClearRadius;
            for (var i = 0; i < all.Count; i++)
                if (all[i] != this && (all[i].transform.position - spot).sqrMagnitude < r * r) return true;
            return false;
        }

        void OnCollisionEnter(Collision c)
        {
            var surface = TrackSurface.Of(c.collider);
            // A remote racer's respawns are its own client's business.
            if (surface != null && !IsRemote) ReactToSurface(surface.kind);
            Collided?.Invoke(this, c);
        }
    }
}
