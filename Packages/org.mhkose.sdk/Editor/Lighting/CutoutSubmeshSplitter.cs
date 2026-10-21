using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor.Lighting
{
    /// <summary>Moves cut-out submeshes of baked renderers into child renderers that keep realtime shadows.</summary>
    internal static class CutoutSubmeshSplitter
    {
        const string CutoutSuffix = "_cutout";
        const string UndoName = "Split cut-out submeshes";
        const int UvChannels = 4;

        readonly struct CutMesh
        {
            public readonly Mesh Mesh;
            public readonly int[] Submeshes;

            public CutMesh(Mesh mesh, int[] submeshes)
            {
                Mesh = mesh;
                Submeshes = submeshes;
            }
        }

        /// <summary>
        /// Moves the cut-out submeshes of static renderers that also have opaque ones into a <c>_cutout</c> child (mesh
        /// stored next to the source in its model library). The source mesh loses those submeshes, other scenes sharing
        /// the library reuse the existing cut-out mesh. Returns the number of renderers split, 0 on a second run.
        /// </summary>
        public static int Split(Transform root)
        {
            var split = 0;
            var cutMeshes = new Dictionary<Mesh, CutMesh>();
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!TrackBakePreparer.NeverMoves(renderer, root)) continue;
                var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                var materials = renderer.sharedMaterials;
                if (!TryGetCutMesh(mesh, materials, cutMeshes, out var entry)) continue;
                CreateCutoutChild(renderer, materials, entry);
                split++;
            }
            return split;
        }

        static bool TryGetCutMesh(Mesh mesh, Material[] materials, Dictionary<Mesh, CutMesh> cutMeshes, out CutMesh entry)
        {
            if (cutMeshes.TryGetValue(mesh, out entry)) return materials.Length == entry.Mesh.subMeshCount + mesh.subMeshCount;
            var cut = Enumerable.Range(0, materials.Length).Where(i => TrackBakePreparer.IsCutout(materials[i])).ToArray();
            if (cut.Length == 0 || cut.Length == materials.Length || !GetOrCreateCutMesh(mesh, cut, materials.Length, out entry)) return false;
            cutMeshes[mesh] = entry;
            return true;
        }

        static bool GetOrCreateCutMesh(Mesh mesh, int[] cut, int materialCount, out CutMesh entry)
        {
            var path = AssetDatabase.GetAssetPath(mesh);
            // Split before by another scene sharing the model library (the mesh lost its cut-out submeshes): reuse that cut-out mesh.
            var existing = string.IsNullOrEmpty(path) ? null
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault(m => m.name == mesh.name + CutoutSuffix);
            if (existing != null && cut.Length == existing.subMeshCount && materialCount == mesh.subMeshCount + existing.subMeshCount)
            {
                entry = new CutMesh(existing, cut);
                return true;
            }
            entry = default;
            if (materialCount != mesh.subMeshCount) return false;
            entry = new CutMesh(ExtractSubmeshes(mesh, cut, mesh.name + CutoutSuffix), cut);
            if (!string.IsNullOrEmpty(path)) AssetDatabase.AddObjectToAsset(entry.Mesh, path);
            if (!mesh.isReadable) MeshWriteScope.SetReadable(entry.Mesh, false);
            using (MeshWriteScope.Open(mesh)) RemoveSubmeshes(mesh, cut);
            EditorUtility.SetDirty(mesh);
            return true;
        }

        static void CreateCutoutChild(MeshRenderer renderer, Material[] materials, CutMesh entry)
        {
            var go = renderer.gameObject;
            var flags = GameObjectUtility.GetStaticEditorFlags(go) & ~StaticEditorFlags.ContributeGI;
            var cutRenderer = RendererSettingsUtil.CreateChildRenderer(go, go.name + CutoutSuffix, entry.Mesh, flags);
            Undo.RegisterCreatedObjectUndo(cutRenderer.gameObject, UndoName);
            RendererSettingsUtil.CopyDrawSettings(renderer, cutRenderer);
            cutRenderer.sharedMaterials = entry.Submeshes.Select(i => materials[i]).ToArray();
            Undo.RecordObject(renderer, UndoName);
            renderer.sharedMaterials = Enumerable.Range(0, materials.Length).Where(i => !entry.Submeshes.Contains(i)).Select(i => materials[i]).ToArray();
        }

        static Mesh ExtractSubmeshes(Mesh source, int[] submeshes, string name)
        {
            using var data = MeshReadback.Of(source);
            var indexLists = RemapIndices(data, submeshes, out var order);
            List<T> Pick<T>(IList<T> all) => order.Select(i => all[i]).ToList();

            var mesh = new Mesh { name = name, indexFormat = order.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(Pick(data.Vertices()));
            if (data.Has(VertexAttribute.Normal)) mesh.SetNormals(Pick(data.Normals()));
            if (data.Has(VertexAttribute.Tangent)) mesh.SetTangents(Pick(data.Tangents()));
            if (data.Has(VertexAttribute.Color)) mesh.SetColors(Pick(data.Colors32()));
            for (var channel = 0; channel < UvChannels; channel++)
            {
                if (!data.Has(VertexAttribute.TexCoord0 + channel)) continue;
                var uvs = new List<Vector4>();
                data.GetUVs(channel, uvs);
                mesh.SetUVs(channel, Pick(uvs));
            }
            mesh.subMeshCount = submeshes.Length;
            for (var i = 0; i < submeshes.Length; i++)
                mesh.SetIndices(indexLists[i], source.GetTopology(submeshes[i]), i);
            mesh.RecalculateBounds();
            return mesh;
        }

        // New vertex numbers in first-use order. order[new] is the source vertex.
        static List<List<int>> RemapIndices(MeshReadback data, int[] submeshes, out int[] order)
        {
            var remap = new Dictionary<int, int>();
            var indexLists = new List<List<int>>();
            foreach (var s in submeshes)
            {
                var list = new List<int>();
                foreach (var i in data.Indices(s))
                {
                    if (!remap.TryGetValue(i, out var n)) remap[i] = n = remap.Count;
                    list.Add(n);
                }
                indexLists.Add(list);
            }
            order = new int[remap.Count];
            foreach (var (from, to) in remap) order[to] = from;
            return indexLists;
        }

        static void RemoveSubmeshes(Mesh mesh, int[] submeshes)
        {
            var keep = Enumerable.Range(0, mesh.subMeshCount).Where(s => !submeshes.Contains(s))
                .Select(s => (indices: mesh.GetIndices(s), topology: mesh.GetTopology(s))).ToList();
            mesh.subMeshCount = keep.Count;
            for (var i = 0; i < keep.Count; i++) mesh.SetIndices(keep[i].indices, keep[i].topology, i);
            mesh.RecalculateBounds();
        }
    }
}
