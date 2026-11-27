using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// The engine part of a kart model (<see cref="KartModel.engine"/>). It is drawn at the engine class size
    /// (<see cref="Core.DifficultySettings.engineModelScale"/>) and shakes while it runs, faster and harder as it revs
    /// (<see cref="Revs"/>). Visual only.
    /// </summary>
    public class KartEngine : MonoBehaviour
    {
        // Shake frequency (Hz) at idle and flat out. Above the frame rate it reads as a buzz, below as a judder.
        const float IdleHz = 11f, RevHz = 23f;
        // Shake amplitude at idle for an engine of modelled size. Lift and sideways sway in metres, rock in degrees.
        const float Lift = 0.005f, Sway = 0.0025f, Rock = 1.4f;
        // How much stronger the shake gets flat out.
        const float RevBoost = 0.8f;

        Vector3 restPosition;
        Quaternion restRotation;
        float size = 1f, phase, seed;

        /// <summary>Share of full revs, 0 at idle to 1 flat out. The kart visual sets it every frame, the menu turntable leaves it idling.</summary>
        public float Revs { get; set; }

        /// <summary>
        /// Sizes the engine of <paramref name="model"/> to <paramref name="classScale"/> of its modelled size about its pivot
        /// and starts it shaking. Null when the model has no engine part.
        /// </summary>
        public static KartEngine Attach(KartModel model, float classScale)
        {
            if (model == null || model.engine == null) return null;
            var engine = model.engine.gameObject.AddComponent<KartEngine>();
            engine.size = Mathf.Max(0.05f, classScale);
            engine.restPosition = model.engine.localPosition;
            engine.restRotation = model.engine.localRotation;
            engine.seed = Random.value * 100f;
            model.engine.localScale *= engine.size;
            return engine;
        }

        void LateUpdate()
        {
            var revs = Mathf.Clamp01(Revs);
            phase += Time.deltaTime * 2f * Mathf.PI * Mathf.Lerp(IdleHz, RevHz, revs);
            phase %= 2f * Mathf.PI * 1000f;
            var strength = size * (1f + RevBoost * revs);
            // Two frequencies a little apart beat against each other, so the shake never looks like a clean wobble.
            var lift = Mathf.Sin(phase) + 0.5f * Mathf.Sin(phase * 1.71f + seed);
            var sway = Mathf.Sin(phase * 1.37f + seed * 2f);
            var rock = Mathf.Sin(phase * 0.83f + seed * 3f);
            transform.localPosition = restPosition + strength * new Vector3(Sway * sway, Lift * lift, 0f);
            transform.localRotation = restRotation * Quaternion.Euler(Rock * strength * rock * 0.5f, 0f, Rock * strength * rock);
        }
    }
}
