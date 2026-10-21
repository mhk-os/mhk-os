using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Swings the object back and forth about a local axis from its rest rotation (flapping wings, wobbling props):
    /// angle = offset + amplitude · sin(2π · frequency · t + phase).
    /// </summary>
    public class Oscillator : MonoBehaviour
    {
        [Tooltip("Local rotation axis.")]
        [SerializeField] Vector3 axis = Vector3.forward;
        [Tooltip("Swing to each side (degrees).")]
        [SerializeField] float amplitude = 30f;
        [Tooltip("Full swings per second.")]
        [SerializeField] float frequency = 3f;
        [Tooltip("Phase (degrees). Mirrored pairs use the same phase with opposite amplitude.")]
        [SerializeField] float phase;
        [Tooltip("Constant angle added to the swing (degrees).")]
        [SerializeField] float offset;

        Quaternion rest;
        float t;

        void Awake() => rest = transform.localRotation;

        void OnEnable() => t = 0f;

        void Update()
        {
            t += Time.deltaTime;
            var angle = offset + amplitude * Mathf.Sin(2f * Mathf.PI * frequency * t + phase * Mathf.Deg2Rad);
            transform.localRotation = rest * Quaternion.AngleAxis(angle, axis);
        }
    }
}
