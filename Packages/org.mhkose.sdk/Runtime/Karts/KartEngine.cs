using MHK.Core;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// The engine part of a kart model (<see cref="KartModel.engine"/>). It is drawn at the engine class size
    /// (<see cref="DifficultySettings.engineModelScale"/>) and shakes while it runs. The shake gets quicker and stronger
    /// with the revs (<see cref="Revs"/>). Supercharged engines (<see cref="DifficultySettings.engineSparks"/>) of racing
    /// karts also throw sparks (<see cref="KartEngineSparks"/>). Visual only.
    /// </summary>
    public class KartEngine : MonoBehaviour
    {
        // Shake frequency (Hz) at idle and flat out. Above the frame rate it reads as a buzz, below as a judder.
        const float IdleHz = 11f, RevHz = 23f;
        // Shake amplitude at idle for an engine of modelled size. Lift and sideways sway in metres, rock in degrees.
        const float Lift = 0.005f, Sway = 0.0025f, Rock = 1.4f;
        // How much stronger the shake gets flat out.
        const float RevBoost = 0.8f;
        const float MinScale = 0.05f;

        Vector3 restPosition;
        Quaternion restRotation;
        float size = 1f, phase, seed;
        bool sparks;

        /// <summary>Share of full revs, 0 at idle to 1 flat out. The kart visual sets it every frame, the menu turntable leaves it idling.</summary>
        public float Revs { get; set; }

        /// <summary>
        /// Sizes the engine of <paramref name="model"/> to the size of <paramref name="engineClass"/> and starts it shaking
        /// (and sparking for a supercharged class once it turns out to be on a racing kart). Null when the model has no
        /// engine part.
        /// </summary>
        public static KartEngine Attach(KartModel model, DifficultySettings engineClass)
        {
            if (model == null || model.engine == null) return null;
            var part = model.engine;
            var engine = part.gameObject.AddComponent<KartEngine>();
            engine.size = Mathf.Max(MinScale, engineClass != null ? engineClass.engineModelScale : 1f);
            engine.sparks = engineClass != null && engineClass.engineSparks;
            engine.restPosition = part.localPosition + (engine.size > 1f ? GrowthShift(model, part, engine.size) : Vector3.zero);
            engine.restRotation = part.localRotation;
            engine.seed = Random.value * 100f;
            part.localScale *= engine.size;
            part.localPosition = engine.restPosition;
            return engine;
        }

        // A bigger engine grows away from the driver. Scaled about its pivot (the bottom centre, KartEngineSplitter) its
        // face towards the seat would move by (size - 1) times its reach from the pivot, and the base karts seat the
        // driver only 0.2 to 0.45 m in front of that face (MHK Extra least). So the pivot moves back by as much and that
        // face stays where it was modelled. Returns the pivot's shift in the parent's space, zero without a seat or mesh.
        static Vector3 GrowthShift(KartModel model, Transform part, float size)
        {
            if (model.seat == null || !part.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) return Vector3.zero;
            var forward = model.transform.forward;
            var ahead = Vector3.Dot(model.seat.position - part.position, forward);
            if (Mathf.Approximately(ahead, 0f)) return Vector3.zero;
            // Towards the seat along the kart's axis, in the part's own axes (unit length, its scale left out).
            var towardSeat = part.InverseTransformDirection(forward * Mathf.Sign(ahead));
            var b = filter.sharedMesh.bounds;
            var reach = Vector3.Dot(b.center, towardSeat) + Mathf.Abs(b.extents.x * towardSeat.x) + Mathf.Abs(b.extents.y * towardSeat.y)
                        + Mathf.Abs(b.extents.z * towardSeat.z);
            if (reach <= 0f) return Vector3.zero;
            return part.localRotation * Vector3.Scale(part.localScale, -towardSeat * ((size - 1f) * reach));
        }

        // Sparks only on karts in a race (the menu turntable has no kart view above it). They are added after the kart
        // visual has gathered its renderers, so the see-through effect leaves them alone.
        void Start()
        {
            if (!sparks) return;
            var view = GetComponentInParent<IKartView>();
            if (view != null) gameObject.AddComponent<KartEngineSparks>().Setup(this, view);
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
