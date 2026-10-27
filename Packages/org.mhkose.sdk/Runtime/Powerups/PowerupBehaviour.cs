using MHK.Audio;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    public readonly struct PowerupContext
    {
        public readonly Kart owner;
        public readonly PowerupDefinition definition;
        public readonly bool backwards;
        /// <summary>
        /// Id of this single use (one charge) of the item, shared by everything it spawns (bursts, scattered hazards,
        /// explosions): a kart loses health once per use however often it is hit (<see cref="Kart.TakeDamage"/>). 0 = none.
        /// </summary>
        public readonly int usage;
        /// <summary>Race tick of the use (<see cref="RaceClock.Ticks"/>; online receivers get the sender's).</summary>
        public readonly int tick;
        /// <summary>
        /// The target the user chose, sent with an online use (<see cref="targetGiven"/>): receivers aim at it instead of
        /// choosing their own from their view of the race. Null with targetGiven = the user had none.
        /// </summary>
        public readonly Kart target;
        public readonly bool targetGiven;

        public PowerupContext(Kart owner, PowerupDefinition definition, bool backwards, int usage)
            : this(owner, definition, backwards, usage, RaceClock.Ticks, null, false) { }

        public PowerupContext(Kart owner, PowerupDefinition definition, bool backwards, int usage, int tick, Kart target, bool targetGiven)
        {
            this.owner = owner;
            this.definition = definition;
            this.backwards = backwards;
            this.usage = usage;
            this.tick = tick;
            this.target = target;
            this.targetGiven = targetGiven;
        }
    }

    /// <summary>A behaviour that carries its object to where it comes to rest (thrown, dropped or lobbed).</summary>
    public interface ILandingPowerup
    {
        bool Landed { get; }
    }

    /// <summary>
    /// A moving powerup object that online clients keep in agreement: every client reports its position
    /// (ProjectileReport) and the server sends a correction when the reports spread (ProjectileCorrect).
    /// </summary>
    public interface INetProjectile
    {
        /// <summary>Still moving (reports stop once it rests or ends).</summary>
        bool Flying { get; }
        /// <summary>Position of the last simulated step.</summary>
        Vector3 NetPosition { get; }
        Vector3 NetVelocity { get; }
        /// <summary>Blends towards <paramref name="position"/> (already extrapolated to the current tick) moving with <paramref name="velocity"/>.</summary>
        void Correct(Vector3 position, Vector3 velocity);
    }

    /// <summary>
    /// Base class of the composable components on a powerup's use prefab. All behaviours on the spawned
    /// prefab receive <see cref="Launch"/>, so they combine into new powerups without code. Gameplay runs in physics
    /// steps (<see cref="Step"/>), never on frame time, so a use plays out alike at any frame rate and on every online
    /// client; moving objects draw interpolated (<see cref="InterpolatePose"/>).
    /// </summary>
    public abstract class PowerupBehaviour : MonoBehaviour
    {
        [Tooltip("Volume of this behaviour's one-shot sounds.")]
        [SerializeField, Range(0f, 1f)] protected float soundVolume = 1f;

        // Share of an online position correction applied per step (about 90 % within 10 steps) and when it is done.
        const float CorrectionShare = 0.2f;
        const float CorrectionDoneSqr = 1e-6f;

        ILandingPowerup landing;
        PoseInterpolation interpolation;
        Vector3 correction;

        protected PowerupContext Context { get; private set; }
        protected Kart Owner => Context.owner;
        /// <summary>
        /// The owner's <see cref="KartPhysics.SpeedScale"/>, to convert the scaled kart velocity to original units.
        /// Self-propelled items use their original world speeds.
        /// </summary>
        protected float SpeedScale => Owner != null && Owner.Physics != null ? Owner.Physics.SpeedScale : KartPhysics.GlobalSpeedScale;
        /// <summary>Race tick of the use (<see cref="PowerupContext.tick"/>): owner immunity counts from it.</summary>
        protected int LaunchTick => Context.tick;
        /// <summary>
        /// The kart this use aims at (homing target, hammer target, the single target of a targeted item), sent with an
        /// online use. Null = none.
        /// </summary>
        public Kart ChosenTarget { get; protected set; }
        /// <summary>An <see cref="ILandingPowerup"/> on this object still carries it.</summary>
        protected bool InFlight => landing != null && !landing.Landed;
        /// <summary>An <see cref="ILandingPowerup"/> on this object has put it down.</summary>
        protected bool HasLanded => landing != null && landing.Landed;

        /// <summary>
        /// Seconds after the launch during which a placed or thrown hazard spares its owner
        /// (<see cref="Core.GameConfig.ownerHazardImmunity"/>). Other karts are hit at once.
        /// </summary>
        public static float OwnerHazardImmunity =>
            Core.GameRoot.Instance != null && Core.GameRoot.Instance.Config != null ? Core.GameRoot.Instance.Config.ownerHazardImmunity : 0f;

        /// <summary>Starts the behaviour for the kart and item in <paramref name="ctx"/>.</summary>
        public void Launch(PowerupContext ctx)
        {
            Context = ctx;
            TryGetComponent(out landing);
            OnLaunch();
        }

        protected abstract void OnLaunch();

        /// <summary>One physics step of the behaviour's gameplay (flight, timers, hit tests). Not called while disabled.</summary>
        protected virtual void Step(float dt) { }

        /// <summary>
        /// The behaviour can simulate several steps at once (<see cref="StepOnce"/>) to catch up with an online use that
        /// started ticks ago. Off for behaviours that move karts or rigidbodies through the physics step.
        /// </summary>
        protected internal virtual bool FastForwards => true;

        void FixedUpdate() => StepOnce();

        /// <summary>Simulates one physics step now (also used to fast-forward).</summary>
        internal void StepOnce()
        {
            Step(Time.fixedDeltaTime);
            if (interpolation != null) interpolation.Capture();
        }

        /// <summary>
        /// Draws this object between its simulated steps from now on (call after placing it in <see cref="OnLaunch"/>).
        /// </summary>
        protected void InterpolatePose()
        {
            if (interpolation == null && !TryGetComponent(out interpolation)) interpolation = gameObject.AddComponent<PoseInterpolation>();
            interpolation.Snap();
        }

        /// <summary>The simulated position: the transform may show an interpolated one between steps (<see cref="InterpolatePose"/>).</summary>
        protected Vector3 SimulatedPosition => interpolation != null ? interpolation.SimulatedPosition : transform.position;

        /// <summary>
        /// <paramref name="k"/> is this item's owner and the use is less than <see cref="OwnerHazardImmunity"/> old (race
        /// ticks since <see cref="LaunchTick"/>).
        /// </summary>
        protected bool OwnerImmune(Kart k) => k != null && k == Owner && RaceClock.SecondsAt(RaceClock.Ticks - LaunchTick) < OwnerHazardImmunity;

        /// <summary>The kart exists, races and is not a ghost, so items can hit it.</summary>
        protected static bool IsTangible(Kart k) => k != null && !k.Eliminated && !k.Effects.Ghost;

        /// <summary>Applies <paramref name="effect"/> from the owner as this item's use. True when it landed or there is no effect.</summary>
        protected bool HitKart(Kart k, StatusEffectDefinition effect) => effect == null || KartReactions.Apply(k, effect, Owner, -1f, Usage);

        /// <summary>This item's use (<see cref="PowerupContext.usage"/>): pass it to <see cref="KartReactions.Apply"/> hits.</summary>
        protected int Usage => Context.usage;

        /// <summary>The owner's velocity (m/s): a remote racer's as its latest state says.</summary>
        protected Vector3 OwnerVelocity => OnlineItems.VelocityOf(Owner);

        /// <summary>
        /// The kart's motion is simulated here, so an item may push or hold it. A remote racer's stand-in only follows
        /// its client: items never move it.
        /// </summary>
        protected static bool MovesHere(Kart k) => k != null && !k.IsRemote;

        /// <summary>The owner's speed (m/s) along the unit vector <paramref name="direction"/>.</summary>
        protected float OwnerSpeedAlong(Vector3 direction) => Vector3.Dot(OwnerVelocity, direction);

        /// <summary>
        /// Starts moving the simulated position towards <paramref name="target"/> over the next steps
        /// (<see cref="ApplyCorrection"/>), so an online correction never makes the object jump.
        /// </summary>
        protected void BeginCorrection(Vector3 target) => correction = target - SimulatedPosition;

        /// <summary>Moves the transform a share of the remaining correction (<see cref="BeginCorrection"/>). Call at the start of a step.</summary>
        protected void ApplyCorrection()
        {
            if (correction == Vector3.zero) return;
            var move = correction * CorrectionShare;
            transform.position += move;
            correction -= move;
            if (correction.sqrMagnitude < CorrectionDoneSqr) correction = Vector3.zero;
        }

        /// <summary>The definition's held mount, or <paramref name="fallback"/> without a definition.</summary>
        protected HeldMount HeldMountOr(HeldMount fallback) => Context.definition != null ? Context.definition.heldMount : fallback;

        /// <summary>Plays a one-shot at <paramref name="at"/> with <see cref="soundVolume"/>, carrying as far as <paramref name="range"/> says.</summary>
        protected void PlaySfx(AudioClip clip, Vector3 at, SoundRange range = null) => AudioManager.PlaySfx(clip, at, soundVolume, 1f, range);

        /// <summary><see cref="PlaySfx(AudioClip, Vector3, SoundRange)"/> following <paramref name="target"/> (a kart it belongs to).</summary>
        protected void PlaySfx(AudioClip clip, Transform target, SoundRange range = null) => AudioManager.PlaySfx(clip, target, soundVolume, 1f, range);

        /// <summary>Spawns <paramref name="vfx"/> and <paramref name="sound"/> at <paramref name="at"/> and removes this object.</summary>
        protected void Despawn(GameObject vfx, Vector3 at, AudioClip sound, SoundRange range = null)
        {
            PowerupFx.Spawn(vfx, at, sound, soundVolume, range);
            Remove();
        }

        /// <summary>
        /// Removes this object at once: deactivated now (nothing of it runs or hits any more, also while a use is caught
        /// up within one frame), destroyed at the end of the frame.
        /// </summary>
        protected void Remove()
        {
            gameObject.SetActive(false);
            Destroy(gameObject);
        }

        /// <summary>Removes the object without effects (an online use the server rejected).</summary>
        internal void Discard() => Remove();

        /// <summary>
        /// Online: <paramref name="victim"/>'s client confirmed a hit by this use (HitApplied) and this part is the one
        /// that can have made it (<see cref="NetHitReach"/>). Shots that stop at a kart end here too, hazards count the
        /// hit. Nothing is applied to the victim.
        /// </summary>
        protected internal virtual void OnNetHit(Kart victim) { }

        /// <summary>Distance (m) from <see cref="NetHitPosition"/> within which this part hits a kart; 0 = it never ends by a hit.</summary>
        protected internal virtual float NetHitReach => 0f;

        /// <summary>Where this part hits from (its simulated position).</summary>
        internal Vector3 NetHitPosition => SimulatedPosition;

        /// <summary>
        /// Distance (m) from the owner's origin to the edge of its chassis box along the unit vector
        /// <paramref name="direction"/>, so items can be released just clear of the kart. 0 without an owner chassis.
        /// </summary>
        protected float OwnerChassisReach(Vector3 direction)
        {
            var chassis = Owner != null && Owner.Physics != null ? Owner.Physics.Chassis : null;
            if (chassis == null) return 0f;
            var t = chassis.transform;
            var s = t.lossyScale;
            var half = new Vector3(chassis.size.x * Mathf.Abs(s.x), chassis.size.y * Mathf.Abs(s.y), chassis.size.z * Mathf.Abs(s.z)) * 0.5f;
            return Vector3.Dot(t.TransformPoint(chassis.center) - Owner.transform.position, direction)
                   + half.x * Mathf.Abs(Vector3.Dot(t.right, direction))
                   + half.y * Mathf.Abs(Vector3.Dot(t.up, direction))
                   + half.z * Mathf.Abs(Vector3.Dot(t.forward, direction));
        }

        /// <summary>Enables or disables every non-null behaviour in <paramref name="behaviours"/>.</summary>
        protected static void SetEnabled(Behaviour[] behaviours, bool on)
        {
            if (behaviours == null) return;
            foreach (var b in behaviours)
                if (b != null) b.enabled = on;
        }
    }
}
