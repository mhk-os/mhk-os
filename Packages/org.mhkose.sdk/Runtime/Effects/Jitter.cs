using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Jumps the object to a random offset around its rest position now and then, so a trail behind it zigzags like
    /// an electric arc (TINcan Viper's phase).
    /// </summary>
    [AddComponentMenu("MHK/Jitter")]
    public class Jitter : MonoBehaviour
    {
        [SerializeField, Tooltip("Largest offset (m) from the rest position along each local axis.")]
        Vector3 amplitude = new(0.15f, 0.15f, 0f);
        [SerializeField, Tooltip("Seconds between jumps (random in this range).")]
        Vector2 interval = new(0.02f, 0.05f);

        Vector3 rest;
        float next;

        void Awake() => rest = transform.localPosition;

        void Update()
        {
            next -= Time.deltaTime;
            if (next > 0f) return;
            next = Random.Range(interval.x, interval.y);
            transform.localPosition = rest + Vector3.Scale(amplitude, new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f)));
        }
    }
}
