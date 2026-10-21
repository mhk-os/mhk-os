using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MHK.Editor.Optimization
{
    /// <summary>Outcome of <see cref="MeshChunker.ChunkScene"/> for one scene.</summary>
    public sealed class MeshChunkReport
    {
        /// <summary>One chunked renderer.</summary>
        public sealed class Entry
        {
            /// <summary>Hierarchy path of the chunked object below the Visual group.</summary>
            public string sourceName;
            public string meshName;
            /// <summary>The source mesh asset (destroyed when <see cref="sourceMeshRemoved"/>).</summary>
            public Mesh sourceMesh;
            public string libraryPath;
            public int vertices, triangles;
            /// <summary>Source vertices referenced by at least one triangle.</summary>
            public int usedVertices;
            /// <summary>Larger horizontal world-space extent (m), the quadtree root's side.</summary>
            public float extent;
            public int leafCount;
            public float minLeafSize, maxLeafSize;
            public int chunkCount, chunkVertices, chunkTriangles;
            public int minChunkTriangles, maxChunkTriangles;
            /// <summary>Components left on the original object (now the chunks' parent).</summary>
            public List<string> keptComponents = new();
            public bool sourceMeshRemoved;
            public string sourceMeshNote = "kept: the scene has not been saved and checked yet";
        }

        public string scenePath;
        public readonly List<Entry> chunked = new();
        /// <summary>Candidates and near-candidates left unchanged, as "path: reason".</summary>
        public readonly List<string> skipped = new();
        /// <summary>Renderers ignored without a report line (too small and too few triangles).</summary>
        public int ignored;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[Optimize] {scenePath}: chunked {chunked.Count} meshes, skipped {skipped.Count}, ignored {ignored} small meshes.");
            foreach (var e in chunked) AppendEntry(sb, e);
            foreach (var s in skipped) sb.Append("\n  skipped " + s);
            return sb.ToString();
        }

        static void AppendEntry(StringBuilder sb, Entry e)
        {
            sb.Append($"\n  {e.sourceName} (mesh '{e.meshName}', {e.libraryPath}): {e.vertices} vertices ({e.usedVertices} used), " +
                      $"{e.triangles} triangles, extent {e.extent:0.#} m -> {e.leafCount} leaves of {e.minLeafSize:0.#}-{e.maxLeafSize:0.#} m, " +
                      $"{e.chunkCount} chunks, {e.chunkVertices} vertices, {e.chunkTriangles} triangles " +
                      $"(per chunk {e.minChunkTriangles}/{e.chunkTriangles / Mathf.Max(1, e.chunkCount)}/{e.maxChunkTriangles} min/avg/max); " +
                      $"source mesh {(e.sourceMeshRemoved ? "removed" : "kept")} ({e.sourceMeshNote})");
            if (e.keptComponents.Count > 0) sb.Append("; components kept on the parent: " + string.Join(", ", e.keptComponents));
        }
    }
}
