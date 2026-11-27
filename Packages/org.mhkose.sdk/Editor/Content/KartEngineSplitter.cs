using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Karts;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Gives an imported kart model its own engine part (<see cref="KartModel.engine"/>) when the engine is baked into the
    /// body mesh (the MHK 2, 3, Extra and Thunder karts). The mesh islands of the body whose centre lies in a box are moved
    /// into a new mesh on a child GameObject "Engine", pivoted at the bottom centre of those islands, and the body mesh
    /// keeps the rest. A skinned body (MHK 2) hands its engine to the bone that carries most of it. Variants that only
    /// swap the body's materials get the same materials on the engine. Run it again after re-importing a kart.
    /// </summary>
    public static class KartEngineSplitter
    {
        const string EngineName = "Engine";
        // Vertices closer than this (model units) count as one when islands are found, since seams split the mesh's vertices.
        const float WeldDistance = 1e-4f;

        /// <summary>
        /// Splits the engine out of the body renderer at <paramref name="bodyPath"/>, a path below the root of the kart
        /// prefab at <paramref name="prefabPath"/>. Every island of its mesh whose centre lies in <paramref name="box"/>
        /// (in the space of the prefab root, x right, y up, z forward) moves. <paramref name="variants"/> are prefab variants
        /// that get the body's materials on the engine. Returns a summary, or throws when nothing lies in the box.
        /// </summary>
        public static string Split(string prefabPath, string bodyPath, Bounds box, IEnumerable<string> variants = null)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var body = root.transform.Find(bodyPath) ?? throw new ArgumentException($"{prefabPath}: no '{bodyPath}'.");
                if (body.Find(EngineName) != null) throw new InvalidOperationException($"{prefabPath}: '{bodyPath}' already has an engine part.");
                var model = root.GetComponentInChildren<KartModel>() ?? throw new InvalidOperationException($"{prefabPath}: no KartModel.");
                var skinned = body.GetComponent<SkinnedMeshRenderer>();
                var mesh = skinned != null ? skinned.sharedMesh : body.GetComponent<MeshFilter>().sharedMesh;
                // The model library optimizer stores render-only meshes without a CPU copy. Readable while they change.
                var wasReadable = mesh.isReadable;
                SetReadable(mesh, true);
                string summary;
                try
                {
                    summary = skinned != null ? SplitSkinned(root.transform, skinned, box, model, wasReadable) : SplitRigid(body, box, model, wasReadable);
                }
                finally
                {
                    SetReadable(mesh, wasReadable);
                }
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                foreach (var v in variants ?? Enumerable.Empty<string>()) CopyBodyMaterials(v, bodyPath);
                AssetDatabase.SaveAssets();
                return $"{prefabPath}: {summary}";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Lists the islands of the body mesh at <paramref name="bodyPath"/> in the kart prefab at
        /// <paramref name="prefabPath"/>, rearmost first, with their bounds in the prefab root's space, to choose the
        /// engine's box for <see cref="Split"/>.
        /// </summary>
        public static string Islands(string prefabPath, string bodyPath)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var body = root.transform.Find(bodyPath) ?? throw new ArgumentException($"{prefabPath}: no '{bodyPath}'.");
                var skinned = body.GetComponent<SkinnedMeshRenderer>();
                var mesh = skinned != null ? skinned.sharedMesh : body.GetComponent<MeshFilter>().sharedMesh;
                var wasReadable = mesh.isReadable;
                SetReadable(mesh, true);
                try
                {
                    var positions = KartPositions(root.transform, skinned != null ? skinned : body.GetComponent<Renderer>(), mesh);
                    var island = IslandOf(mesh, mesh.vertices);
                    var islands = new Dictionary<int, (Bounds bounds, int count)>();
                    for (var i = 0; i < positions.Length; i++)
                    {
                        var p = positions[i];
                        if (islands.TryGetValue(island[i], out var acc))
                        {
                            acc.bounds.Encapsulate(p);
                            islands[island[i]] = (acc.bounds, acc.count + 1);
                        }
                        else islands[island[i]] = (new Bounds(p, Vector3.zero), 1);
                    }
                    return string.Join("\n", islands.Values.OrderBy(v => v.bounds.center.z)
                        .Select(v => $"{v.count,5} verts centre {v.bounds.center:F3} size {v.bounds.size:F3}"));
                }
                finally
                {
                    SetReadable(mesh, wasReadable);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static string SplitRigid(Transform body, Bounds box, KartModel model, bool readable)
        {
            var renderer = body.GetComponent<MeshRenderer>();
            var mesh = body.GetComponent<MeshFilter>().sharedMesh;
            var kart = KartPositions(body.root, renderer, mesh);
            var picked = Pick(mesh, box, kart);
            var pivot = body.InverseTransformPoint(body.root.TransformPoint(Bottom(kart, picked.vertices)));
            var engineMesh = Extract(mesh, picked.triangles, v => v - pivot, n => n);
            Remove(mesh, picked.triangles);
            Save(mesh, engineMesh, readable);
            var engine = NewPart(body, pivot, Quaternion.identity);
            engine.gameObject.AddComponent<MeshFilter>().sharedMesh = engineMesh;
            CopyRenderer(renderer, engine.gameObject.AddComponent<MeshRenderer>(), engineMesh.subMeshCount);
            model.engine = engine;
            return $"{picked.islands} islands, {picked.vertices.Count} vertices moved to {engineMesh.name}.";
        }

        // The engine of a skinned body becomes a rigid part of the bone that carries most of its weight, in that bone's
        // space as the mesh's bind pose places it.
        static string SplitSkinned(Transform root, SkinnedMeshRenderer skinned, Bounds box, KartModel model, bool readable)
        {
            var mesh = skinned.sharedMesh;
            var kart = KartPositions(root, skinned, mesh);
            var picked = Pick(mesh, box, kart);
            var weights = mesh.boneWeights;
            var load = new float[skinned.bones.Length];
            foreach (var v in picked.vertices)
            {
                var w = weights[v];
                load[w.boneIndex0] += w.weight0;
                load[w.boneIndex1] += w.weight1;
                load[w.boneIndex2] += w.weight2;
                load[w.boneIndex3] += w.weight3;
            }
            var boneIndex = Array.IndexOf(load, load.Max());
            var bone = skinned.bones[boneIndex];
            var toBone = mesh.bindposes[boneIndex];
            // Pivot at the bottom centre in the kart's space (up is the kart's up), then in the bone's.
            var pivot = bone.InverseTransformPoint(root.TransformPoint(Bottom(kart, picked.vertices)));
            var rotation = Quaternion.FromToRotation(Vector3.up, bone.InverseTransformDirection(root.up));
            var fromBone = Matrix4x4.TRS(pivot, rotation, Vector3.one).inverse * toBone;
            var engineMesh = Extract(mesh, picked.triangles, v => fromBone.MultiplyPoint3x4(v), n => fromBone.MultiplyVector(n).normalized);
            Remove(mesh, picked.triangles);
            Save(mesh, engineMesh, readable);
            var engine = NewPart(bone, pivot, rotation);
            engine.gameObject.AddComponent<MeshFilter>().sharedMesh = engineMesh;
            CopyRenderer(skinned, engine.gameObject.AddComponent<MeshRenderer>(), engineMesh.subMeshCount);
            model.engine = engine;
            return $"{picked.islands} islands, {picked.vertices.Count} vertices moved to {engineMesh.name} on bone {bone.name}.";
        }

        // Every vertex of the body mesh in the space of the kart prefab's root. A skinned body places each vertex with its
        // main bone's bind pose, which the prefab holds.
        static Vector3[] KartPositions(Transform root, Renderer body, Mesh mesh)
        {
            var positions = mesh.vertices;
            var kart = new Vector3[positions.Length];
            if (body is SkinnedMeshRenderer skinned)
            {
                var weights = mesh.boneWeights;
                var binds = mesh.bindposes;
                var bones = skinned.bones;
                for (var i = 0; i < positions.Length; i++)
                {
                    var b = weights[i].boneIndex0;
                    kart[i] = root.InverseTransformPoint(bones[b].localToWorldMatrix.MultiplyPoint3x4(binds[b].MultiplyPoint3x4(positions[i])));
                }
            }
            else
                for (var i = 0; i < positions.Length; i++) kart[i] = root.InverseTransformPoint(body.transform.TransformPoint(positions[i]));
            return kart;
        }

        sealed class Picked
        {
            public readonly List<int>[] triangles;
            public readonly List<int> vertices = new();
            public int islands;

            public Picked(int subMeshes)
            {
                triangles = new List<int>[subMeshes];
                for (var i = 0; i < subMeshes; i++) triangles[i] = new List<int>();
            }
        }

        // One is picked when the centre of its vertices in the kart's space (kart, per vertex) lies in the box.
        static Picked Pick(Mesh mesh, Bounds box, Vector3[] kart)
        {
            var island = IslandOf(mesh, mesh.vertices);
            var sums = new Dictionary<int, (Vector3 sum, int count)>();
            for (var i = 0; i < kart.Length; i++)
            {
                sums.TryGetValue(island[i], out var acc);
                sums[island[i]] = (acc.sum + kart[i], acc.count + 1);
            }
            var inside = new HashSet<int>(sums.Where(p => box.Contains(p.Value.sum / p.Value.count)).Select(p => p.Key));
            if (inside.Count == 0) throw new InvalidOperationException($"{mesh.name}: no island has its centre in {box}.");
            var picked = new Picked(mesh.subMeshCount) { islands = inside.Count };
            var used = new HashSet<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (var t = 0; t < tris.Length; t += 3)
                {
                    if (!inside.Contains(island[tris[t]])) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        picked.triangles[s].Add(tris[t + k]);
                        if (used.Add(tris[t + k])) picked.vertices.Add(tris[t + k]);
                    }
                }
            }
            return picked;
        }

        // The island of every vertex. Triangles joined by shared (welded) vertices form one island.
        static int[] IslandOf(Mesh mesh, Vector3[] positions)
        {
            var welded = Weld(positions);
            var parent = Enumerable.Range(0, positions.Length).ToArray();
            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (var t = 0; t < tris.Length; t += 3)
                {
                    var a = Find(welded[tris[t]]);
                    parent[Find(welded[tris[t + 1]])] = a;
                    parent[Find(welded[tris[t + 2]])] = a;
                }
            }
            var island = new int[positions.Length];
            for (var i = 0; i < positions.Length; i++) island[i] = Find(welded[i]);
            return island;
        }

        static int[] Weld(Vector3[] positions)
        {
            var first = new Dictionary<Vector3Int, int>();
            var welded = new int[positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                var key = Vector3Int.RoundToInt(positions[i] / WeldDistance);
                if (!first.TryGetValue(key, out var w)) first[key] = w = i;
                welded[i] = w;
            }
            return welded;
        }

        // Bottom centre of the picked vertices in the kart's space.
        static Vector3 Bottom(Vector3[] kart, List<int> picked)
        {
            var b = new Bounds(kart[picked[0]], Vector3.zero);
            foreach (var v in picked) b.Encapsulate(kart[v]);
            return new Vector3(b.center.x, b.min.y, b.center.z);
        }

        static Mesh Extract(Mesh source, List<int>[] triangles, Func<Vector3, Vector3> position, Func<Vector3, Vector3> normal)
        {
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var list in triangles)
                foreach (var v in list)
                    if (!map.ContainsKey(v))
                    {
                        map[v] = order.Count;
                        order.Add(v);
                    }
            var mesh = new Mesh { name = source.name + "_" + EngineName };
            var p = source.vertices;
            mesh.SetVertices(order.Select(v => position(p[v])).ToList());
            var n = source.normals;
            if (n.Length > 0) mesh.SetNormals(order.Select(v => normal(n[v])).ToList());
            var c = source.colors;
            if (c.Length > 0) mesh.SetColors(order.Select(v => c[v]).ToList());
            var uvs = new List<Vector4>();
            for (var channel = 0; channel < 8; channel++)
            {
                source.GetUVs(channel, uvs);
                if (uvs.Count == 0) continue;
                mesh.SetUVs(channel, order.Select(v => uvs[v]).ToList());
            }
            mesh.subMeshCount = triangles.Length;
            for (var s = 0; s < triangles.Length; s++) mesh.SetTriangles(triangles[s].Select(v => map[v]).ToList(), s);
            mesh.RecalculateBounds();
            if (source.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.Tangent) > 0) mesh.RecalculateTangents();
            return mesh;
        }

        static void Remove(Mesh mesh, List<int>[] removed)
        {
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (removed[s].Count == 0) continue;
                var gone = new HashSet<(int, int, int)>();
                for (var t = 0; t < removed[s].Count; t += 3) gone.Add((removed[s][t], removed[s][t + 1], removed[s][t + 2]));
                var tris = mesh.GetTriangles(s);
                var kept = new List<int>(tris.Length);
                for (var t = 0; t < tris.Length; t += 3)
                    if (!gone.Contains((tris[t], tris[t + 1], tris[t + 2])))
                        kept.AddRange(new[] { tris[t], tris[t + 1], tris[t + 2] });
                mesh.SetTriangles(kept, s, false);
            }
            mesh.RecalculateBounds();
        }

        // The engine mesh joins the body mesh's asset (the kart's model library), readable like the body was.
        static void Save(Mesh body, Mesh engine, bool readable)
        {
            var path = AssetDatabase.GetAssetPath(body);
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException($"{body.name} is not an asset.");
            AssetDatabase.AddObjectToAsset(engine, path);
            SetReadable(engine, readable);
            EditorUtility.SetDirty(body);
            EditorUtility.SetDirty(engine);
        }

        static Transform NewPart(Transform parent, Vector3 localPosition, Quaternion localRotation)
        {
            var go = new GameObject(EngineName) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPosition, localRotation);
            return go.transform;
        }

        static void CopyRenderer(Renderer from, MeshRenderer to, int subMeshes)
        {
            to.sharedMaterials = from.sharedMaterials.Take(subMeshes).ToArray();
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
        }

        static void CopyBodyMaterials(string variantPath, string bodyPath)
        {
            var root = PrefabUtility.LoadPrefabContents(variantPath);
            try
            {
                var body = root.transform.Find(bodyPath);
                var engine = root.GetComponentInChildren<KartModel>()?.engine;
                if (body == null || engine == null || !body.TryGetComponent<Renderer>(out var from) || !engine.TryGetComponent<MeshRenderer>(out var to))
                    throw new InvalidOperationException($"{variantPath}: no body '{bodyPath}' or engine part.");
                to.sharedMaterials = from.sharedMaterials.Take(to.sharedMaterials.Length).ToArray();
                PrefabUtility.SaveAsPrefabAsset(root, variantPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static void SetReadable(Mesh mesh, bool readable)
        {
            var so = new SerializedObject(mesh);
            var p = so.FindProperty("m_IsReadable");
            if (p == null || p.boolValue == readable) return;
            p.boolValue = readable;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
