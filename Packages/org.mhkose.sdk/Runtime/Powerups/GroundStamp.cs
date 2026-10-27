using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Shows a flat round visual (a disc mesh with a planar texture: the TINcan signal jammer wave) as a
    /// <see cref="GroundDecal"/> stamped onto the ground below it, oriented to the ground's slope and conforming to bumps,
    /// instead of in the plane it was modelled in. Its place and size follow the source every frame (growing, pulsing, a
    /// field carried by its user). Execution order 1000: after whatever moved or scaled the source this frame.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class GroundStamp : MonoBehaviour
    {
        const float GroundProbeUp = 2f;
        const float GroundProbeDown = 4f;
        const float MinForwardSqr = 1e-6f;

        Bounds meshBounds;
        GroundDecal decal;

        /// <summary>Hides the flat mesh of <paramref name="filter"/> and stamps it onto the ground instead.</summary>
        public static void Attach(MeshFilter filter, MeshRenderer renderer)
        {
            var stamp = filter.gameObject.AddComponent<GroundStamp>();
            stamp.meshBounds = filter.sharedMesh.bounds;
            // Not below the source: it scales through zero while a field grows in.
            var go = new GameObject(filter.name + " (ground)") { layer = filter.gameObject.layer };
            go.transform.SetParent(PowerupFx.Root, false);
            stamp.decal = go.AddComponent<GroundDecal>();
            go.GetComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
            renderer.enabled = false;
            go.SetActive(stamp.isActiveAndEnabled);
            stamp.Stamp();
        }

        void OnEnable()
        {
            if (decal != null) decal.gameObject.SetActive(true);
        }

        void OnDisable()
        {
            if (decal != null) decal.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (decal != null) Destroy(decal.gameObject);
        }

        void LateUpdate() => Stamp();

        void Stamp()
        {
            var t = transform;
            var centre = t.TransformPoint(meshBounds.center);
            var scale = t.lossyScale;
            var radius = Mathf.Max(meshBounds.extents.x * Mathf.Abs(scale.x), meshBounds.extents.z * Mathf.Abs(scale.z));
            var up = Vector3.up;
            if (ItemPhysics.TryGroundBelow(centre, GroundProbeUp, GroundProbeDown, out var hit, Physics.DefaultRaycastLayers))
            {
                centre = hit.point;
                up = hit.normal;
            }
            var forward = Vector3.ProjectOnPlane(t.forward, up);
            if (forward.sqrMagnitude < MinForwardSqr) forward = Vector3.Cross(t.right, up);
            decal.Stamp(centre, Quaternion.LookRotation(forward, up), radius);
        }
    }
}
