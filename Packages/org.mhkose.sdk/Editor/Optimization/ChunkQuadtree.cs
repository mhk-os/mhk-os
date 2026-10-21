using System.Collections.Generic;
using System.Linq;

namespace MHK.Editor.Optimization
{
    /// <summary>Adaptive quadtree over a source mesh's world XZ bounds that groups its triangles into chunks.</summary>
    internal static class ChunkQuadtree
    {
        const int MaxQuadtreePathLength = 24;

        // Path: "r" for the root, one quadrant digit per level, "_p<n>" for packed parts. Size: leaf side (m).
        internal sealed class Piece
        {
            public string path;
            public float size;
            public List<int> triangles;
        }

        /// <summary>Leaves of the quadtree over every triangle of <paramref name="source"/>, root side <paramref name="extent"/>.</summary>
        public static List<Piece> Split(ChunkSourceMesh source, float extent)
        {
            var pieces = new List<Piece>();
            Split(source, Enumerable.Range(0, source.triangleCount).ToList(), source.minX, source.minZ, extent, "r", pieces);
            return pieces;
        }

        static void Split(ChunkSourceMesh source, List<int> tris, float x0, float z0, float size, string path, List<Piece> pieces)
        {
            var split = (tris.Count > MeshChunker.TargetTriangles && size > MeshChunker.MinCellSize)
                        || source.CountVertices(tris) > MeshChunker.MaxChunkVertices;
            if (!split)
            {
                pieces.Add(new Piece { path = path, size = size, triangles = tris });
                return;
            }
            if (path.Length > MaxQuadtreePathLength)
            {
                var part = 0;
                foreach (var packed in source.Pack(tris, MeshChunker.MaxChunkVertices))
                    pieces.Add(new Piece { path = path + "_p" + part++, size = size, triangles = packed });
                return;
            }
            // Quadrant digit: bit 0 = +x half, bit 1 = +z half.
            var half = size * 0.5f;
            float mx = x0 + half, mz = z0 + half;
            var quadrants = new List<int>[4];
            foreach (var t in tris)
                (quadrants[(source.centroidX[t] >= mx ? 1 : 0) + (source.centroidZ[t] >= mz ? 2 : 0)] ??= new List<int>()).Add(t);
            for (var q = 0; q < 4; q++)
                if (quadrants[q] != null)
                    Split(source, quadrants[q], (q & 1) == 0 ? x0 : mx, (q & 2) == 0 ? z0 : mz, half, path + q, pieces);
        }
    }
}
