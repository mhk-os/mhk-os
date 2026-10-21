using UnityEngine;

namespace MHK.Lighting
{
    /// <summary>Flickers a light's intensity around its set value with smooth noise (torches, fires, candles).</summary>
    [RequireComponent(typeof(Light))]
    public class LightFlicker : MonoBehaviour
    {
        [Tooltip("Share of the intensity that flickers (0.2 = 80 to 120 %).")]
        [Range(0f, 1f)] [SerializeField] float amount = 0.2f;
        [Tooltip("Flicker speed (noise steps per second).")]
        [SerializeField] float speed = 6f;

        /// <summary>Range of the random noise row, so lights do not flicker in sync.</summary>
        const float SeedRange = 100f;

        Light lamp;
        float baseIntensity;
        float seed;

        void Awake()
        {
            lamp = GetComponent<Light>();
            baseIntensity = lamp.intensity;
            seed = Random.value * SeedRange;
        }

        void Update()
        {
            var noise = Mathf.PerlinNoise(seed, Time.time * speed);
            lamp.intensity = baseIntensity * (1f - amount + 2f * amount * noise);
        }
    }
}
