using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// The '?' sign above a remote kart whose character or kart is not installed here (online races, see
    /// <see cref="Validation.UnknownContentGuard"/>): it floats above the stand-in driver's head and turns its face to
    /// the camera, so nobody takes the stand-in for the driver the other player actually chose.
    /// </summary>
    [AddComponentMenu("MHK/Unknown Content Marker")]
    public class UnknownContentMarker : MonoBehaviour
    {
        [SerializeField, Tooltip("Height (m) of the sign's centre above the top of the driver's head.")]
        float lift = 0.75f;
        [SerializeField, Tooltip("Height (m) of the sign's centre above the kart when it shows no driver.")]
        float fallbackHeight = 2f;
        [SerializeField, Tooltip("Vertical bob of the sign (m).")]
        float bobAmplitude = 0.08f;
        [SerializeField, Tooltip("Seconds per bob.")]
        float bobPeriod = 1.6f;

        KartVisual visual;
        float phase;

        void Awake()
        {
            visual = GetComponentInParent<KartVisual>();
            phase = Random.value * bobPeriod;
        }

        void LateUpdate()
        {
            var top = visual != null && visual.TryGetDriverTop(out var head) ? head + Vector3.up * lift : Vector3.up * fallbackHeight;
            var bob = bobPeriod > 0f ? Mathf.Sin((Time.time + phase) * (2f * Mathf.PI / bobPeriod)) * bobAmplitude : 0f;
            transform.localPosition = top + Vector3.up * bob;
            // The face (local -Z) towards the camera, upright.
            var cam = Camera.main;
            if (cam == null) return;
            var away = transform.position - cam.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}
