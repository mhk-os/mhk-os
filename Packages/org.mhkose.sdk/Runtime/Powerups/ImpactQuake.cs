using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Put on a falling visual (e.g. the 16-ton weight of <see cref="TargetEffectPowerup"/>): when it hits the ground
    /// <see cref="impactDelay"/> seconds after spawning, karts around it shake visually (<see cref="KartQuake"/>).
    /// </summary>
    public class ImpactQuake : MonoBehaviour
    {
        [Tooltip("Seconds from spawn to impact (match the spawning powerup's delay).")]
        [SerializeField] float impactDelay = 1.4f;
        [Tooltip("Karts within this distance of the impact shake (m).")]
        [SerializeField] float radius = 30f;
        [SerializeField] float duration = 1.6f;
        [Tooltip("Peak hop height at the impact point (m).")]
        [SerializeField] float amplitude = 0.35f;
        [Tooltip("Hops per second (duration × frequency = number of hops).")]
        [SerializeField] float frequency = 7f;
        [Tooltip("The quake spreads outwards at this speed (m/s, 0 = everywhere at once).")]
        [SerializeField] float waveSpeed = 60f;
        [Tooltip("Share of the hop height lost by the end of the quake.")]
        [SerializeField, Range(0f, 1f)] float decay = 0.5f;
        [Tooltip("Share of the peak hop height over the distance from the impact divided by the radius (x 0 = impact, " +
                 "x 1 = radius). Empty = linear fade to 30 % at the radius.")]
        [SerializeField] AnimationCurve falloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        [Tooltip("Karts closer than this to the impact sit under the falling visual and do not hop (m, 0 = all hop).")]
        [SerializeField] float pinnedRadius;

        float age;

        void Update()
        {
            age += Time.deltaTime;
            if (age < impactDelay) return;
            KartQuake.Shake(transform.position, radius, duration, amplitude, frequency, waveSpeed, decay, falloff, pinnedRadius);
            enabled = false;
        }
    }
}
