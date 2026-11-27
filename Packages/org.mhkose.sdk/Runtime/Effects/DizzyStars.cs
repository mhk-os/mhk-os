using MHK.Karts;
using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Stars circling the head of a dazed driver, as in TINcan Race. Rings of stars tilted against each other spin around
    /// the head in opposite directions, and the halo sways a little. The kart visual (<see cref="KartVisual"/>) shows them
    /// above any driver while an effect plays its Dizzy animation (<see cref="Show"/>). They grow in and shrink away.
    /// </summary>
    [AddComponentMenu("MHK/Dizzy Stars")]
    public class DizzyStars : MonoBehaviour
    {
        [SerializeField, Tooltip("Rings of stars, each spun about its own up axis.")]
        Transform[] rings = System.Array.Empty<Transform>();
        [SerializeField, Tooltip("Spin of each ring (degrees per second, same order as rings). Negative turns the other way.")]
        float[] spins = { 320f, -260f };
        [SerializeField, Tooltip("Height (m) of the halo's centre above the top of the driver's head. Negative lowers it around the head.")]
        float lift = -0.05f;
        [SerializeField, Tooltip("Height (m) of the halo's centre above the kart when it shows no driver.")]
        float fallbackHeight = 1.1f;
        [SerializeField, Tooltip("Seconds to grow in and to shrink away.")]
        float growSeconds = 0.25f;
        [SerializeField, Tooltip("Sway of the whole halo (degrees).")]
        float sway = 10f;
        [SerializeField, Tooltip("Sways per second.")]
        float swayRate = 1.1f;

        KartVisual visual;
        Vector3 fullScale;
        float shown, phase;
        bool on;

        /// <summary>Starts the stars circling, or lets them shrink away and switch off.</summary>
        public void Show(bool show)
        {
            on = show;
            if (show) gameObject.SetActive(true);
        }

        void Awake()
        {
            visual = GetComponentInParent<KartVisual>();
            fullScale = transform.localScale;
            transform.localScale = Vector3.zero;
            phase = Random.value * 10f;
        }

        void LateUpdate()
        {
            var dt = Time.deltaTime;
            shown = Mathf.MoveTowards(shown, on ? 1f : 0f, growSeconds > 0f ? dt / growSeconds : 1f);
            if (shown <= 0f && !on)
            {
                gameObject.SetActive(false);
                return;
            }
            phase += dt;
            transform.localScale = fullScale * Mathf.SmoothStep(0f, 1f, shown);
            var top = visual != null && visual.TryGetDriverTop(out var head) ? head + Vector3.up * lift : Vector3.up * fallbackHeight;
            var tilt = sway * Mathf.Sin(phase * swayRate * 2f * Mathf.PI);
            transform.SetLocalPositionAndRotation(top, Quaternion.Euler(tilt * 0.6f, 0f, tilt));
            for (var i = 0; i < rings.Length && i < spins.Length; i++)
                if (rings[i] != null) rings[i].Rotate(0f, spins[i] * dt, 0f, Space.Self);
        }
    }
}
