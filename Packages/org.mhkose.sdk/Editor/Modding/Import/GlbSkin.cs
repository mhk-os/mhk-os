using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Skins of one import: skinned renderers bound to the joint nodes (joints outside the scene bind to the root),
    /// per-vertex weights merged per joint, sorted and normalised, and the inverse bind matrices.
    /// </summary>
    static class GlbSkin
    {
        /// <summary>A skinned renderer on <paramref name="go"/> drawing <paramref name="mesh"/> with the joints of skin <paramref name="skinIndex"/>.</summary>
        public static SkinnedMeshRenderer CreateRenderer(GlbImport g, GameObject go, Mesh mesh, int skinIndex)
        {
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            var skin = (JObject)g.json["skins"][skinIndex];
            var bones = ((JArray)skin["joints"]).Select(j => Joint(g, (int)j, skinIndex)).ToArray();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            var skeleton = GlbReader.I(skin["skeleton"]);
            if (skeleton >= 0 && skeleton < g.nodes.Length && g.nodes[skeleton] != null) smr.rootBone = g.nodes[skeleton];
            else
            {
                var ancestor = CommonAncestor(bones, g.root.transform);
                smr.rootBone = ancestor != null ? ancestor : bones.FirstOrDefault();
            }
            smr.updateWhenOffscreen = true;
            return smr;
        }

        static Transform Joint(GlbImport g, int node, int skinIndex)
        {
            var bone = node >= 0 && node < g.nodes.Length ? g.nodes[node] : null;
            if (bone != null) return bone;
            g.WarnOnce("joint" + node, $"skin {skinIndex} joint node {node} is not part of the scene; bound to the root");
            return g.root.transform;
        }

        static Transform CommonAncestor(Transform[] bones, Transform root)
        {
            Transform best = null;
            foreach (var b in bones)
            {
                if (b == null || b == root) continue;
                if (best == null)
                {
                    best = b;
                    continue;
                }
                while (best != null && best != root && !b.IsChildOf(best)) best = best.parent;
            }
            return best == root ? null : best;
        }

        /// <summary>
        /// Sets up to four weights per vertex: slots sharing a joint are merged, joints outside the skin dropped, the
        /// rest sorted by weight and normalised. Vertices without weight bind fully to bone 0.
        /// </summary>
        public static void SetBoneWeights(GlbImport g, Mesh mesh, List<int> joints, List<float> weights, int boneCount, int meshIndex)
        {
            var vertexCount = joints.Count / 4;
            var perVertex = new NativeArray<byte>(vertexCount, Allocator.Temp);
            try
            {
                var all = new List<BoneWeight1>(vertexCount * 2);
                var tmp = new List<BoneWeight1>(4);
                for (var v = 0; v < vertexCount; v++)
                {
                    var sum = CollectWeights(g, tmp, joints, weights, v, boneCount, meshIndex);
                    tmp.Sort((a, b) => b.weight.CompareTo(a.weight));
                    foreach (var b in tmp) all.Add(new BoneWeight1 { boneIndex = b.boneIndex, weight = b.weight / sum });
                    perVertex[v] = (byte)tmp.Count;
                }
                using var weightArray = new NativeArray<BoneWeight1>(all.ToArray(), Allocator.Temp);
                mesh.SetBoneWeights(perVertex, weightArray);
            }
            finally
            {
                perVertex.Dispose();
            }
        }

        static float CollectWeights(GlbImport g, List<BoneWeight1> tmp, List<int> joints, List<float> weights, int v, int boneCount, int meshIndex)
        {
            tmp.Clear();
            var sum = 0f;
            for (var k = 0; k < 4; k++)
            {
                var w = weights[v * 4 + k];
                var j = joints[v * 4 + k];
                if (w <= 0f) continue;
                if (j < 0 || j >= boneCount)
                {
                    g.WarnOnce("jointRange" + meshIndex, $"mesh {meshIndex} references joint {j} outside its skin; weight dropped");
                    continue;
                }
                var existing = tmp.FindIndex(b => b.boneIndex == j);
                if (existing >= 0) tmp[existing] = new BoneWeight1 { boneIndex = j, weight = tmp[existing].weight + w };
                else tmp.Add(new BoneWeight1 { boneIndex = j, weight = w });
                sum += w;
            }
            if (tmp.Count > 0 && sum > 0f) return sum;
            tmp.Clear();
            tmp.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
            return 1f;
        }

        /// <summary>Inverse bind matrices of skin <paramref name="skinIndex"/> in Unity space (identity when missing).</summary>
        public static Matrix4x4[] Bindposes(GlbImport g, int skinIndex)
        {
            var skin = (JObject)g.json["skins"][skinIndex];
            var count = ((JArray)skin["joints"]).Count;
            var result = new Matrix4x4[count];
            var ibm = GlbReader.I(skin["inverseBindMatrices"]);
            if (ibm < 0)
            {
                for (var i = 0; i < count; i++) result[i] = Matrix4x4.identity;
                return result;
            }
            var m = g.doc.ReadFloats(ibm, out var c);
            if (c != GltfConstants.Mat4Components || m.Length < count * GltfConstants.Mat4Components)
                throw new InvalidDataException($"{g.doc.path}: skin {skinIndex} inverseBindMatrices do not match its joints");
            for (var i = 0; i < count; i++) result[i] = GlbSpace.Matrix(m, i * GltfConstants.Mat4Components);
            return result;
        }
    }
}
