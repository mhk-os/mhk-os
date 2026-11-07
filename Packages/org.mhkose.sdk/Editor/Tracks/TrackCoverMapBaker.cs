using System.Collections.Generic;
using MHK.Race;
using MHK.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// Bakes a track's <see cref="TrackCoverMap"/>: the height of the topmost opaque render surface at the centre of every
    /// texel of the ground plan, rasterised top-down on the CPU from the triangles under the track's Visual group (the
    /// highest triangle's plane height wins). Transparent and cut-out materials and sky domes do not cover; vertical
    /// faces cover no texel centre, so walls do not dry the road beside them.
    /// </summary>
    public static class TrackCoverMapBaker
    {
        /// <summary>Texel size (m) of tracks up to <see cref="MaxResolution"/> m across.</summary>
        public const float Texel = 1f;
        /// <summary>Longest map side (texels): larger tracks get coarser texels.</summary>
        public const int MaxResolution = 512;

        // Height range of a flat track, so the encoding stays finite.
        const float MinRange = 0.01f;
        // Barycentric tolerance: a texel centre on an edge shared by two triangles belongs to both.
        const float EdgeTolerance = 1e-5f;

        /// <summary>A renderer and the submeshes of its mesh whose materials cover.</summary>
        readonly struct Part
        {
            public readonly MeshRenderer renderer;
            public readonly Mesh mesh;
            public readonly List<int> submeshes;

            public Part(MeshRenderer renderer, Mesh mesh, List<int> submeshes)
            {
                this.renderer = renderer;
                this.mesh = mesh;
                this.submeshes = submeshes;
            }
        }

        /// <summary>Local vertices and per-submesh triangle indices of a mesh (null for other topologies).</summary>
        sealed class MeshTriangles
        {
            public Vector3[] vertices;
            public int[][] indices;
        }

        /// <summary>Topmost height per texel centre (negative infinity = nothing).</summary>
        sealed class Raster
        {
            public readonly float minX, minZ, texel;
            public readonly int width, height;
            public readonly float[] top;

            public Raster(float minX, float minZ, float texel, int width, int height)
            {
                this.minX = minX;
                this.minZ = minZ;
                this.texel = texel;
                this.width = width;
                this.height = height;
                top = new float[width * height];
                System.Array.Fill(top, float.NegativeInfinity);
            }

            // Texel centre (i, j) lies at texel coordinates (i, j): world (minX + (i + 0.5) texel, minZ + (j + 0.5) texel).
            public void Triangle(Vector3 a, Vector3 b, Vector3 c)
            {
                float ax = (a.x - minX) / texel - 0.5f, az = (a.z - minZ) / texel - 0.5f;
                float bx = (b.x - minX) / texel - 0.5f, bz = (b.z - minZ) / texel - 0.5f;
                float cx = (c.x - minX) / texel - 0.5f, cz = (c.z - minZ) / texel - 0.5f;
                var area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
                if (Mathf.Abs(area) < 1e-8f) return; // seen edge-on from above: covers no texel centre
                var x0 = Mathf.Max(0, Mathf.CeilToInt(Mathf.Min(ax, Mathf.Min(bx, cx))));
                var x1 = Mathf.Min(width - 1, Mathf.FloorToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
                var z0 = Mathf.Max(0, Mathf.CeilToInt(Mathf.Min(az, Mathf.Min(bz, cz))));
                var z1 = Mathf.Min(height - 1, Mathf.FloorToInt(Mathf.Max(az, Mathf.Max(bz, cz))));
                var inv = 1f / area;
                for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    var wa = ((bx - x) * (cz - z) - (bz - z) * (cx - x)) * inv;
                    var wb = ((cx - x) * (az - z) - (cz - z) * (ax - x)) * inv;
                    var wc = 1f - wa - wb;
                    if (wa < -EdgeTolerance || wb < -EdgeTolerance || wc < -EdgeTolerance) continue;
                    var y = wa * a.y + wb * b.y + wc * c.y;
                    var i = z * width + x;
                    if (y > top[i]) top[i] = y;
                }
            }
        }

        /// <summary>
        /// Bakes the cover map of the loaded track scene <paramref name="trackScene"/> into the asset at
        /// <paramref name="assetPath"/> (created, or overwritten in place so references to it stay) and returns it. Null
        /// with a warning when the scene has no <see cref="TrackRuntime"/> with a Visual group or that holds no opaque
        /// geometry.
        /// </summary>
        public static TrackCoverMap Bake(Scene trackScene, string assetPath)
        {
            var runtime = TrackScenes.FindRuntime(trackScene);
            var visual = runtime != null ? runtime.transform.Find(TrackRuntime.VisualGroup) : null;
            if (visual == null)
            {
                Debug.LogWarning($"TrackCoverMapBaker: scene '{trackScene.path}' has no TrackRuntime with a {TrackRuntime.VisualGroup} group, no cover map baked.");
                return null;
            }
            var parts = CollectParts(visual, out var bounds);
            if (parts.Count == 0)
            {
                Debug.LogWarning($"TrackCoverMapBaker: the {TrackRuntime.VisualGroup} group of '{trackScene.path}' has no opaque geometry, no cover map baked.");
                return null;
            }
            var texel = Mathf.Max(Texel, Mathf.Max(bounds.size.x, bounds.size.z) / MaxResolution);
            var raster = new Raster(bounds.min.x, bounds.min.z, texel,
                Mathf.Clamp(Mathf.CeilToInt(bounds.size.x / texel), 1, MaxResolution),
                Mathf.Clamp(Mathf.CeilToInt(bounds.size.z / texel), 1, MaxResolution));
            Rasterize(parts, raster);
            return Save(raster, bounds.min.y, Mathf.Max(bounds.size.y, MinRange), assetPath);
        }

        static List<Part> CollectParts(Transform visual, out Bounds bounds)
        {
            var parts = new List<Part>();
            var materials = new List<Material>();
            bounds = new Bounds();
            foreach (var r in visual.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || r.forceRenderingOff || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                if (r.GetComponentInParent<FollowCameraPosition>(true) != null || r.GetComponentInParent<WorldSkyDome>(true) != null) continue;
                if (!r.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) continue;
                r.GetSharedMaterials(materials);
                List<int> submeshes = null;
                for (var i = 0; i < materials.Count && i < filter.sharedMesh.subMeshCount; i++)
                {
                    if (!Covers(materials[i])) continue;
                    submeshes ??= new List<int>();
                    submeshes.Add(i);
                }
                if (submeshes == null) continue;
                if (parts.Count == 0) bounds = r.bounds;
                else bounds.Encapsulate(r.bounds);
                parts.Add(new Part(r, filter.sharedMesh, submeshes));
            }
            return parts;
        }

        // Opaque materials of the world: not sky (queue below Geometry, MHK/Lit's _Fog 0), cut-out or transparent.
        static bool Covers(Material m)
        {
            if (m == null) return false;
            var queue = m.renderQueue;
            if (queue < (int)RenderQueue.Geometry || queue >= (int)RenderQueue.AlphaTest) return false;
            if (m.IsKeywordEnabled(MHKLitIds.AlphaTestKeyword)) return false;
            return !m.HasProperty(MHKLitIds.Fog) || m.GetFloat(MHKLitIds.Fog) >= 0.5f;
        }

        static void Rasterize(List<Part> parts, Raster raster)
        {
            var meshes = new Dictionary<Mesh, MeshTriangles>();
            var world = new Vector3[0];
            foreach (var part in parts)
            {
                if (!meshes.TryGetValue(part.mesh, out var triangles))
                {
                    triangles = Read(part.mesh);
                    meshes.Add(part.mesh, triangles);
                }
                var local = triangles.vertices;
                if (world.Length < local.Length) world = new Vector3[local.Length];
                var toWorld = part.renderer.localToWorldMatrix;
                for (var v = 0; v < local.Length; v++) world[v] = toWorld.MultiplyPoint3x4(local[v]);
                foreach (var submesh in part.submeshes)
                {
                    var indices = triangles.indices[submesh];
                    if (indices == null) continue;
                    for (var t = 0; t + 2 < indices.Length; t += 3)
                        raster.Triangle(world[indices[t]], world[indices[t + 1]], world[indices[t + 2]]);
                }
            }
        }

        static MeshTriangles Read(Mesh mesh)
        {
            using var readback = MeshReadback.Of(mesh);
            var indices = new int[readback.SubMeshCount][];
            for (var s = 0; s < indices.Length; s++)
                if (readback.SubMesh(s).topology == MeshTopology.Triangles) indices[s] = readback.Indices(s);
            return new MeshTriangles { vertices = readback.Vertices(), indices = indices };
        }

        // Heights as R16 over minY .. minY + range, 1 and up for surfaces (0 = nothing), as TrackCoverMap reads them.
        static TrackCoverMap Save(Raster raster, float minY, float range, string assetPath)
        {
            var data = new ushort[raster.top.Length];
            var scale = ushort.MaxValue / range;
            for (var i = 0; i < data.Length; i++)
                if (!float.IsNegativeInfinity(raster.top[i]))
                    data[i] = (ushort)Mathf.Clamp(Mathf.RoundToInt((raster.top[i] - minY) * scale), 1, ushort.MaxValue);

            var map = AssetDatabase.LoadAssetAtPath<TrackCoverMap>(assetPath);
            if (map == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null) AssetDatabase.DeleteAsset(assetPath);
                map = ScriptableObject.CreateInstance<TrackCoverMap>();
                AssetDatabase.CreateAsset(map, assetPath);
            }
            var texture = HeightsTexture(map, assetPath, raster.width, raster.height);
            texture.SetPixelData(data, 0);
            texture.Apply(false, false);
            map.Assign(new Vector2(raster.minX, raster.minZ), new Vector2(raster.width, raster.height) * raster.texel, minY, range, texture);
            EditorUtility.SetDirty(texture);
            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssetIfDirty(map);
            return map;
        }

        // The map's heights sub-asset resized (a new one on the first bake); stray textures of older bakes are removed.
        static Texture2D HeightsTexture(TrackCoverMap map, string assetPath, int width, int height)
        {
            var texture = map.heights != null && AssetDatabase.GetAssetPath(map.heights) == assetPath ? map.heights : null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (asset is not Texture2D stray || stray == texture) continue;
                AssetDatabase.RemoveObjectFromAsset(stray);
                Object.DestroyImmediate(stray, true);
            }
            if (texture == null)
            {
                texture = new Texture2D(width, height, TextureFormat.R16, false, true);
                AssetDatabase.AddObjectToAsset(texture, map);
            }
            else texture.Reinitialize(width, height, TextureFormat.R16, false);
            texture.name = map.name + " heights";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }
    }
}
