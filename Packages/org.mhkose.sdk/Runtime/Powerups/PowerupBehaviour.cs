using MHK.Audio;
using MHK.Karts;
using MHK.Modding;
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

        public PowerupContext(Kart owner, PowerupDefinition definition, bool backwards, int usage)
        {
            this.owner = owner;
            this.definition = definition;
            this.backwards = backwards;
            this.usage = usage;
        }
    }

    /// <summary>A behaviour that carries its object to where it comes to rest (thrown, dropped or lobbed).</summary>
    public interface ILandingPowerup
    {
        bool Landed { get; }
    }

    /// <summary>
    /// Base class of the composable components on a powerup's use prefab. All behaviours on the spawned
    /// prefab receive <see cref="Launch"/>, so they combine into new powerups without code.
    /// </summary>
    public abstract class PowerupBehaviour : MonoBehaviour
    {
        [Tooltip("Volume of this behaviour's one-shot sounds.")]
        [SerializeField, Range(0f, 1f)] protected float soundVolume = 1f;

        ILandingPowerup landing;

        protected PowerupContext Context { get; private set; }
        protected Kart Owner => Context.owner;
        /// <summary>
        /// The owner's <see cref="KartPhysics.SpeedScale"/>, to convert the scaled kart velocity to original units.
        /// Self-propelled items use their original world speeds.
        /// </summary>
        protected float SpeedScale => Owner != null && Owner.Physics != null ? Owner.Physics.SpeedScale : KartPhysics.GlobalSpeedScale;
        /// <summary><see cref="Time.time"/> at <see cref="Launch"/> (when the item was placed, dropped or thrown).</summary>
        protected float LaunchTime { get; private set; }
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
            LaunchTime = Time.time;
            TryGetComponent(out landing);
            OnLaunch();
        }

        protected abstract void OnLaunch();

        /// <summary><paramref name="k"/> is this item's owner and the item was launched less than <see cref="OwnerHazardImmunity"/> ago.</summary>
        protected bool OwnerImmune(Kart k) => k != null && k == Owner && Time.time - LaunchTime < OwnerHazardImmunity;

        /// <summary>The kart exists, races and is not a ghost, so items can hit it.</summary>
        protected static bool IsTangible(Kart k) => k != null && !k.Eliminated && !k.Effects.Ghost;

        /// <summary>Applies <paramref name="effect"/> from the owner as this item's use. True when it landed or there is no effect.</summary>
        protected bool HitKart(Kart k, StatusEffectDefinition effect) => effect == null || KartReactions.Apply(k, effect, Owner, -1f, Usage);

        /// <summary>This item's use (<see cref="PowerupContext.usage"/>): pass it to <see cref="KartReactions.Apply"/> hits.</summary>
        protected int Usage => Context.usage;

        /// <summary>The owner's speed (m/s) along the unit vector <paramref name="direction"/>.</summary>
        protected float OwnerSpeedAlong(Vector3 direction) => Vector3.Dot(Owner.Physics.Body.linearVelocity, direction);

        /// <summary>The definition's held mount, or <paramref name="fallback"/> without a definition.</summary>
        protected HeldMount HeldMountOr(HeldMount fallback) => Context.definition != null ? Context.definition.heldMount : fallback;

        /// <summary>Plays a one-shot at <paramref name="at"/> with <see cref="soundVolume"/>, carrying as far as <paramref name="range"/> says.</summary>
        protected void PlaySfx(AudioClip clip, Vector3 at, SoundRange range = null) => AudioManager.PlaySfx(clip, at, soundVolume, 1f, range);

        /// <summary>Spawns <paramref name="vfx"/> and <paramref name="sound"/> at <paramref name="at"/> and removes this object.</summary>
        protected void Despawn(GameObject vfx, Vector3 at, AudioClip sound, SoundRange range = null)
        {
            PowerupFx.Spawn(vfx, at, sound, soundVolume, range);
            Destroy(gameObject);
        }

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
