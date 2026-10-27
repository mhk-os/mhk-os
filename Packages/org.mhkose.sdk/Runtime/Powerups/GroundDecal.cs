using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Powerups
{
    /// <summary>
    /// A round texture stamped onto the level geometry below this transform (MHK2 black hole: hole.bmp), a polar disc
    /// whose vertices are projected along -up onto solid ground (<see cref="ItemPhysics"/>). Setting <see cref="Radius"/>
    /// re-projects it, 0 hides it.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GroundDecal : MonoBehaviour
    {
        const float MinVisibleRadius = 0.001f;

        [SerializeField, Range(1, 16)] int rings = 6;
        [SerializeField, Range(6, 64)] int segments = 24;
        [Tooltip("Height above the ground the decal floats at, to avoid z-fighting (m).")]
        [SerializeField] float lift = 0.04f;
        [Tooltip("Projection rays start this far above the decal plane (m).")]
        [SerializeField] float probeHeight = 1f;
        [Tooltip("...and reach this far below it (m).")]
        [SerializeField] float probeDepth = 3f;
        [SerializeField] LayerMask groundMask = ~0;

        Mesh mesh;
        MeshRenderer meshRenderer;
        Vector3[] vertices;
        float radius = -1f;

        /// <summary>Disc radius (m). Changing it re-projects the mesh onto the ground.</summary>
        public float Radius
        {
            get => Mathf.Max(0f, radius);
            set
            {
                value = Mathf.Max(0f, value);
                if (Mathf.Approximately(value, radius)) return;
                radius = value;
                Project();
            }
        }

        /// <summary>
        /// Moves the decal to <paramref name="position"/> and <paramref name="rotation"/> (its up is the projection axis)
        /// and re-projects it at <paramref name="radius"/>, also when the radius is unchanged (a decal that moves).
        /// </summary>
        public void Stamp(Vector3 position, Quaternion rotation, float radius)
        {
            transform.SetPositionAndRotation(position, rotation);
            this.radius = Mathf.Max(0f, radius);
            Project();
        }

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            BuildTopology();
            if (radius < 0f) Radius = 0f;
        }

        void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }

        void BuildTopology()
        {
            mesh = new Mesh { name = "GroundDecal" };
            mesh.MarkDynamic();
            var count = 1 + rings * segments;
            vertices = new Vector3[count];
            var colors = new Color32[count];
            for (var i = 0; i < count; i++) colors[i] = new Color32(255, 255, 255, 255);
            mesh.vertices = vertices;
            mesh.uv = BuildUvs(count);
            mesh.colors32 = colors;
            mesh.triangles = BuildTriangles();
            GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        Vector2[] BuildUvs(int count)
        {
            var uvs = new Vector2[count];
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (var r = 1; r <= rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var a = s * Mathf.PI * 2f / segments;
                var f = 0.5f * r / rings;
                uvs[Index(r, s)] = new Vector2(0.5f + Mathf.Cos(a) * f, 0.5f + Mathf.Sin(a) * f);
            }
            return uvs;
        }

        int[] BuildTriangles()
        {
            var tris = new int[(segments + (rings - 1) * segments * 2) * 3];
            var t = 0;
            for (var s = 0; s < segments; s++)
            {
                var n = (s + 1) % segments;
                tris[t++] = 0; tris[t++] = Index(1, n); tris[t++] = Index(1, s);
            }
            for (var r = 1; r < rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var n = (s + 1) % segments;
                int a = Index(r, s), b = Index(r, n), c = Index(r + 1, s), d = Index(r + 1, n);
                tris[t++] = a; tris[t++] = b; tris[t++] = c;
                tris[t++] = b; tris[t++] = d; tris[t++] = c;
            }
            return tris;
        }

        int Index(int ring, int segment) => 1 + (ring - 1) * segments + segment;

        void Project()
        {
            if (mesh == null) return;
            meshRenderer.enabled = radius > MinVisibleRadius;
            if (!meshRenderer.enabled) return;
            var up = transform.up;
            var right = transform.right;
            var forward = transform.forward;
            var centre = transform.position;
            vertices[0] = Ground(centre, up);
            for (var r = 1; r <= rings; r++)
            for (var s = 0; s < segments; s++)
            {
                var a = s * Mathf.PI * 2f / segments;
                var planar = centre + (right * Mathf.Cos(a) + forward * Mathf.Sin(a)) * (radius * r / rings);
                vertices[Index(r, s)] = Ground(planar, up);
            }
            mesh.SetVertices(vertices);
            mesh.RecalculateBounds();
        }

        Vector3 Ground(Vector3 planar, Vector3 up)
        {
            var world = ItemPhysics.TryGroundAlong(planar, up, probeHeight, probeDepth, out var hit, groundMask)
                ? hit.point + hit.normal * lift
                : planar + up * lift;
            return transform.InverseTransformPoint(world);
        }
    }
}
