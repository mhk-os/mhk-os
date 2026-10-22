using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Put on a status effect's <see cref="StatusEffectDefinition.attachedVfxPrefab"/>: spawns <see cref="prefab"/> at
    /// every wheel of the kart, its +Z pointing out of the kart's side (MHK2 x-field: air hissing out of the popped
    /// tyres). Karts without wheel transforms use the lower corners of their visual bounds.
    /// </summary>
    public class WheelAttachedVfx : MonoBehaviour
    {
        [Tooltip("Emitter spawned at each wheel.")]
        [SerializeField] GameObject prefab;
        [Tooltip("Distance the emitters sit outside the wheel centres (m).")]
        [SerializeField] float outwardOffset = 0.05f;

        static readonly Vector3 FallbackMin = new(-0.5f, -0.3f, -0.7f);
        static readonly Vector3 FallbackMax = new(0.5f, 0.5f, 0.7f);
        const float WheelHeightShare = 0.2f;
        const float TrackWidthShare = 0.85f;
        const float FrontAxleShare = 0.8f;
        const float RearAxleShare = 0.2f;

        void Start()
        {
            var kart = GetComponentInParent<Kart>();
            if (kart == null || prefab == null) return;
            var space = kart.Visual != null ? kart.Visual.Body : kart.transform;
            foreach (var p in WheelPositions(kart, space))
            {
                var side = p.x >= 0f ? Vector3.right : Vector3.left;
                Instantiate(prefab, space.TransformPoint(p + side * outwardOffset), space.rotation * Quaternion.LookRotation(side, Vector3.up), transform);
            }
        }

        // Wheel centres in the space of the drawn kart body (turned by spin-outs and the body bob like the wheels).
        static List<Vector3> WheelPositions(Kart kart, Transform space)
        {
            var list = new List<Vector3>();
            WheelCentres(kart, space, list);
            if (list.Count > 0) return list;
            if (!VisualBounds(space, out var min, out var max))
            {
                min = FallbackMin;
                max = FallbackMax;
            }
            CornerWheels(min, max, list);
            return list;
        }

        static void WheelCentres(Kart kart, Transform space, List<Vector3> list)
        {
            var model = kart.Visual != null ? kart.Visual.Model : null;
            if (model == null) return;
            foreach (var w in model.frontWheels) if (w != null) list.Add(space.InverseTransformPoint(w.position));
            foreach (var w in model.rearWheels) if (w != null) list.Add(space.InverseTransformPoint(w.position));
        }

        static bool VisualBounds(Transform space, out Vector3 min, out Vector3 max)
        {
            var any = false;
            min = max = default;
            foreach (var r in space.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                var b = r.localBounds;
                for (var i = 0; i < 8; i++)
                {
                    var p = space.InverseTransformPoint(r.transform.TransformPoint(BoundsUtil.Corner(b, i)));
                    if (!any) { min = max = p; any = true; }
                    else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                }
            }
            return any;
        }

        static void CornerWheels(Vector3 min, Vector3 max, List<Vector3> list)
        {
            var y = min.y + WheelHeightShare * (max.y - min.y);
            var x = TrackWidthShare * Mathf.Max(Mathf.Abs(min.x), Mathf.Abs(max.x));
            var zf = min.z + FrontAxleShare * (max.z - min.z);
            var zr = min.z + RearAxleShare * (max.z - min.z);
            list.Add(new Vector3(-x, y, zf));
            list.Add(new Vector3(x, y, zf));
            list.Add(new Vector3(-x, y, zr));
            list.Add(new Vector3(x, y, zr));
        }
    }
}
