using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Scale animation: grows in over <see cref="growIn"/> seconds, then pulses (MHK2 shield: 1.2 + 0.05·sin(14t)),
    /// optionally wobbling each axis on its own (MHK3 soap bubble).
    /// </summary>
    public class Pulse : MonoBehaviour
    {
        [Tooltip("Scale factor the pulse swings around.")]
        [SerializeField] float baseScale = 1.2f;
        [Tooltip("Sine amplitude added to the base scale.")]
        [SerializeField] float amplitude = 0.05f;
        [Tooltip("Angular frequency of the pulse (rad/s).")]
        [SerializeField] float frequency = 14f;
        [Tooltip("Seconds to grow from zero to full size.")]
        [SerializeField] float growIn = 0.3f;
        [Tooltip("Share of the scale each local axis swings by on its own, 0 = none (MHK3 soap bubble: a jelly wobble).")]
        [SerializeField] Vector3 axisWobble;
        [Tooltip("Full wobble swings per second of each local axis.")]
        [SerializeField] Vector3 axisFrequency = Vector3.one;

        float t;
        Vector3 initial;

        void Awake() => initial = transform.localScale;

        void OnEnable() => t = 0f;

        void Update()
        {
            t += Time.deltaTime;
            var grow = growIn > 0f ? Mathf.Clamp01(t / growIn) : 1f;
            var pulse = (baseScale + amplitude * Mathf.Sin(frequency * t)) * grow;
            transform.localScale = Vector3.Scale(initial, Wobble()) * pulse;
        }

        Vector3 Wobble()
        {
            var w = 2f * Mathf.PI * t;
            return new Vector3(1f + axisWobble.x * Mathf.Sin(w * axisFrequency.x), 1f + axisWobble.y * Mathf.Sin(w * axisFrequency.y),
                1f + axisWobble.z * Mathf.Sin(w * axisFrequency.z));
        }
    }
}
