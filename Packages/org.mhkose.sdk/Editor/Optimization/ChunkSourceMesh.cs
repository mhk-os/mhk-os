using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor.Optimization
{
    /// <summary>
    /// A source mesh read once (Read/Write not required): raw vertex streams copied byte for byte into chunks, the
    /// triangles of all submeshes in submesh order and their world-space XZ centroids.
    /// </summary>
    internal sealed class ChunkSourceMesh
    {
        VertexAttributeDescriptor[] attributes;
        byte[][] streams;
        int[] strides;
        int[] indices;
        int[] triangleSubMesh;
        public int triangleCount, usedVertices;
        public float[] centroidX, centroidZ;
        public float minX, maxX, minZ, maxZ;
        // Per-vertex scratch: visit stamp (vertex counting) and chunk index (building).
        int[] stamp, remap;
        int generation;

        /// <summary>Reads <paramref name="mesh"/> under <paramref name="localToWorld"/>, null with <paramref name="error"/> when it cannot be chunked.</summary>
        public static ChunkSourceMesh Read(Mesh mesh, Matrix4x4 localToWorld, out string error)
        {
            using var array = MeshUtility.AcquireReadOnlyMeshData(mesh);
            var data = array[0];
            var s = new ChunkSourceMesh { attributes = mesh.GetVertexAttributes() };
            error = s.ReadIndices(data, mesh.name);
            if (error != null) return null;
            s.ComputeCentroidsAndBounds(WorldPositions(data, localToWorld));
            if (s.triangleCount == 0)
            {
                error = $"mesh '{mesh.name}' has no triangles";
                return null;
            }
            s.CopyVertexStreams(data);
            s.stamp = new int[data.vertexCount];
            s.remap = new int[data.vertexCount];
            return s;
        }

        string ReadIndices(Mesh.MeshData data, string meshName)
        {
            var triangles = 0;
            for (var i = 0; i < data.subMeshCount; i++)
            {
                var sub = data.GetSubMesh(i);
                if (sub.topology != MeshTopology.Triangles) return $"submesh {i} of '{meshName}' has {sub.topology} topology";
                triangles += sub.indexCount / 3;
            }

            indices = new int[triangles * 3];
            triangleSubMesh = new int[triangles];
            var at = 0;
            for (var i = 0; i < data.subMeshCount; i++)
            {
                var sub = data.GetSubMesh(i);
                var count = sub.indexCount / 3 * 3;
                CopyIndices(data, sub, at, count);
                for (var k = 0; k < count / 3; k++) triangleSubMesh[at / 3 + k] = i;
                at += count;
            }
            triangleCount = triangles;
            return null;
        }

        void CopyIndices(Mesh.MeshData data, SubMeshDescriptor sub, int at, int count)
        {
            if (data.indexFormat == IndexFormat.UInt16)
            {
                var src = data.GetIndexData<ushort>();
                for (var k = 0; k < count; k++) indices[at + k] = src[sub.indexStart + k] + sub.baseVertex;
            }
            else
            {
                var src = data.GetIndexData<uint>();
                for (var k = 0; k < count; k++) indices[at + k] = (int)src[sub.indexStart + k] + sub.baseVertex;
            }
        }

        static Vector3[] WorldPositions(Mesh.MeshData data, Matrix4x4 localToWorld)
        {
            var world = new Vector3[data.vertexCount];
            using var positions = new NativeArray<Vector3>(data.vertexCount, Allocator.TempJob);
            data.GetVertices(positions);
            for (var v = 0; v < world.Length; v++) world[v] = localToWorld.MultiplyPoint3x4(positions[v]);
            return world;
        }

        void ComputeCentroidsAndBounds(Vector3[] world)
        {
            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            var used = new bool[world.Length];
            centroidX = new float[triangleCount];
            centroidZ = new float[triangleCount];
            for (var t = 0; t < triangleCount; t++)
            {
                float cx = 0f, cz = 0f;
                for (var k = 0; k < 3; k++)
                {
                    var v = indices[t * 3 + k];
                    var p = world[v];
                    cx += p.x;
                    cz += p.z;
                    if (used[v]) continue;
                    used[v] = true;
                    usedVertices++;
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                }
                centroidX[t] = cx / 3f;
                centroidZ[t] = cz / 3f;
            }
        }

        void CopyVertexStreams(Mesh.MeshData data)
        {
            streams = new byte[data.vertexBufferCount][];
            strides = new int[data.vertexBufferCount];
            for (var i = 0; i < data.vertexBufferCount; i++)
            {
                strides[i] = data.GetVertexBufferStride(i);
                streams[i] = strides[i] > 0 ? data.GetVertexData<byte>(i).ToArray() : Array.Empty<byte>();
            }
        }

        /// <summary>Distinct vertices used by <paramref name="tris"/>.</summary>
        public int CountVertices(List<int> tris)
        {
            generation++;
            var count = 0;
            foreach (var t in tris)
                for (var k = 0; k < 3; k++)
                {
                    var v = indices[t * 3 + k];
                    if (stamp[v] == generation) continue;
                    stamp[v] = generation;
                    count++;
                }
            return count;
        }

        /// <summary>Splits <paramref name="tris"/> in order into runs using at most <paramref name="maxVertices"/> vertices each.</summary>
        public List<List<int>> Pack(List<int> tris, int maxVertices)
        {
            var runs = new List<List<int>> { new() };
            var count = 0;
            generation++;
            foreach (var t in tris)
            {
                int a = indices[t * 3], b = indices[t * 3 + 1], c = indices[t * 3 + 2];
                var added = (stamp[a] != generation ? 1 : 0)
                            + (stamp[b] != generation && b != a ? 1 : 0)
                            + (stamp[c] != generation && c != a && c != b ? 1 : 0);
                if (count + added > maxVertices)
                {
                    runs.Add(new List<int>());
                    count = 0;
                    generation++;
                    added = 1 + (b != a ? 1 : 0) + (c != a && c != b ? 1 : 0);
                }
                runs[runs.Count - 1].Add(t);
                stamp[a] = stamp[b] = stamp[c] = generation;
                count += added;
            }
            return runs;
        }

        /// <summary>
        /// Mesh of <paramref name="tris"/> (ascending) with only the vertices they use and one UInt16 submesh per source
        /// submesh that occurs, listed in <paramref name="subMeshes"/>. Throws when a chunk cannot be built.
        /// </summary>
        public Mesh Build(List<int> tris, string name, out List<int> subMeshes)
        {
            var chunkIndices = RemapTriangles(tris, out var used, out subMeshes, out var subMeshStarts);
            if (used.Count > MeshChunker.MaxChunkVertices) throw new InvalidOperationException($"chunk {name} has {used.Count} vertices");

            var array = Mesh.AllocateWritableMeshData(1);
            try
            {
                WriteMeshData(array[0], used, chunkIndices, subMeshes, subMeshStarts);
            }
            catch
            {
                array.Dispose();
                throw;
            }
            var mesh = new Mesh { name = name };
            Mesh.ApplyAndDisposeWritableMeshData(array, mesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        ushort[] RemapTriangles(List<int> tris, out List<int> used, out List<int> subMeshes, out List<int> subMeshStarts)
        {
            generation++;
            used = new List<int>();
            var chunkIndices = new ushort[tris.Count * 3];
            subMeshes = new List<int>();
            subMeshStarts = new List<int>();
            for (var i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                var sub = triangleSubMesh[t];
                if (subMeshes.Count == 0 || subMeshes[subMeshes.Count - 1] != sub)
                {
                    subMeshes.Add(sub);
                    subMeshStarts.Add(i * 3);
                }
                for (var k = 0; k < 3; k++)
                {
                    var v = indices[t * 3 + k];
                    if (stamp[v] != generation)
                    {
                        stamp[v] = generation;
                        remap[v] = used.Count;
                        used.Add(v);
                    }
                    chunkIndices[i * 3 + k] = (ushort)remap[v];
                }
            }
            return chunkIndices;
        }

        void WriteMeshData(Mesh.MeshData data, List<int> used, ushort[] chunkIndices, List<int> subMeshes, List<int> subMeshStarts)
        {
            data.SetVertexBufferParams(used.Count, attributes);
            for (var s = 0; s < streams.Length; s++) CopyStream(data, s, used);
            data.SetIndexBufferParams(chunkIndices.Length, IndexFormat.UInt16);
            data.GetIndexData<ushort>().CopyFrom(chunkIndices);
            data.subMeshCount = subMeshes.Count;
            for (var i = 0; i < subMeshes.Count; i++)
            {
                var end = i + 1 < subMeshes.Count ? subMeshStarts[i + 1] : chunkIndices.Length;
                data.SetSubMesh(i, new SubMeshDescriptor(subMeshStarts[i], end - subMeshStarts[i]));
            }
        }

        void CopyStream(Mesh.MeshData data, int s, List<int> used)
        {
            var stride = strides[s];
            if (stride == 0) return;
            if (data.GetVertexBufferStride(s) != stride)
                throw new InvalidOperationException($"vertex stream {s} layout differs ({data.GetVertexBufferStride(s)} vs {stride} bytes)");
            var target = data.GetVertexData<byte>(s);
            for (var i = 0; i < used.Count; i++) NativeArray<byte>.Copy(streams[s], used[i] * stride, target, i * stride, stride);
        }
    }
}
