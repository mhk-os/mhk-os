using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor
{
    /// <summary>
    /// Read-only snapshot of a mesh's data that works whether or not the mesh is Read/Write enabled
    /// (<see cref="MeshUtility.AcquireReadOnlyMeshData(Mesh)"/>). Dispose after use.
    /// </summary>
    public readonly struct MeshReadback : IDisposable
    {
        readonly Mesh.MeshDataArray array;

        MeshReadback(Mesh mesh) => array = MeshUtility.AcquireReadOnlyMeshData(mesh);

        public static MeshReadback Of(Mesh mesh) => new(mesh);

        Mesh.MeshData Data => array[0];
        public int VertexCount => Data.vertexCount;
        public int SubMeshCount => Data.subMeshCount;
        public bool Has(VertexAttribute attribute) => Data.HasVertexAttribute(attribute);
        public SubMeshDescriptor SubMesh(int index) => Data.GetSubMesh(index);

        public Vector3[] Vertices()
        {
            using var native = new NativeArray<Vector3>(VertexCount, Allocator.Temp);
            Data.GetVertices(native);
            return native.ToArray();
        }

        /// <summary>Vertex normals, empty without a normal attribute.</summary>
        public Vector3[] Normals()
        {
            if (!Has(VertexAttribute.Normal)) return Array.Empty<Vector3>();
            using var native = new NativeArray<Vector3>(VertexCount, Allocator.Temp);
            Data.GetNormals(native);
            return native.ToArray();
        }

        /// <summary>Vertex tangents, empty without a tangent attribute.</summary>
        public Vector4[] Tangents()
        {
            if (!Has(VertexAttribute.Tangent)) return Array.Empty<Vector4>();
            using var native = new NativeArray<Vector4>(VertexCount, Allocator.Temp);
            Data.GetTangents(native);
            return native.ToArray();
        }

        /// <summary>Vertex colours as bytes, empty without a colour attribute.</summary>
        public Color32[] Colors32()
        {
            if (!Has(VertexAttribute.Color)) return Array.Empty<Color32>();
            using var native = new NativeArray<Color32>(VertexCount, Allocator.Temp);
            Data.GetColors(native);
            return native.ToArray();
        }

        /// <summary>Texture coordinates of <paramref name="channel"/> into <paramref name="uvs"/> (cleared, empty without that channel).</summary>
        public void GetUVs(int channel, List<Vector2> uvs)
        {
            uvs.Clear();
            if (!Has(VertexAttribute.TexCoord0 + channel)) return;
            using var native = new NativeArray<Vector2>(VertexCount, Allocator.Temp);
            Data.GetUVs(channel, native);
            uvs.AddRange(native);
        }

        /// <summary>Four-component texture coordinates of <paramref name="channel"/> into <paramref name="uvs"/>.</summary>
        public void GetUVs(int channel, List<Vector4> uvs)
        {
            uvs.Clear();
            if (!Has(VertexAttribute.TexCoord0 + channel)) return;
            using var native = new NativeArray<Vector4>(VertexCount, Allocator.Temp);
            Data.GetUVs(channel, native);
            uvs.AddRange(native);
        }

        /// <summary>Indices of submesh <paramref name="subMesh"/> (base vertex applied unless <paramref name="applyBaseVertex"/> is false).</summary>
        public int[] Indices(int subMesh, bool applyBaseVertex = true)
        {
            using var native = new NativeArray<int>(Data.GetSubMesh(subMesh).indexCount, Allocator.Temp);
            Data.GetIndices(native, subMesh, applyBaseVertex);
            return native.ToArray();
        }

        /// <summary>Triangle indices of every triangle submesh in order, base vertex applied (like <see cref="Mesh.triangles"/>).</summary>
        public int[] Triangles()
        {
            var all = new List<int>();
            for (var s = 0; s < SubMeshCount; s++)
                if (SubMesh(s).topology == MeshTopology.Triangles)
                    all.AddRange(Indices(s));
            return all.ToArray();
        }

        public void Dispose() => array.Dispose();
    }

    /// <summary>Temporarily Read/Write enabled mesh for in-place edits. Restores the flag on dispose.</summary>
    public readonly struct MeshWriteScope : IDisposable
    {
        const string ReadableProperty = "m_IsReadable";
        readonly Mesh mesh;

        MeshWriteScope(Mesh mesh) => this.mesh = mesh;

        /// <summary>Makes <paramref name="mesh"/> readable until the scope ends (no-op for readable meshes).</summary>
        public static MeshWriteScope Open(Mesh mesh)
        {
            if (mesh == null || mesh.isReadable) return default;
            SetReadable(mesh, true);
            return new MeshWriteScope(mesh);
        }

        /// <summary>Sets the serialized Read/Write flag (no public setter exists).</summary>
        public static void SetReadable(Mesh mesh, bool readable)
        {
            using var so = new SerializedObject(mesh);
            so.FindProperty(ReadableProperty).boolValue = readable;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        public void Dispose()
        {
            if (mesh != null && mesh.isReadable) SetReadable(mesh, false);
        }
    }
}
