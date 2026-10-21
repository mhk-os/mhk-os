using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Powerups
{
    /// <summary>One ground capture of a <see cref="MolehillMound"/> and the material showing it (null capture: fallback mapping).</summary>
    sealed class MoundLook
    {
        public GroundCapture capture;
        public Material material;
        public readonly List<int> tris = new();

        public void Release()
        {
            if (material != null) Object.Destroy(material);
            capture?.Release();
        }
    }

    /// <summary>A cross-section of the mound being drawn this frame, front to back.</summary>
    struct MoundRow
    {
        public Vector3 normal;
        public float behind, lump;
        public int groundStart;
        public MoundLook look;
    }

    /// <summary>Turns the mound's surface grid into a mesh with one submesh and material per ground capture.</summary>
    sealed class MoundMesh
    {
        readonly List<Vector3> verts = new();
        readonly List<Vector3> normals = new();
        readonly List<Vector2> uvs = new();
        Vector3[] grid = Array.Empty<Vector3>();
        Material[] materials = Array.Empty<Material>();
        List<MoundRow> rows;
        List<Vector3> rowGround;
        int across;
        float fallbackUvPerMetre;

        public Mesh Mesh { get; }

        public MoundMesh(string name)
        {
            Mesh = new Mesh { name = name };
            Mesh.MarkDynamic();
        }

        public void Destroy() => Object.Destroy(Mesh);

        /// <summary>Surface points (row-major, <paramref name="count"/> or more) the caller fills before <see cref="Build"/>.</summary>
        public Vector3[] Grid(int count)
        {
            if (grid.Length < count) grid = new Vector3[count];
            return grid;
        }

        /// <summary>Rebuilds the mesh from the grid of <paramref name="liveRows"/> × <paramref name="vertsAcross"/> points.</summary>
        public void Build(List<MoundRow> liveRows, List<Vector3> ground, int vertsAcross, IReadOnlyList<MoundLook> looks, float uvPerMetre)
        {
            rows = liveRows;
            rowGround = ground;
            across = vertsAcross;
            fallbackUvPerMetre = uvPerMetre;
            verts.Clear();
            normals.Clear();
            uvs.Clear();
            foreach (var look in looks) look.tris.Clear();
            EmitRows();
            Upload(looks);
        }

        /// <summary>Assigns one material per look (or <paramref name="fallback"/>) when they changed.</summary>
        public void SyncMaterials(MeshRenderer meshRenderer, IReadOnlyList<MoundLook> looks, Material fallback)
        {
            var changed = false;
            if (materials.Length != looks.Count)
            {
                materials = new Material[looks.Count];
                changed = true;
            }
            for (var i = 0; i < looks.Count; i++)
            {
                var m = looks[i].material != null ? looks[i].material : fallback;
                changed |= materials[i] != m;
                materials[i] = m;
            }
            if (changed) meshRenderer.sharedMaterials = materials;
        }

        // Each quad uses the capture of the row nearer the head, so where the ridge passes from one capture to the
        // next the row behind is emitted twice (once per capture).
        void EmitRows()
        {
            var rowLook = rows[0].look;
            var row = EmitRow(0, rowLook);
            for (var si = 0; si + 1 < rows.Count; si++)
            {
                var next = EmitRow(si + 1, rowLook);
                AddQuads(rowLook.tris, row, next);
                row = next;
                var nextLook = rows[si + 1].look;
                if (nextLook == rowLook) continue;
                rowLook = nextLook;
                row = EmitRow(si + 1, rowLook);
            }
        }

        // Row `ahead` is in front of row `behind`, right = increasing vertex index.
        void AddQuads(List<int> quads, int ahead, int behind)
        {
            for (var v = 0; v + 1 < across; v++)
            {
                int a = ahead + v, b = a + 1, c = behind + v, d = c + 1;
                quads.Add(a); quads.Add(b); quads.Add(c);
                quads.Add(b); quads.Add(d); quads.Add(c);
            }
        }

        int EmitRow(int si, MoundLook look)
        {
            var start = verts.Count;
            var up = rows[si].normal;
            var capture = look?.capture;
            for (var v = 0; v < across; v++)
            {
                var ground = rowGround[rows[si].groundStart + v];
                verts.Add(grid[si * across + v]);
                normals.Add(GridNormal(si, v, up));
                uvs.Add(capture != null ? capture.UvAt(ground) : new Vector2(ground.x, ground.z) * fallbackUvPerMetre);
            }
            return start;
        }

        Vector3 GridNormal(int s, int v, Vector3 up)
        {
            var n = across;
            var acrossDelta = grid[s * n + Mathf.Min(v + 1, n - 1)] - grid[s * n + Mathf.Max(v - 1, 0)];
            var along = grid[Mathf.Max(s - 1, 0) * n + v] - grid[Mathf.Min(s + 1, rows.Count - 1) * n + v];
            var normal = Vector3.Cross(acrossDelta, along);
            if (normal.sqrMagnitude < 1e-10f) return up;
            normal.Normalize();
            return Vector3.Dot(normal, up) < 0f ? -normal : normal;
        }

        void Upload(IReadOnlyList<MoundLook> looks)
        {
            Mesh.Clear();
            Mesh.SetVertices(verts);
            Mesh.SetNormals(normals);
            Mesh.SetUVs(0, uvs);
            Mesh.subMeshCount = looks.Count;
            for (var i = 0; i < looks.Count; i++) Mesh.SetTriangles(looks[i].tris, i, false);
            Mesh.RecalculateBounds();
        }
    }
}
