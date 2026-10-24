using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Core;
using MHK.Editor.Tracks;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Collision of track.json ("collision": one entry per file with a default surface, "surfaces": material or texture
    /// name to surface kind), merged per file and surface kind into double-sided mesh colliders with a
    /// <see cref="TrackSurface"/> holding the kind's defaults.
    /// </summary>
    public static class TrackCollisionBuilder
    {
        const MeshColliderCookingOptions Cooking = MeshColliderCookingOptions.CookForFasterSimulation | MeshColliderCookingOptions.EnableMeshCleaning |
                                                   MeshColliderCookingOptions.WeldColocatedVertices | MeshColliderCookingOptions.UseFastMidphase;

        sealed class Group
        {
            public readonly List<Vector3> vertices = new();
            public readonly List<int> indices = new();
        }

        /// <summary>Builds the Collision group under <paramref name="root"/>.</summary>
        public static void Build(TrackImportContext c, Transform root)
        {
            var colRoot = ImportHierarchy.CreateChild(root, TrackRuntime.CollisionGroup);
            var groups = new Dictionary<(SurfaceKind kind, string file), Group>();
            var surfaces = c.Json["surfaces"] as JObject ?? new JObject();
            foreach (var entry in ExportJson.Items(c.Json["collision"]))
            {
                if (ExportJson.B(entry["inert"]) || ExportJson.B(entry["aiOnly"])) continue;
                var file = ExportJson.Str(entry["file"]);
                var m = c.Model(file, GlbLightingStyle.World);
                if (m != null) CollectTriangles(c, m, file, ParseSurface(ExportJson.Str(entry["surface"])), surfaces, groups);
            }
            foreach (var ((kind, file), g) in groups.OrderBy(x => x.Key.kind))
                if (g.indices.Count > 0) CreateCollider(c, colRoot, kind, file, g);
        }

        static void CollectTriangles(TrackImportContext c, GlbModel m, string file, SurfaceKind defaultKind, JObject surfaces,
            Dictionary<(SurfaceKind, string), Group> groups)
        {
            var names = MaterialNames(m);
            var inst = c.Instance(m, null, "collision");
            try
            {
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    var mats = mf.TryGetComponent(out MeshRenderer mr) ? mr.sharedMaterials : Array.Empty<Material>();
                    using var data = MeshReadback.Of(mf.sharedMesh);
                    var verts = data.Vertices();
                    // The instance sits at the track origin with the model's scale, like the Visual copy.
                    var toTrack = mf.transform.localToWorldMatrix;
                    for (var sm = 0; sm < data.SubMeshCount; sm++)
                    {
                        var kind = SurfaceFor(defaultKind, sm < mats.Length ? mats[sm] : null, names, surfaces);
                        var key = (kind, Path.GetFileNameWithoutExtension(file));
                        if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group();
                        AppendSubmesh(g, data.Indices(sm), verts, toTrack);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(inst);
            }
        }

        static SurfaceKind SurfaceFor(SurfaceKind defaultKind, Material mat, Dictionary<Material, (string name, string texture)> names, JObject surfaces)
        {
            if (mat == null || !names.TryGetValue(mat, out var n)) return defaultKind;
            var key = n.name != null && surfaces[n.name] != null ? n.name : n.texture;
            return key != null && surfaces[key] != null ? ParseSurface(ExportJson.Str(surfaces[key])) : defaultKind;
        }

        static void AppendSubmesh(Group g, int[] indices, Vector3[] verts, Matrix4x4 toTrack)
        {
            var remap = new Dictionary<int, int>();
            foreach (var idx in indices)
            {
                if (!remap.TryGetValue(idx, out var ni))
                {
                    ni = g.vertices.Count;
                    g.vertices.Add(toTrack.MultiplyPoint3x4(verts[idx]));
                    remap[idx] = ni;
                }
                g.indices.Add(ni);
            }
        }

        static void CreateCollider(TrackImportContext c, Transform colRoot, SurfaceKind kind, string file, Group g)
        {
            SplitLongTriangles(g.vertices, g.indices, c.Settings.maxCollisionEdge);
            var mesh = new Mesh
            {
                name = $"{c.ShortName}_collision_{AssetPaths.SafeFileName(file)}_{kind.ToString().ToLowerInvariant()}",
                indexFormat = MeshIndex.FormatFor(g.vertices.Count),
            };
            mesh.SetVertices(g.vertices);
            mesh.SetTriangles(g.indices, 0);
            mesh.RecalculateBounds();
            CollisionWinding.MakeDoubleSided(mesh);
            mesh = c.Library.Store(mesh);

            var go = ImportHierarchy.CreateChild(colRoot, $"{kind} ({file})").gameObject;
            go.layer = GameLayers.Track;
            GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)~0);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.cookingOptions = Cooking;
            mc.sharedMaterial = kind == SurfaceKind.Wall ? c.Settings.wallMaterial : c.Settings.groundMaterial;
            var surf = go.AddComponent<TrackSurface>();
            surf.kind = kind;
            surf.ApplyDefaults();
        }

        static SurfaceKind ParseSurface(string s) =>
            Enum.TryParse<SurfaceKind>(s ?? "road", true, out var k) ? k : SurfaceKind.Road;

        static Dictionary<Material, (string name, string texture)> MaterialNames(GlbModel m)
        {
            var result = new Dictionary<Material, (string, string)>();
            var mats = m.json["materials"] as JArray;
            var textures = m.json["textures"] as JArray;
            var images = m.json["images"] as JArray;
            foreach (var (index, mat) in m.materials)
            {
                if (mat == null || mats == null || index < 0 || index >= mats.Count) continue;
                var jm = mats[index];
                string tex = null;
                var ti = jm["pbrMetallicRoughness"]?["baseColorTexture"]?["index"];
                if (ti != null && textures != null && (int)ti < textures.Count && textures[(int)ti]["source"] is { } src && images != null && (int)src < images.Count)
                    tex = ExportJson.Str(images[(int)src]["name"]) ?? Path.GetFileNameWithoutExtension(ExportJson.Str(images[(int)src]["uri"], ""));
                result[mat] = (ExportJson.Str(jm["name"]), tex);
            }
            return result;
        }

        // Splits at the longest edge until no edge exceeds maxEdge (0 = off). Shared edges share a midpoint, so the surface stays closed.
        static void SplitLongTriangles(List<Vector3> v, List<int> tris, float maxEdge)
        {
            if (maxEdge <= 0f) return;
            var max2 = maxEdge * maxEdge;
            var midpoints = new Dictionary<(int, int), int>();
            var input = new List<int>(tris);
            tris.Clear();
            for (var t = 0; t + 2 < input.Count; t += 3)
            {
                int a = input[t], b = input[t + 1], c = input[t + 2];
                var ab = (v[a] - v[b]).sqrMagnitude;
                var bc = (v[b] - v[c]).sqrMagnitude;
                var ca = (v[c] - v[a]).sqrMagnitude;
                if (ab <= max2 && bc <= max2 && ca <= max2)
                {
                    AddTriangle(tris, a, b, c);
                    continue;
                }
                SplitAtLongestEdge(v, midpoints, input, a, b, c, ab, bc, ca);
            }
        }

        // Both halves go back into the queue with the same winding.
        static void SplitAtLongestEdge(List<Vector3> v, Dictionary<(int, int), int> midpoints, List<int> queue,
            int a, int b, int c, float ab, float bc, float ca)
        {
            if (ab >= bc && ab >= ca)
            {
                var m = Midpoint(v, midpoints, a, b);
                AddTriangle(queue, a, m, c);
                AddTriangle(queue, m, b, c);
            }
            else if (bc >= ca)
            {
                var m = Midpoint(v, midpoints, b, c);
                AddTriangle(queue, a, b, m);
                AddTriangle(queue, a, m, c);
            }
            else
            {
                var m = Midpoint(v, midpoints, c, a);
                AddTriangle(queue, a, b, m);
                AddTriangle(queue, m, b, c);
            }
        }

        static int Midpoint(List<Vector3> v, Dictionary<(int, int), int> midpoints, int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (midpoints.TryGetValue(key, out var m)) return m;
            v.Add((v[a] + v[b]) * 0.5f);
            return midpoints[key] = v.Count - 1;
        }

        static void AddTriangle(List<int> list, int a, int b, int c)
        {
            list.Add(a);
            list.Add(b);
            list.Add(c);
        }
    }
}
