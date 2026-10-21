using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Race;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Makes track collision meshes double-sided by adding a reversed copy of every one-sided triangle. Idempotent.
    /// Used by the export importer and the lighting baker.
    /// </summary>
    public static class CollisionWinding
    {
        /// <summary>Makes the collision meshes of <paramref name="scene"/> double-sided, returns the number of faces added.</summary>
        public static int FixScene(Scene scene)
        {
            var added = 0;
            var done = new HashSet<Mesh>();
            foreach (var runtime in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<TrackRuntime>(true)))
            {
                var root = runtime.transform.Find(TrackRuntime.CollisionGroup);
                if (root == null) continue;
                foreach (var mc in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    var mesh = mc.sharedMesh;
                    if (mesh == null || !done.Add(mesh)) continue;
                    var n = MakeDoubleSided(mesh);
                    if (n == 0) continue;
                    EditorUtility.SetDirty(mesh);
                    added += n;
                }
            }
            return added;
        }

        /// <summary>Adds a reversed copy of every triangle of <paramref name="mesh"/> that has none, returns how many.</summary>
        public static int MakeDoubleSided(Mesh mesh)
        {
            var added = 0;
            var subs = new List<int[]>();
            using (var readback = MeshReadback.Of(mesh))
                for (var s = 0; s < readback.SubMeshCount; s++)
                    subs.Add(WithBackFaces(TrianglesOf(readback, s), ref added));
            if (added == 0) return 0;
            using (MeshWriteScope.Open(mesh))
            {
                if (mesh.indexFormat == IndexFormat.UInt16 && mesh.vertexCount > ushort.MaxValue)
                    mesh.indexFormat = IndexFormat.UInt32;
                for (var s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s, false);
            }
            return added;
        }

        static int[] TrianglesOf(MeshReadback readback, int subMesh) =>
            readback.SubMesh(subMesh).topology == MeshTopology.Triangles ? readback.Indices(subMesh) : Array.Empty<int>();

        static int[] WithBackFaces(int[] tris, ref int added)
        {
            // Triangles keyed by their vertex cycle, rotated to start at the smallest index.
            var present = new HashSet<(int, int, int)>();
            for (var i = 0; i < tris.Length; i += 3) present.Add(Key(tris[i], tris[i + 1], tris[i + 2]));
            var result = new List<int>(tris.Length * 2);
            result.AddRange(tris);
            for (var i = 0; i < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                if (a == b || b == c || a == c) continue;
                if (!present.Add(Key(a, c, b))) continue;
                result.Add(a);
                result.Add(c);
                result.Add(b);
                added++;
            }
            return result.ToArray();
        }

        static (int, int, int) Key(int a, int b, int c)
        {
            if (a <= b && a <= c) return (a, b, c);
            return b <= c ? (b, c, a) : (c, a, b);
        }
    }
}
