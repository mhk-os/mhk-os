using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Editor.Tracks;
using MHK.Modding;
using MHK.Race;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MHK.Editor.Optimization
{
    /// <summary>
    /// Splits large static meshes under a track's Visual group into quadtree chunks so they can be frustum, shadow and
    /// occlusion culled. Lossless and idempotent. Chunk meshes are stored in the source mesh's <see cref="ModelLibrary"/>.
    /// </summary>
    public static class MeshChunker
    {
        /// <summary>Meshes with fewer triangles are not chunked (unless over the UInt16 vertex limit).</summary>
        public const int MinTriangles = 8000;
        /// <summary>Smallest quadtree node side (m) split for triangle count.</summary>
        public const float MinCellSize = 32f;
        /// <summary>A quadtree node with more triangles than this is split into four, down to <see cref="MinCellSize"/>.</summary>
        public const int TargetTriangles = 6000;

        internal const int MaxChunkVertices = ushort.MaxValue;
        const float MinChunkableExtent = 2f * MinCellSize;
        const string ChunkMarker = "_chunk_";

        [MenuItem("MHK/Optimize/Chunk Large Meshes in Open Scene")]
        static void ChunkOpenScene()
        {
            var scene = SceneManager.GetActiveScene();
            var report = ChunkScene(scene);
            if (report.chunked.Count > 0 && !string.IsNullOrEmpty(scene.path)
                && EditorSceneManager.SaveModifiedScenesIfUserWantsTo(new[] { scene }) && !scene.isDirty)
                RemoveUnreferencedSourceMeshes(new[] { report });
            Debug.Log(report);
        }

        [MenuItem("MHK/Optimize/Chunk Large Meshes in All Track Scenes")]
        static void ChunkAllTrackScenes()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var reports = TrackScenes.RunSingle(TrackScenes.AllPaths(), "Chunk large meshes", ChunkScene, SceneSave.IfDirty);
            RemoveUnreferencedSourceMeshes(reports);
            foreach (var r in reports) Debug.Log(r);
            Debug.Log($"[Optimize] {reports.Count} track scenes: chunked {reports.Sum(r => r.chunked.Count)} meshes into " +
                      $"{reports.Sum(r => r.chunked.Sum(e => e.chunkCount))} chunks, removed " +
                      $"{reports.SelectMany(r => r.chunked).Where(e => e.sourceMeshRemoved).Select(e => e.meshName).Distinct().Count()} source meshes.");
        }

        /// <summary>
        /// Chunks the large meshes of the Visual group of the open track <paramref name="scene"/>. Chunk meshes are saved
        /// in the source meshes' libraries, the scene is marked dirty but not saved.
        /// </summary>
        public static MeshChunkReport ChunkScene(Scene scene)
        {
            var report = new MeshChunkReport { scenePath = scene.path };
            var runtime = TrackScenes.FindRuntime(scene);
            var visual = runtime != null ? runtime.transform.Find(TrackRuntime.VisualGroup) : null;
            if (visual == null)
            {
                report.skipped.Add("(scene): no TrackRuntime with a Visual group");
                return report;
            }

            var lodRenderers = new HashSet<Renderer>(scene.GetRootGameObjects()
                .SelectMany(g => g.GetComponentsInChildren<LODGroup>(true))
                .SelectMany(g => g.GetLODs()).SelectMany(l => l.renderers).Where(r => r != null));
            var libraries = new HashSet<ModelLibrary>();
            using (new AssetDatabase.AssetEditingScope())
            {
                foreach (var renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var entry = Chunk(renderer, visual, lodRenderers, report, out var library);
                    if (entry == null) continue;
                    report.chunked.Add(entry);
                    libraries.Add(library);
                }
            }
            foreach (var library in libraries) AssetDatabase.SaveAssetIfDirty(library);
            if (report.chunked.Count > 0) EditorSceneManager.MarkSceneDirty(scene);
            return report;
        }

        /// <summary>
        /// Removes each chunked source mesh of <paramref name="reports"/> from its library when no .unity or .prefab file
        /// under Assets references it (save the chunked scenes first).
        /// </summary>
        public static void RemoveUnreferencedSourceMeshes(IEnumerable<MeshChunkReport> reports) =>
            UnreferencedSubAssets.RemoveSourceMeshes(reports);

        static MeshChunkReport.Entry Chunk(MeshRenderer renderer, Transform visual, HashSet<Renderer> lodRenderers,
                                           MeshChunkReport report, out ModelLibrary library)
        {
            library = null;
            var go = renderer.gameObject;
            var mesh = go.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            if (mesh == null || go.name.Contains(ChunkMarker) || mesh.name.Contains(ChunkMarker)) return null;

            var name = HierarchyPath(renderer.transform, visual);
            var matrix = renderer.transform.localToWorldMatrix;
            MeshChunkReport.Entry entry = null;
            var reason = GetSkipReason(renderer, mesh, visual, lodRenderers, matrix, report)
                         ?? TryChunk(renderer, filter, matrix, name, out entry, out library);
            if (!string.IsNullOrEmpty(reason)) report.skipped.Add($"{name}: {reason}");
            return reason == null ? entry : null;
        }

        // Null for a candidate, empty when silently ignored (counted in the report), else the skip reason.
        static string GetSkipReason(MeshRenderer renderer, Mesh mesh, Transform visual, HashSet<Renderer> lodRenderers,
                                    Matrix4x4 matrix, MeshChunkReport report)
        {
            var sizeReason = SizeSkipReason(mesh, matrix, report);
            if (sizeReason != null) return sizeReason;
            if (PrefabUtility.IsPartOfPrefabInstance(renderer.gameObject)) return "part of a prefab instance";
            if (lodRenderers.Contains(renderer)) return "used by a LODGroup";
            var driver = DrivingComponentReason(renderer.transform, visual);
            if (driver != null) return driver;
            if (mesh.blendShapeCount > 0) return "mesh has blend shapes";
            var materials = renderer.sharedMaterials.Length;
            return materials != mesh.subMeshCount ? $"{materials} materials for {mesh.subMeshCount} submeshes" : null;
        }

        static string SizeSkipReason(Mesh mesh, Matrix4x4 matrix, MeshChunkReport report)
        {
            if (mesh.vertexCount > MaxChunkVertices) return null;
            long indexCount = 0;
            for (var i = 0; i < mesh.subMeshCount; i++) indexCount += mesh.GetIndexCount(i);
            var enoughTriangles = indexCount / 3 >= MinTriangles;
            var bigEnough = HorizontalExtent(mesh.bounds, matrix) > MinChunkableExtent;
            if (!enoughTriangles && !bigEnough)
            {
                report.ignored++;
                return string.Empty;
            }
            if (!enoughTriangles) return $"too few triangles ({indexCount / 3} < {MinTriangles})";
            return bigEnough ? null : $"too small (extent <= {MinChunkableExtent:0.#} m)";
        }

        static string DrivingComponentReason(Transform from, Transform visual)
        {
            for (var t = from; t != null && t != visual; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                    if (c == null || c is MonoBehaviour || c is Animator || c is Animation || c is Rigidbody)
                        return $"{(c == null ? "missing script" : c.GetType().Name)} on '{t.name}' (may move or drive the renderer)";
            return null;
        }

        static string TryChunk(MeshRenderer renderer, MeshFilter filter, Matrix4x4 matrix, string name,
                               out MeshChunkReport.Entry entry, out ModelLibrary library)
        {
            entry = null;
            var mesh = filter.sharedMesh;
            var libraryPath = AssetDatabase.GetAssetPath(mesh);
            library = AssetDatabase.LoadMainAssetAtPath(libraryPath) as ModelLibrary;
            if (library == null) return $"mesh '{mesh.name}' is not stored in a ModelLibrary ({libraryPath})";

            var source = ChunkSourceMesh.Read(mesh, matrix, out var error);
            if (source == null) return error;
            var extent = Mathf.Max(source.maxX - source.minX, source.maxZ - source.minZ);
            if (mesh.vertexCount <= MaxChunkVertices && extent <= MinChunkableExtent)
                return $"too small (extent {extent:0.#} m <= {MinChunkableExtent:0.#} m)";
            var pieces = ChunkQuadtree.Split(source, extent);
            if (pieces.Count < 2) return "all triangles fall into one chunk";

            // Build every chunk mesh before touching the scene or the library, so a failure leaves both unchanged.
            var buildError = TryBuildChunkMeshes(source, pieces, renderer.gameObject.name, out var chunks);
            if (buildError != null) return buildError;
            entry = CreateEntry(name, mesh, libraryPath, source, extent, pieces, chunks);
            AddKeptComponents(entry, renderer.gameObject, mesh);
            CreateChunkObjects(renderer, filter, chunks, library);
            return null;
        }

        static string TryBuildChunkMeshes(ChunkSourceMesh source, List<ChunkQuadtree.Piece> pieces, string baseName,
                                          out List<(string name, Mesh mesh, List<int> subMeshes)> chunks)
        {
            chunks = new List<(string name, Mesh mesh, List<int> subMeshes)>();
            try
            {
                foreach (var piece in pieces)
                {
                    var chunkName = baseName + ChunkMarker + piece.path;
                    var chunkMesh = source.Build(piece.triangles, chunkName, out var subMeshes);
                    chunks.Add((chunkName, chunkMesh, subMeshes));
                }
                return null;
            }
            catch (InvalidOperationException ex)
            {
                foreach (var c in chunks) Object.DestroyImmediate(c.mesh);
                return ex.Message;
            }
        }

        static MeshChunkReport.Entry CreateEntry(string name, Mesh mesh, string libraryPath, ChunkSourceMesh source, float extent,
                                                 List<ChunkQuadtree.Piece> pieces, List<(string name, Mesh mesh, List<int> subMeshes)> chunks) => new()
        {
            sourceName = name, meshName = mesh.name, sourceMesh = mesh, libraryPath = libraryPath,
            vertices = mesh.vertexCount, triangles = source.triangleCount, usedVertices = source.usedVertices,
            extent = extent, leafCount = pieces.Select(p => p.path.Split('_')[0]).Distinct().Count(),
            minLeafSize = pieces.Min(p => p.size), maxLeafSize = pieces.Max(p => p.size),
            chunkCount = chunks.Count, chunkVertices = chunks.Sum(c => c.mesh.vertexCount), chunkTriangles = pieces.Sum(p => p.triangles.Count),
            minChunkTriangles = pieces.Min(p => p.triangles.Count), maxChunkTriangles = pieces.Max(p => p.triangles.Count),
        };

        static void AddKeptComponents(MeshChunkReport.Entry entry, GameObject go, Mesh mesh)
        {
            foreach (var c in go.GetComponents<Component>())
                if (!(c is Transform || c is MeshFilter || c is MeshRenderer))
                    entry.keptComponents.Add(c.GetType().Name + (c is MeshCollider mc && mc.sharedMesh == mesh ? " (uses the source mesh)" : ""));
        }

        static void CreateChunkObjects(MeshRenderer renderer, MeshFilter filter, List<(string name, Mesh mesh, List<int> subMeshes)> chunks,
                                       ModelLibrary library)
        {
            var go = renderer.gameObject;
            var mesh = filter.sharedMesh;
            var materials = renderer.sharedMaterials;
            var flags = GameObjectUtility.GetStaticEditorFlags(go) | StaticEditorFlags.BatchingStatic;
            foreach (var (chunkName, chunkMesh, subMeshes) in chunks)
            {
                if (!mesh.isReadable) MeshWriteScope.SetReadable(chunkMesh, false);
                AssetDatabase.AddObjectToAsset(chunkMesh, library);
                // Chunks keep the source's lightmap UVs, so the copied lightmap placement stays valid.
                var chunkRenderer = RendererSettingsUtil.CreateChildRenderer(go, chunkName, chunkMesh, flags);
                RendererSettingsUtil.CopySettings(renderer, chunkRenderer);
                chunkRenderer.sharedMaterials = subMeshes.Select(s => materials[s]).ToArray();
            }
            EditorUtility.SetDirty(library);
            Object.DestroyImmediate(renderer);
            Object.DestroyImmediate(filter);
        }

        static float HorizontalExtent(Bounds local, Matrix4x4 m)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (var i = 0; i < 8; i++)
            {
                var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = m.MultiplyPoint3x4(corner);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            return Mathf.Max(maxX - minX, maxZ - minZ);
        }

        static string HierarchyPath(Transform t, Transform stop)
        {
            var path = t.name;
            for (var p = t.parent; p != null && p != stop; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
