using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>A built mesh with one material per submesh.</summary>
    sealed class GlbMeshData
    {
        public Mesh mesh;
        public Material[] materials;
        public bool hasJoints;
    }

    /// <summary>
    /// Meshes of one import: one per glTF mesh and skin, one submesh per triangle primitive, named
    /// "prefix_meshName" (made unique with "_index" and "_skinN") and stored in the library.
    /// </summary>
    sealed class GlbMeshBuilder
    {
        readonly GlbImport g;
        readonly Dictionary<(int mesh, int skin), GlbMeshData> cache = new();
        readonly HashSet<string> names = new();

        public GlbMeshBuilder(GlbImport import) => g = import;

        /// <summary>The mesh of glTF mesh <paramref name="meshIndex"/> as used with <paramref name="skinIndex"/>, null without triangles.</summary>
        public GlbMeshData Get(int meshIndex, int skinIndex)
        {
            if (g.json["meshes"] is not JArray meshes || meshIndex < 0 || meshIndex >= meshes.Count)
                throw new InvalidDataException($"{g.doc.path}: mesh {meshIndex} does not exist");
            if (cache.TryGetValue((meshIndex, skinIndex), out var cached)) return cached;
            var m = (JObject)meshes[meshIndex];
            var data = Build(m, meshIndex, skinIndex, UniqueName(m, meshIndex, skinIndex));
            cache[(meshIndex, skinIndex)] = data;
            if (data != null) data.mesh = g.Store(data.mesh);
            return data;
        }

        string UniqueName(JObject m, int meshIndex, int skinIndex)
        {
            var name = $"{g.prefix}_{GlbReader.Str(m["name"]) ?? meshIndex.ToString()}";
            if (names.Add(name)) return name;
            name += skinIndex >= 0 ? $"_{meshIndex}_skin{skinIndex}" : $"_{meshIndex}";
            names.Add(name);
            return name;
        }

        GlbMeshData Build(JObject m, int meshIndex, int skinIndex, string name)
        {
            var prims = TrianglePrimitives(m);
            if (prims.Count == 0)
            {
                g.WarnOnce("empty" + meshIndex, $"mesh {meshIndex} has no triangle primitives and is skipped");
                return null;
            }
            // Primitives sharing one vertex stream (same attribute accessors) keep it once.
            var shared = prims.Count > 1 && prims.All(p => JToken.DeepEquals(p["attributes"], prims[0]["attributes"]));
            var streams = new GlbVertexStreams(g.doc, shared ? new List<JObject> { prims[0] } : prims, skinIndex >= 0, meshIndex);
            var mesh = streams.CreateMesh(name);
            var materials = SetSubmeshes(mesh, prims, streams, shared, meshIndex);
            streams.ApplyNormals(mesh);
            if (streams.hasJoints)
            {
                GlbSkin.SetBoneWeights(g, mesh, streams.joints, streams.weights, ExportJson.Items(g.json["skins"][skinIndex]["joints"]).Count, meshIndex);
                mesh.bindposes = GlbSkin.Bindposes(g, skinIndex);
            }
            mesh.RecalculateBounds();
            return new GlbMeshData { mesh = mesh, materials = materials, hasJoints = streams.hasJoints };
        }

        List<JObject> TrianglePrimitives(JObject m)
        {
            var kept = new List<JObject>();
            foreach (var prim in ((JArray)m["primitives"]).Cast<JObject>())
            {
                var mode = GlbReader.I(prim["mode"], GltfConstants.Triangles);
                if (mode != GltfConstants.Triangles)
                {
                    g.WarnOnce("mode" + mode, $"primitives of mode {mode} (not triangles) are skipped");
                    continue;
                }
                if (prim["attributes"]?["POSITION"] == null)
                {
                    g.WarnOnce("nopos", "primitives without POSITION are skipped");
                    continue;
                }
                if (prim["targets"] != null) g.WarnOnce("morph", "morph targets are not supported and are skipped");
                kept.Add(prim);
            }
            return kept;
        }

        Material[] SetSubmeshes(Mesh mesh, List<JObject> prims, GlbVertexStreams streams, bool shared, int meshIndex)
        {
            mesh.subMeshCount = prims.Count;
            var mats = new Material[prims.Count];
            for (var k = 0; k < prims.Count; k++)
            {
                var offset = shared ? 0 : streams.starts[k];
                var count = (shared ? streams.VertexCount : k + 1 < prims.Count ? streams.starts[k + 1] : streams.VertexCount) - offset;
                mesh.SetTriangles(Triangles(prims[k], offset, count, meshIndex), k, false);
                mats[k] = g.materials.Get(GlbReader.I(prims[k]["material"]), prims[k]["attributes"]["COLOR_0"] != null);
            }
            return mats;
        }

        int[] Triangles(JObject prim, int offset, int streamCount, int meshIndex)
        {
            int[] tris;
            if (prim["indices"] == null || prim["indices"].Type == JTokenType.Null)
            {
                tris = new int[streamCount / 3 * 3];
                for (var i = 0; i + 2 < streamCount; i += 3)
                {
                    tris[i] = offset + i;
                    tris[i + 1] = offset + i + 2;
                    tris[i + 2] = offset + i + 1;
                }
                return tris;
            }
            var idx = g.doc.ReadInts((int)prim["indices"], out _);
            tris = new int[idx.Length / 3 * 3];
            // glTF counter-clockwise to Unity clockwise (the x mirror flips the winding).
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                tris[i] = idx[i] + offset;
                tris[i + 1] = idx[i + 2] + offset;
                tris[i + 2] = idx[i + 1] + offset;
            }
            foreach (var v in idx)
                if (v < 0 || v >= streamCount) throw new InvalidDataException($"{g.doc.path}: mesh {meshIndex} index {v} is out of range ({streamCount} vertices)");
            return tris;
        }
    }

    /// <summary>The vertex attributes of a mesh's primitives, concatenated stream by stream in Unity space.</summary>
    sealed class GlbVertexStreams
    {
        public readonly List<Vector3> positions = new();
        public readonly List<Vector3> normals;
        public readonly List<Vector2> uv0, uv1;
        public readonly List<Color> colors;
        public readonly List<int> joints;
        public readonly List<float> weights;
        public readonly int[] starts;
        public readonly bool hasJoints;

        readonly bool allNormals;
        readonly List<(int start, int count)> normalRanges = new();
        readonly GlbReader doc;
        readonly int meshIndex;

        public int VertexCount => positions.Count;

        public GlbVertexStreams(GlbReader doc, List<JObject> streams, bool skinned, int meshIndex)
        {
            this.doc = doc;
            this.meshIndex = meshIndex;
            var attributes = streams.Select(p => (JObject)p["attributes"]).ToList();
            allNormals = attributes.All(a => a["NORMAL"] != null);
            hasJoints = skinned && attributes.Any(a => a["JOINTS_0"] != null && a["WEIGHTS_0"] != null);
            normals = attributes.Any(a => a["NORMAL"] != null) ? new List<Vector3>() : null;
            uv0 = attributes.Any(a => a["TEXCOORD_0"] != null) ? new List<Vector2>() : null;
            uv1 = attributes.Any(a => a["TEXCOORD_1"] != null) ? new List<Vector2>() : null;
            colors = attributes.Any(a => a["COLOR_0"] != null) ? new List<Color>() : null;
            joints = hasJoints ? new List<int>() : null;
            weights = hasJoints ? new List<float>() : null;
            starts = new int[attributes.Count];
            for (var s = 0; s < attributes.Count; s++)
            {
                starts[s] = positions.Count;
                Read(attributes[s]);
            }
        }

        void Read(JObject a)
        {
            var start = positions.Count;
            var p = doc.ReadFloats((int)a["POSITION"], out var pc);
            if (pc != 3) throw new InvalidDataException($"{doc.path}: mesh {meshIndex} POSITION is not VEC3");
            var count = p.Length / 3;
            for (var i = 0; i < count; i++) positions.Add(GlbSpace.Vec(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
            ReadNormals(a["NORMAL"], start, count);
            ReadUv(uv0, a["TEXCOORD_0"], count);
            ReadUv(uv1, a["TEXCOORD_1"], count);
            ReadColours(a["COLOR_0"], count);
            ReadSkinning(a["JOINTS_0"], a["WEIGHTS_0"], count);
        }

        float[] Read(JToken accessor, int components, int count)
        {
            var v = doc.ReadFloats((int)accessor, out var c);
            if (c != components || v.Length != count * components)
                throw new InvalidDataException($"{doc.path}: mesh {meshIndex} attribute accessor {accessor} does not match its POSITION (count / type)");
            return v;
        }

        void ReadNormals(JToken accessor, int start, int count)
        {
            if (normals == null) return;
            if (accessor == null)
            {
                normals.AddRange(Enumerable.Repeat(Vector3.zero, count));
                return;
            }
            var n = Read(accessor, 3, count);
            for (var i = 0; i < count; i++) normals.Add(GlbSpace.Vec(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]));
            normalRanges.Add((start, count));
        }

        void ReadUv(List<Vector2> list, JToken accessor, int count)
        {
            if (list == null) return;
            if (accessor == null)
            {
                list.AddRange(Enumerable.Repeat(Vector2.zero, count));
                return;
            }
            var uv = Read(accessor, 2, count);
            // glTF v = 0 is the first image row, Unity's uv.y = 1 is the top.
            for (var i = 0; i < count; i++) list.Add(new Vector2(uv[i * 2], 1f - uv[i * 2 + 1]));
        }

        void ReadColours(JToken accessor, int count)
        {
            if (colors == null) return;
            if (accessor == null)
            {
                colors.AddRange(Enumerable.Repeat(Color.white, count));
                return;
            }
            var c = doc.ReadFloats((int)accessor, out var cc);
            if (cc != 3 && cc != 4 || c.Length != count * cc) throw new InvalidDataException($"{doc.path}: mesh {meshIndex} COLOR_0 has a wrong size");
            for (var i = 0; i < count; i++)
                colors.Add(new Color(c[i * cc], c[i * cc + 1], c[i * cc + 2], cc == 4 ? c[i * cc + 3] : 1f));
        }

        void ReadSkinning(JToken jointAccessor, JToken weightAccessor, int count)
        {
            if (joints == null) return;
            if (jointAccessor == null || weightAccessor == null)
            {
                for (var i = 0; i < count; i++)
                {
                    joints.Add(0); joints.Add(0); joints.Add(0); joints.Add(0);
                    weights.Add(1f); weights.Add(0f); weights.Add(0f); weights.Add(0f);
                }
                return;
            }
            var j = doc.ReadInts((int)jointAccessor, out var jc);
            var w = Read(weightAccessor, 4, count);
            if (jc != 4 || j.Length != count * 4) throw new InvalidDataException($"{doc.path}: mesh {meshIndex} JOINTS_0 is not VEC4");
            joints.AddRange(j);
            weights.AddRange(w);
        }

        /// <summary>A new mesh with the positions, UVs and colours (triangles and normals follow).</summary>
        public Mesh CreateMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = MeshIndex.FormatFor(VertexCount) };
            mesh.SetVertices(positions);
            if (uv0 != null) mesh.SetUVs(0, uv0);
            if (uv1 != null) mesh.SetUVs(1, uv1);
            if (colors != null) mesh.SetColors(colors);
            return mesh;
        }

        /// <summary>The provided normals. Streams without normals get computed ones (needs the triangles).</summary>
        public void ApplyNormals(Mesh mesh)
        {
            if (allNormals)
            {
                mesh.SetNormals(normals);
                return;
            }
            mesh.RecalculateNormals();
            if (normals == null || normalRanges.Count == 0) return;
            var computed = mesh.normals;
            foreach (var (start, count) in normalRanges)
                for (var i = 0; i < count; i++) computed[start + i] = normals[start + i];
            mesh.normals = computed;
        }
    }
}
