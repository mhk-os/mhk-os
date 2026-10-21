using MHK.Modding;
using UnityEngine;
using UnityEngine.Serialization;

namespace MHK.Powerups
{
    /// <summary>Shared flight, hit and sound settings of self-propelled shots (<see cref="ProjectilePowerup"/>, <see cref="StraightProjectile"/>).</summary>
    public abstract class ProjectileBehaviour : PowerupBehaviour
    {
        [Header("Flight")]
        public float speed = 40f;
        public float lifetime = 10f;
        [Tooltip("Speed lost per second (soap bubbles, snowballs slow down). Stops at 0.")]
        public float deceleration;
        [Tooltip("Spawn distance in front of the kart along the flight direction (m).")]
        [SerializeField] protected float spawnDistance = 1.8f;
        [SerializeField] protected LayerMask worldMask = ~0;

        [Header("Hits")]
        [SerializeField] protected StatusEffectDefinition hitEffect;
        [Tooltip("Karts whose origin comes this close to the flight path are hit (m).")]
        public float hitRadius = 1.1f;

        [Header("Sounds")]
        [Tooltip("One-shot at the spawn point, for every shot (e.g. each bubble of a BurstPowerup).")]
        [SerializeField] protected AudioClip launchSound;
        [Tooltip("Loop that follows the projectile while it flies.")]
        [SerializeField] protected Audio.LoopSound flightLoop = new();
        [Tooltip("One-shot at each kart it hits.")]
        [FormerlySerializedAs("impactSound"), SerializeField] protected AudioClip hitSound;
        [Tooltip("One-shot where it meets a wall or steep terrain.")]
        [SerializeField] protected AudioClip terrainSound;
        [Tooltip("One-shot where it ends because its lifetime ran out.")]
        [SerializeField] protected AudioClip expireSound;

        /// <summary>Seconds flown so far.</summary>
        protected float Age { get; private set; }

        /// <summary>Plays the launch one-shot and starts the flight loop, which ends with the object.</summary>
        protected void PlayLaunch()
        {
            PlaySfx(launchSound, transform.position);
            Effects.EffectLoopSound.Play(gameObject, flightLoop);
        }

        /// <summary>Takes <see cref="deceleration"/> off <see cref="speed"/> for <paramref name="dt"/> seconds, down to 0.</summary>
        protected void Decelerate(float dt)
        {
            if (deceleration > 0f) speed = Mathf.Max(0f, speed - deceleration * dt);
        }

        /// <summary>Advances <see cref="Age"/>. Once the lifetime is over the shot despawns with <paramref name="expireVfx"/> and this returns true.</summary>
        protected bool TickLifetime(float dt, GameObject expireVfx)
        {
            Age += dt;
            if (Age <= lifetime) return false;
            Despawn(expireVfx, transform.position, expireSound);
            return true;
        }
    }
}
