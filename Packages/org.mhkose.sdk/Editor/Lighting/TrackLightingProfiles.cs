using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MHK.Editor.Tracks;
using MHK.Lighting;
using MHK.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MHK.Editor.Lighting
{
    /// <summary>
    /// Applies <see cref="TrackLightingProfile"/> entries to their scenes: indirect light, environment overrides,
    /// emitter glow and generated baked lights.
    /// </summary>
    public static class TrackLightingProfiles
    {
        /// <summary>Name of the group holding the generated lights.</summary>
        public const string LightsGroupName = "Profile Lights";
        const string MenuRoot = "MHK/Lighting/";
        const float WeldCellsPerMetre = 100f;
        const float MaskThreshold = 0.5f;
        const double ClosedTubeNormalShare = 0.3;
        const float VerticalDot = 0.99f;
        const float InnerSpotRatio = 0.5f;
        const float MinAreaLightSize = 0.05f;
        const double MinClusterArea = 1e-9;

        /// <summary>What <see cref="Apply"/> did to one scene.</summary>
        public class Result
        {
            public string profile;
            public bool bakedIndirect;
            public int glowMaterials, lights;
            /// <summary>Renderers of <see cref="TrackLightingProfile.Track.unbakedObjects"/>: left out of the bake, lit as outdoors.</summary>
            public readonly HashSet<Renderer> unbaked = new();
            public readonly List<string> warnings = new();

            public override string ToString()
            {
                var sb = new StringBuilder($"profile '{profile}': {(bakedIndirect ? "baked indirect light" : "no baked indirect light")}, " +
                                           $"{glowMaterials} glowing materials, {lights} baked lamp lights, {unbaked.Count} renderers kept out of the bake");
                foreach (var w in warnings) sb.Append("; " + w);
                return sb.ToString();
            }
        }

        [MenuItem(MenuRoot + "Apply Lighting Profiles to Open Track Scenes")]
        static void ApplyOpenMenu() => TrackScenes.ForEachLoaded(scene =>
        {
            var result = Apply(scene);
            return result == null ? null : $"[Lighting] {scene.path}: {result}";
        });

        [MenuItem(MenuRoot + "Bake Scenes of Selected Lighting Profile")]
        static void BakeSelectedMenu()
        {
            var profile = Selection.activeObject as TrackLightingProfile;
            if (profile == null || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            TrackLightBaker.BakeTracks(ScenePaths(profile));
        }

        [MenuItem(MenuRoot + "Bake Scenes of Selected Lighting Profile", true)]
        static bool BakeSelectedValidate() => Selection.activeObject is TrackLightingProfile;

        /// <summary>Scene paths of every entry of <paramref name="profile"/>.</summary>
        public static List<string> ScenePaths(TrackLightingProfile profile) =>
            profile.tracks.Where(t => t.scene != null).Select(t => AssetDatabase.GetAssetPath(t.scene)).Distinct().ToList();

        /// <summary>The first profile entry (of any profile asset) for the scene at <paramref name="scenePath"/>.</summary>
        public static (TrackLightingProfile profile, TrackLightingProfile.Track track) Find(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath)) return default;
            foreach (var profile in EditorAssets.FindAll<TrackLightingProfile>())
            {
                var track = profile.Find(scenePath);
                if (track != null) return (profile, track);
            }
            return default;
        }

        /// <summary>
        /// Applies the scene's profile entry, null when no profile lists the scene (nothing changed). Marks nothing
        /// dirty itself, callers save.
        /// </summary>
        public static Result Apply(Scene scene)
        {
            var (profile, track) = Find(scene.path);
            if (track == null) return null;
            var result = new Result { profile = profile.name, bakedIndirect = track.bakedIndirect };
            var runtime = TrackScenes.FindRuntime(scene);
            if (runtime == null)
            {
                result.warnings.Add("no TrackRuntime: skipped");
                return result;
            }
            var root = runtime.transform;
            var lights = TrackBakePreparer.SceneLights(scene).ToList();
            var lighting = LightingObject(scene, root, lights);
            ApplyIndirect(scene, lighting, track);
            ApplyEnvironment(scene, track, lights);
            RemoveOldLightGroups(scene);
            var group = CreateLightGroup(track, lighting);

            var renderers = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy).ToList();
            CollectUnbaked(track, renderers, root, result);
            var glowing = new HashSet<Material>();
            foreach (var emitter in track.emitters) ApplyEmitter(emitter, track.bakedIndirect, renderers, root, group, result, glowing);
            result.glowMaterials = glowing.Count;
            return result;
        }

        static Transform LightingObject(Scene scene, Transform root, List<Light> lights)
        {
            var sun = lights.FirstOrDefault(l => l.type == LightType.Directional && l.transform.parent != null);
            if (sun != null) return sun.transform.parent;
            var lighting = root.Find(TrackBakePreparer.LightingObjectName);
            if (lighting != null) return lighting;
            var top = scene.GetRootGameObjects().FirstOrDefault(g => g.name == TrackBakePreparer.LightingObjectName);
            if (top != null) return top.transform;
            lighting = new GameObject(TrackBakePreparer.LightingObjectName).transform;
            Undo.RegisterCreatedObjectUndo(lighting.gameObject, "Lighting");
            lighting.SetParent(root, false);
            return lighting;
        }

        static void ApplyIndirect(Scene scene, Transform lighting, TrackLightingProfile.Track track)
        {
            var existing = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<BakedIndirectLighting>(true)).ToList();
            if (!track.bakedIndirect)
            {
                foreach (var b in existing) Undo.DestroyObjectImmediate(b);
                return;
            }
            var bil = existing.Count > 0 ? existing[0] : Undo.AddComponent<BakedIndirectLighting>(lighting.gameObject);
            foreach (var extra in existing.Skip(1)) Undo.DestroyObjectImmediate(extra);
            Undo.RecordObject(bil, "Baked indirect light");
            bil.enabled = true;
            bil.intensity = track.indirectIntensity;
            bil.minimum = track.minimum;
            bil.sunShadowStrength = track.sunShadowStrength;
            bil.lightmapMaxSize = track.lightmapMaxSize;
        }

        static void ApplyEnvironment(Scene scene, TrackLightingProfile.Track track, List<Light> lights)
        {
            var directional = lights.Where(l => l.type == LightType.Directional).ToList();
            var sun = directional.FirstOrDefault(l => l.shadows != LightShadows.None && l.lightmapBakeType != LightmapBakeType.Baked);
            if (sun == null) sun = directional.FirstOrDefault(l => l.shadows != LightShadows.None);
            if (sun != null)
            {
                Undo.RecordObject(sun.transform, "Sun");
                Undo.RecordObject(sun, "Sun");
                if (track.overrideSunRotation) sun.transform.rotation = Quaternion.Euler(track.sunRotation);
                if (track.sunIntensity >= 0f) sun.intensity = track.sunIntensity;
            }
            if (track.fillIntensity >= 0f)
                foreach (var fill in directional.Where(l => l != sun))
                {
                    Undo.RecordObject(fill, "Fill light");
                    fill.intensity = track.fillIntensity;
                }
            if (!track.overrideAmbient) return;
            TrackScenes.WithActive(scene, () =>
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = track.ambientColor;
            });
        }

        // Every earlier group, wherever it ended up (a scene may have Lighting objects at the root and under the track).
        static void RemoveOldLightGroups(Scene scene)
        {
            foreach (var old in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true))
                         .Where(t => t.name == LightsGroupName).Select(t => t.gameObject).ToList())
                if (old != null) Undo.DestroyObjectImmediate(old);
        }

        static Transform CreateLightGroup(TrackLightingProfile.Track track, Transform lighting)
        {
            if (!track.emitters.Any(e => e.lights != TrackLightingProfile.LightShape.None)) return null;
            var group = new GameObject(LightsGroupName).transform;
            Undo.RegisterCreatedObjectUndo(group.gameObject, "Profile lights");
            group.SetParent(lighting, false);
            return group;
        }

        static void CollectUnbaked(TrackLightingProfile.Track track, List<MeshRenderer> renderers, Transform root, Result result)
        {
            var unbaked = Glob(track.unbakedObjects);
            if (unbaked == null) return;
            foreach (var r in renderers)
                if (ObjectMatches(r.transform, root, unbaked))
                    result.unbaked.Add(r);
        }

        static void ApplyEmitter(TrackLightingProfile.Emitter emitter, bool bakedIndirect, List<MeshRenderer> renderers, Transform root,
                                 Transform group, Result result, HashSet<Material> glowing)
        {
            var clusters = new List<Cluster>();
            var placeLights = emitter.lights != TrackLightingProfile.LightShape.None;
            var matched = MatchEmitter(emitter, renderers, root, placeLights ? clusters : null, result);
            if (matched.Count == 0)
            {
                result.warnings.Add($"emitter '{emitter.label}' ({emitter.objectPattern} / {emitter.materialPattern}) matched nothing");
                return;
            }
            if (emitter.glow)
                foreach (var m in matched)
                    if (SetGlow(m, emitter)) glowing.Add(m);
                    else result.warnings.Add($"material '{m.name}' is not MHK/Lit: no glow");
            if (!placeLights) return;
            if (!bakedIndirect)
            {
                result.warnings.Add($"emitter '{emitter.label}': lights need baked indirect light (they would be realtime), none placed");
                return;
            }
            var merged = Merge(clusters, emitter.mergeDistance).Where(c => c.area >= emitter.minArea).ToList();
            for (var i = 0; i < merged.Count; i++) CreateLight(group, emitter, merged[i], i + 1);
            result.lights += merged.Count;
        }

        static HashSet<Material> MatchEmitter(TrackLightingProfile.Emitter emitter, List<MeshRenderer> renderers, Transform root,
                                              List<Cluster> clusters, Result result)
        {
            var objects = Glob(emitter.objectPattern);
            var materials = Glob(emitter.materialPattern);
            var matched = new HashSet<Material>();
            var mask = clusters != null ? LoadMask(emitter.glowMap) : null;
            foreach (var renderer in renderers)
                if (ObjectMatches(renderer.transform, root, objects))
                    MatchRenderer(renderer, materials, mask, matched, clusters, result);
            if (mask != null) Object.DestroyImmediate(mask);
            return matched;
        }

        static void MatchRenderer(MeshRenderer renderer, Regex materials, Texture2D mask, HashSet<Material> matched, List<Cluster> clusters, Result result)
        {
            var mesh = renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            var shared = renderer.sharedMaterials;
            for (var s = 0; s < shared.Length; s++)
            {
                var m = shared[s];
                if (m == null || !Matches(materials, m.name)) continue;
                matched.Add(m);
                if (clusters != null && mesh != null && s < mesh.subMeshCount)
                    AddClusters(mesh, s, renderer.transform.localToWorldMatrix, mask, m.GetTextureScale(MHKLitIds.BaseMap), m.GetTextureOffset(MHKLitIds.BaseMap), clusters, result);
            }
        }

        // A readable copy of the glow map read from its file (import settings do not matter), else null. Caller destroys it.
        static Texture2D LoadMask(Texture2D glowMap)
        {
            var path = glowMap != null ? AssetDatabase.GetAssetPath(glowMap) : null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var copy = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (copy.LoadImage(File.ReadAllBytes(path))) return copy;
            Object.DestroyImmediate(copy);
            return null;
        }

        static bool SetGlow(Material m, TrackLightingProfile.Emitter e)
        {
            if (m.shader == null || m.shader.name != MHKLitIds.ShaderName) return false;
            Undo.RecordObject(m, "Glow");
            m.SetFloat(MHKLitIds.GlowOn, 1f);
            m.EnableKeyword(MHKLitIds.GlowKeyword);
            m.SetColor(MHKLitIds.GlowColor, e.glowColor);
            m.SetFloat(MHKLitIds.GlowBake, e.glowBake);
            m.SetTexture(MHKLitIds.GlowMap, e.glowMap);
            EditorUtility.SetDirty(m);
            return true;
        }

        static Regex Glob(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            var alternatives = pattern.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0)
                .Select(p => "^" + Regex.Escape(Normalize(p)).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
            return new Regex(string.Join("|", alternatives), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Spaces and underscores alike: imported material names have spaces replaced by underscores.
        static string Normalize(string s) => s.Replace(' ', '_');

        static bool Matches(Regex glob, string name) => glob == null || glob.IsMatch(Normalize(name));

        static bool ObjectMatches(Transform t, Transform root, Regex glob)
        {
            if (glob == null) return true;
            for (; t != null && t != root; t = t.parent)
                if (glob.IsMatch(Normalize(t.name)))
                    return true;
            return false;
        }

        sealed class Cluster
        {
            public Vector3 weightedCentre, normalSum;
            public double area;
            public Bounds bounds;
            public Vector3 Centre => area > MinClusterArea ? weightedCentre / (float)area : bounds.center;

            public void Add(Cluster other)
            {
                weightedCentre += other.weightedCentre;
                normalSum += other.normalSum;
                area += other.area;
                bounds.Encapsulate(other.bounds);
            }

            public Cluster Clone() => new() { bounds = bounds, weightedCentre = weightedCentre, normalSum = normalSum, area = area };
        }

        sealed class WeldedUnionFind
        {
            public readonly List<Vector3> Positions = new();
            readonly Vector3[] vertices;
            readonly Matrix4x4 toWorld;
            readonly Dictionary<Vector3Int, int> ids = new();
            readonly Dictionary<int, int> vertexId = new();
            readonly List<int> parent = new();

            public WeldedUnionFind(Vector3[] vertices, Matrix4x4 toWorld)
            {
                this.vertices = vertices;
                this.toWorld = toWorld;
            }

            public (int a, int b, int c) AddTriangle(int i0, int i1, int i2)
            {
                var t = (Id(i0), Id(i1), Id(i2));
                int ra = RootOf(t.Item1), rb = RootOf(t.Item2), rc = RootOf(t.Item3);
                parent[rb] = ra;
                parent[RootOf(rc)] = ra;
                return t;
            }

            public int RootOf(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            int Id(int v)
            {
                if (vertexId.TryGetValue(v, out var id)) return id;
                var p = toWorld.MultiplyPoint3x4(vertices[v]);
                var key = Vector3Int.RoundToInt(p * WeldCellsPerMetre);
                if (!ids.TryGetValue(key, out id))
                {
                    id = Positions.Count;
                    ids[key] = id;
                    Positions.Add(p);
                    parent.Add(id);
                }
                vertexId[v] = id;
                return id;
            }
        }

        // Faces of one submesh sharing welded world vertices form clusters. A glow mask keeps only faces on its white parts.
        static void AddClusters(Mesh mesh, int submesh, Matrix4x4 toWorld, Texture2D mask, Vector2 scale, Vector2 offset,
                                List<Cluster> into, Result result)
        {
            if (mesh.GetTopology(submesh) != MeshTopology.Triangles) return;
            Vector3[] vertices;
            int[] indices;
            var uvs = new List<Vector2>();
            using (var data = MeshReadback.Of(mesh))
            {
                vertices = data.Vertices();
                indices = data.Indices(submesh);
                if (mask != null) data.GetUVs(0, uvs);
            }
            if (mask != null && uvs.Count != vertices.Length)
            {
                result.warnings.Add($"mesh '{mesh.name}' has no texture coordinates: glow mask ignored for its lights");
                mask = null;
            }
            var weld = new WeldedUnionFind(vertices, toWorld);
            var triangles = new List<(int a, int b, int c)>();
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                if (indices[i] >= vertices.Length || indices[i + 1] >= vertices.Length || indices[i + 2] >= vertices.Length) continue;
                if (mask != null && !GlowMaskAccepts(mask, uvs[indices[i]], uvs[indices[i + 1]], uvs[indices[i + 2]], scale, offset)) continue;
                triangles.Add(weld.AddTriangle(indices[i], indices[i + 1], indices[i + 2]));
            }
            into.AddRange(BuildClusters(weld, triangles));
        }

        static bool GlowMaskAccepts(Texture2D mask, Vector2 a, Vector2 b, Vector2 c, Vector2 scale, Vector2 offset)
        {
            var uv = (a + b + c) / 3f * scale + offset;
            return !(mask.GetPixelBilinear(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y)).grayscale < MaskThreshold);
        }

        static IEnumerable<Cluster> BuildClusters(WeldedUnionFind weld, List<(int a, int b, int c)> triangles)
        {
            var clusters = new Dictionary<int, Cluster>();
            foreach (var (a, b, c) in triangles)
            {
                var r = weld.RootOf(a);
                Vector3 pa = weld.Positions[a], pb = weld.Positions[b], pc = weld.Positions[c];
                var cross = Vector3.Cross(pb - pa, pc - pa) * 0.5f;
                var area = cross.magnitude;
                if (!clusters.TryGetValue(r, out var cluster))
                    clusters[r] = cluster = new Cluster { bounds = new Bounds(pa, Vector3.zero) };
                cluster.weightedCentre += (pa + pb + pc) / 3f * area;
                cluster.normalSum += cross;
                cluster.area += area;
                cluster.bounds.Encapsulate(pa);
                cluster.bounds.Encapsulate(pb);
                cluster.bounds.Encapsulate(pc);
            }
            return clusters.Values;
        }

        static List<Cluster> Merge(List<Cluster> clusters, float distance)
        {
            var merged = new List<Cluster>();
            var d2 = distance * distance;
            foreach (var c in clusters.OrderByDescending(c => c.area))
            {
                var target = merged.FirstOrDefault(m => (m.Centre - c.Centre).sqrMagnitude <= d2);
                if (target != null) target.Add(c);
                else merged.Add(c.Clone());
            }
            return merged;
        }

        static void CreateLight(Transform group, TrackLightingProfile.Emitter e, Cluster cluster, int index)
        {
            var dir = FacingDirection(e.facing, cluster);
            var go = new GameObject($"{e.label} {index}");
            go.transform.SetParent(group, false);
            go.transform.SetPositionAndRotation(cluster.Centre + dir * e.offset,
                Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > VerticalDot ? Vector3.forward : Vector3.up));
            var light = go.AddComponent<Light>();
            light.type = LightTypeFor(e.lights);
            light.lightmapBakeType = LightmapBakeType.Baked;
            light.shadows = LightShadows.Soft;
            light.shapeRadius = e.shadowRadius;
            light.color = e.lightColor;
            light.intensity = e.lightIntensity;
            light.range = e.lightRange;
            ConfigureShape(light, e, cluster);
        }

        static Vector3 FacingDirection(TrackLightingProfile.LightFacing facing, Cluster cluster) => facing switch
        {
            TrackLightingProfile.LightFacing.Down => Vector3.down,
            TrackLightingProfile.LightFacing.Up => Vector3.up,
            // A closed tube's normals cancel out: such lamps shine down.
            _ => cluster.normalSum.magnitude > ClosedTubeNormalShare * cluster.area ? cluster.normalSum.normalized : Vector3.down,
        };

        static LightType LightTypeFor(TrackLightingProfile.LightShape shape) => shape switch
        {
            TrackLightingProfile.LightShape.Spot => LightType.Spot,
            TrackLightingProfile.LightShape.Area => LightType.Rectangle,
            _ => LightType.Point,
        };

        static void ConfigureShape(Light light, TrackLightingProfile.Emitter e, Cluster cluster)
        {
            if (light.type == LightType.Spot)
            {
                light.spotAngle = e.spotAngle;
                light.innerSpotAngle = e.spotAngle * InnerSpotRatio;
            }
            if (light.type != LightType.Rectangle) return;
            var ext = cluster.bounds.extents;
            var t = light.transform;
            float Across(Vector3 axis) => 2f * (Mathf.Abs(axis.x) * ext.x + Mathf.Abs(axis.y) * ext.y + Mathf.Abs(axis.z) * ext.z);
            light.areaSize = new Vector2(Mathf.Max(MinAreaLightSize, Across(t.right)), Mathf.Max(MinAreaLightSize, Across(t.up)));
        }
    }
}
