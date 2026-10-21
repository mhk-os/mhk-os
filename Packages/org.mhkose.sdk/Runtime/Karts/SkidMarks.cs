using System.Runtime.InteropServices;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Scene-wide pool of tyre mark quads shared by all karts: one dynamic ring-buffer mesh (one draw call) where each new
    /// segment overwrites the oldest once full. The material samples its texture's middle row, vertex colours tint it.
    /// </summary>
    public sealed partial class SkidMarks : MonoBehaviour
    {
        /// <summary>
        /// Segments in the pool (about 5 km of mark at 0.3 m segments). 16384 × 4 vertices fill the 16-bit index range.
        /// </summary>
        public const int Capacity = 16384;
        const float WorldBoundsSize = 100000f;

        [StructLayout(LayoutKind.Sequential)]
        struct Vertex
        {
            public Vector3 position;
            public Color32 color;
            public Vector2 uv;
        }

        [AutoStaticsCleanup] static SkidMarks instance;

        Mesh mesh;
        Vertex[] vertices;
        int head;
        int dirtyMin = int.MaxValue, dirtyMax = -1;
        bool linear;

        /// <summary>The scene's mark pool, created on first use with <paramref name="material"/>.</summary>
        public static SkidMarks Get(Material material)
        {
            if (instance != null) return instance;
            if (material == null) return null;
            var go = new GameObject("SkidMarks");
            instance = go.AddComponent<SkidMarks>();
            instance.Init(material);
            return instance;
        }

        void Init(Material material)
        {
            linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            vertices = new Vertex[Capacity * 4];
            mesh = new Mesh { name = "SkidMarks" };
            mesh.MarkDynamic();
            mesh.SetVertexBufferParams(vertices.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
            const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
            mesh.SetVertexBufferData(vertices, 0, 0, vertices.Length, 0, flags);
            var indices = new ushort[Capacity * 6];
            for (var s = 0; s < Capacity; s++)
            {
                var v = (ushort)(s * 4);
                var i = s * 6;
                indices[i] = v; indices[i + 1] = (ushort)(v + 2); indices[i + 2] = (ushort)(v + 1);
                indices[i + 3] = (ushort)(v + 1); indices[i + 4] = (ushort)(v + 2); indices[i + 5] = (ushort)(v + 3);
            }
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length, flags);
            var bounds = new Bounds(Vector3.zero, Vector3.one * WorldBoundsSize);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length) { bounds = bounds, vertexCount = vertices.Length }, flags);
            mesh.bounds = bounds;
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>Adds one quad from the edge (l0, r0) to (l1, r1) with start / end colours (rgb + opacity).</summary>
        public void Add(Vector3 l0, Vector3 r0, Vector3 l1, Vector3 r1, Color c0, Color c1)
        {
            var s = head;
            head = (head + 1) % Capacity;
            var v = s * 4;
            var a = ToVertexColor(c0);
            var b = ToVertexColor(c1);
            Set(v, l0, a, 0f);
            Set(v + 1, r0, a, 1f);
            Set(v + 2, l1, b, 0f);
            Set(v + 3, r1, b, 1f);
            MarkDirty(v, v + 3);
        }

        Color32 ToVertexColor(Color c)
        {
            if (linear) c = new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);
            return c;
        }

        void Set(int i, Vector3 p, Color32 c, float u)
        {
            vertices[i] = new Vertex { position = p, color = c, uv = new Vector2(u, 0.5f) };
        }

        void MarkDirty(int from, int to)
        {
            if (from < dirtyMin) dirtyMin = from;
            if (to > dirtyMax) dirtyMax = to;
        }

        void LateUpdate()
        {
            if (dirtyMax < 0) return;
            mesh.SetVertexBufferData(vertices, dirtyMin, dirtyMin, dirtyMax - dirtyMin + 1, 0,
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers);
            dirtyMin = int.MaxValue;
            dirtyMax = -1;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            if (mesh != null) Destroy(mesh);
        }
    }
}
