using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Editor.Tracks;
using MHK.Lighting;
using MHK.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Lighting
{
    /// <summary>Prepare step of <see cref="TrackLightBaker"/>: lights, static classification, lightmap UVs and scales, probes.</summary>
    internal static class TrackBakePreparer
    {
        internal const string LightingObjectName = "Lighting";
        const string RendererUndo = "Prepare renderer";
        const string MaterialUndo = "Prepare material";
        const string RenderTypeTag = "RenderType";
        const string CutoutRenderType = "TransparentCutout";
        const string OpaqueRenderType = "Opaque";
        const double GuessCoverage = 0.5;
        const int MissingMeshNamesShown = 10;
        const double SquareMetresPerSquareKm = 1e6;

        static string bakedIndirectScriptGuid;

        readonly struct StaticRenderer
        {
            public readonly MeshRenderer Renderer;
            public readonly Mesh Mesh;
            public readonly double Area;
            public readonly float Scale;

            public StaticRenderer(MeshRenderer renderer, Mesh mesh, double area, float scale)
            {
                Renderer = renderer;
                Mesh = mesh;
                Area = area;
                Scale = scale;
            }
        }

        /// <summary>See <see cref="TrackLightBaker.PrepareScene"/>.</summary>
        public static TrackBakeReport Prepare(Scene scene, bool regenerateLightmapUVs)
        {
            var settings = TrackBakeSettings.Load();
            var report = new TrackBakeReport { scenePath = scene.path };
            var profile = TrackLightingProfiles.Apply(scene);
            report.profile = profile?.ToString();
            report.bakedIndirect = FindBakedIndirect(scene) != null;
            var lighting = TrackLightingSettingsSetup.Configure(settings, report.bakedIndirect);
            TrackLightingSettingsSetup.ConfigureQualityShadowmask();
            Lightmapping.SetLightingSettingsForScene(scene, lighting);

            var runtime = TrackScenes.FindRuntime(scene);
            if (runtime == null)
            {
                report.warnings.Add("no TrackRuntime: skipped");
                return report;
            }
            var root = runtime.transform;
            PrepareLights(scene, settings, report);
            var pathPoints = RacingLineProbes.Place(root, settings, out report.probes);
            WarnMissingMeshes(root, report);
            report.cutoutSplits = CutoutSubmeshSplitter.Split(root);
            var statics = ClassifyRenderers(root, profile, settings, pathPoints, report);

            var budget = settings.LightmapBudget * settings.LightmapSize * (double)settings.LightmapSize * settings.PackingEfficiency;
            var coverage = EnsureStaticUVs(statics, settings, budget, regenerateLightmapUVs, report);
            report.texelDensity = Density(budget, statics.Sum(s => s.Area * s.Scale * s.Scale / coverage[s.Mesh]), settings);
            report.staticArea = statics.Sum(s => s.Area);
            var materials = ApplyLightmapScales(statics, coverage, lighting, settings, report);
            foreach (var m in materials) SyncLightmapperCopies(m);
            foreach (var m in materials) MarkGlowBakedEmissive(m);
            EditorSceneManager.MarkSceneDirty(scene);
            return report;
        }

        internal static IEnumerable<Light> SceneLights(Scene scene) =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Light>(true));

        internal static BakedIndirectLighting FindBakedIndirect(Scene scene) =>
            scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<BakedIndirectLighting>()).FirstOrDefault(b => b.enabled);

        internal static float ShadowStrength(Scene scene, TrackBakeSettings settings)
        {
            var indirect = FindBakedIndirect(scene);
            return indirect != null ? indirect.sunShadowStrength : settings.ShadowStrength;
        }

        // Loaded scenes count only an enabled component, scene files any reference to its script.
        internal static bool UsesBakedIndirect(string scenePath)
        {
            var loaded = SceneManager.GetSceneByPath(scenePath);
            if (loaded.isLoaded) return FindBakedIndirect(loaded) != null;
            bakedIndirectScriptGuid ??= FindBakedIndirectScriptGuid();
            return bakedIndirectScriptGuid.Length > 0 && File.ReadAllText(scenePath).Contains("guid: " + bakedIndirectScriptGuid);
        }

        static string FindBakedIndirectScriptGuid()
        {
            var script = EditorAssets.FindPaths("t:MonoScript " + nameof(BakedIndirectLighting))
                .Select(path => AssetDatabase.LoadAssetAtPath<MonoScript>(path))
                .FirstOrDefault(s => s != null && s.GetClass() == typeof(BakedIndirectLighting));
            return script == null ? "" : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
        }

        internal static bool NeverMoves(MeshRenderer renderer, Transform root)
        {
            if (!renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null) return false;
            var materials = renderer.sharedMaterials;
            if (materials.Length == 0 || materials.Any(m => m == null || m.shader == null || m.shader.name != MHKLitIds.ShaderName)) return false;
            for (var t = renderer.transform; t != null && t != root; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                    if (c is Behaviour || c is Rigidbody || c is Joint || c is ParticleSystem)
                        return false;
            return true;
        }

        internal static bool IsCutout(Material m) => m != null && m.IsKeywordEnabled(MHKLitIds.AlphaTestKeyword);

        internal static void SetContributeGI(GameObject go, bool on)
        {
            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            var wanted = on ? flags | StaticEditorFlags.ContributeGI : flags & ~StaticEditorFlags.ContributeGI;
            if (wanted != flags) GameObjectUtility.SetStaticEditorFlags(go, wanted);
        }

        static void PrepareLights(Scene scene, TrackBakeSettings settings, TrackBakeReport report)
        {
            var strength = ShadowStrength(scene, settings);
            foreach (var light in SceneLights(scene))
            {
                Undo.RecordObject(light, "Prepare light");
                var mode = BakeMode(light, report.bakedIndirect);
                // Baked lights are occluded in the bake only with shadows (free at runtime). A directional light baked
                // before (fill light of a scene that no longer bakes indirect light) is an unshadowed realtime fill again.
                if (mode == LightmapBakeType.Baked && light.shadows == LightShadows.None) light.shadows = LightShadows.Soft;
                else if (mode == LightmapBakeType.Realtime && light.type == LightType.Directional && light.lightmapBakeType == LightmapBakeType.Baked)
                    light.shadows = LightShadows.None;
                if (light.shadows != LightShadows.None)
                {
                    light.shadowStrength = strength;
                    if (mode != LightmapBakeType.Baked) report.lights++;
                }
                light.lightmapBakeType = mode;
                if (mode == LightmapBakeType.Baked) report.bakedLights++;
                if (light.type == LightType.Directional && mode != LightmapBakeType.Realtime) light.shadowAngle = settings.BakedShadowAngle;
            }
        }

        static LightmapBakeType BakeMode(Light light, bool indirect)
        {
            var baked = light.lightmapBakeType == LightmapBakeType.Baked;
            if (light.type != LightType.Directional) return indirect && baked ? LightmapBakeType.Baked : LightmapBakeType.Realtime;
            if (baked) return indirect ? LightmapBakeType.Baked : LightmapBakeType.Realtime;
            if (light.shadows != LightShadows.None) return LightmapBakeType.Mixed;
            return indirect ? LightmapBakeType.Baked : LightmapBakeType.Realtime;
        }

        static void WarnMissingMeshes(Transform root, TrackBakeReport report)
        {
            var missing = root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh == null && f.TryGetComponent<MeshRenderer>(out _))
                .Select(f => f.name).ToList();
            if (missing.Count > 0) report.warnings.Add($"{missing.Count} renderers without a mesh: {string.Join(", ", missing.Take(MissingMeshNamesShown))}");
        }

        static List<StaticRenderer> ClassifyRenderers(Transform root, TrackLightingProfiles.Result profile, TrackBakeSettings settings,
                                                      List<Vector3> pathPoints, TrackBakeReport report)
        {
            var statics = new List<StaticRenderer>();
            var maxArea = MaxBakeableArea(settings);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (profile != null && profile.unbaked.Contains(renderer))
                {
                    ExcludeBackdrop(renderer, report);
                    continue;
                }
                var passThrough = report.bakedIndirect && IsPassThrough(renderer, root);
                var oversized = false;
                if (renderer is MeshRenderer mr && !passThrough && IsStatic(mr, root))
                {
                    var mesh = mr.GetComponent<MeshFilter>().sharedMesh;
                    var area = MeshLightmapUVs.WorldArea(mesh, mr.transform.localToWorldMatrix);
                    if (area > 0 && area <= maxArea)
                    {
                        statics.Add(new StaticRenderer(mr, mesh, area, NearScale(mr.bounds, pathPoints, settings)));
                        continue;
                    }
                    oversized = area > maxArea;
                    if (oversized) report.warnings.Add($"'{mr.name}' ({area / SquareMetresPerSquareKm:0.0} km²) left realtime: larger than one lightmap at the lowest texel density");
                }
                MarkDynamic(renderer, root, passThrough, oversized, settings, report);
            }
            return statics;
        }

        // One atlas at the lowest texel density. A larger single surface would pull the whole track's density down.
        static double MaxBakeableArea(TrackBakeSettings settings) =>
            Math.Pow((settings.LightmapSize - 2 * settings.Padding) / (double)settings.TexelDensityRange.x, 2);

        static void ExcludeBackdrop(Renderer renderer, TrackBakeReport report)
        {
            SetContributeGI(renderer.gameObject, false);
            Undo.RecordObject(renderer, RendererUndo);
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            report.dynamicRenderers++;
        }

        // Glass and static surfaces casting no shadows let baked-indirect light through. Glowing ones stay baked.
        static bool IsPassThrough(Renderer renderer, Transform root) =>
            renderer is MeshRenderer see && NeverMoves(see, root) &&
            (IsTransparent(see) || (see.shadowCastingMode == ShadowCastingMode.Off &&
                                    !see.sharedMaterials.Any(m => m != null && m.IsKeywordEnabled(MHKLitIds.GlowKeyword))));

        static bool IsTransparent(MeshRenderer renderer)
        {
            var materials = renderer.sharedMaterials;
            return materials.Length > 0 && materials.All(m => m != null && m.renderQueue > (int)RenderQueue.GeometryLast);
        }

        static bool IsStatic(MeshRenderer renderer, Transform root) =>
            NeverMoves(renderer, root) && !renderer.sharedMaterials.All(IsCutout);

        static void MarkDynamic(Renderer renderer, Transform root, bool passThrough, bool oversized, TrackBakeSettings settings, TrackBakeReport report)
        {
            SetContributeGI(renderer.gameObject, false);
            report.dynamicRenderers++;
            if (passThrough)
            {
                report.passThrough++;
                if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                {
                    Undo.RecordObject(renderer, RendererUndo);
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                }
            }
            if (!(renderer is MeshRenderer foliage) || !NeverMoves(foliage, root)) return;
            SetRealtimeProbeUsage(foliage, settings);
            if (oversized) report.oversized++;
            else report.realtimeCutouts++;
        }

        static void SetRealtimeProbeUsage(MeshRenderer renderer, TrackBakeSettings settings)
        {
            var size = renderer.bounds.size;
            var usage = Mathf.Max(size.x, size.y, size.z) > settings.FoliageProbeMaxSize ? LightProbeUsage.Off : LightProbeUsage.BlendProbes;
            if (renderer.lightProbeUsage == usage) return;
            Undo.RecordObject(renderer, RendererUndo);
            renderer.lightProbeUsage = usage;
        }

        static float NearScale(Bounds bounds, List<Vector3> pathPoints, TrackBakeSettings settings)
        {
            if (pathPoints.Count == 0) return 1f;
            var d2 = pathPoints.Min(p => bounds.SqrDistance(p));
            var t = Mathf.InverseLerp(settings.FarDistance, 2f * settings.FarDistance, Mathf.Sqrt(d2));
            return Mathf.Lerp(1f, settings.FarScale, t);
        }

        static float Density(double budget, double weightedArea, TrackBakeSettings settings) =>
            weightedArea > 0
                ? Mathf.Clamp((float)Math.Sqrt(budget / weightedArea), settings.TexelDensityRange.x, settings.TexelDensityRange.y)
                : settings.TexelDensityRange.x;

        static Dictionary<Mesh, double> EnsureStaticUVs(List<StaticRenderer> statics, TrackBakeSettings settings, double budget, bool regenerate,
                                                        TrackBakeReport report)
        {
            var guessDensity = Density(budget, statics.Sum(s => s.Area * s.Scale * s.Scale) / GuessCoverage, settings);
            var coverage = new Dictionary<Mesh, double>();
            var failed = new HashSet<Mesh>();
            foreach (var (mesh, scaledArea) in SmallestScaledAreas(statics))
            {
                var outcome = MeshLightmapUVs.Ensure(mesh, regenerate, cov => settings.IslandMarginTexels / (float)(Math.Sqrt(scaledArea / cov) * guessDensity), settings);
                if (outcome.Result == MeshLightmapUVs.UnwrapResult.Failed)
                {
                    failed.Add(mesh);
                    report.meshesSkipped++;
                    report.warnings.Add($"mesh '{mesh.name}' left realtime: {outcome.Why}");
                    continue;
                }
                if (outcome.Result == MeshLightmapUVs.UnwrapResult.Unwrapped) report.meshesUnwrapped++;
                coverage[mesh] = Math.Max(outcome.Coverage, settings.MinCoverageFloor);
            }
            statics.RemoveAll(s =>
            {
                if (!failed.Contains(s.Mesh)) return false;
                SetContributeGI(s.Renderer.gameObject, false);
                report.dynamicRenderers++;
                return true;
            });
            return coverage;
        }

        static Dictionary<Mesh, double> SmallestScaledAreas(List<StaticRenderer> statics)
        {
            var smallest = new Dictionary<Mesh, double>();
            foreach (var s in statics)
            {
                var a = s.Area * s.Scale * s.Scale;
                smallest[s.Mesh] = smallest.TryGetValue(s.Mesh, out var min) ? Math.Min(min, a) : a;
            }
            return smallest;
        }

        static HashSet<Material> ApplyLightmapScales(List<StaticRenderer> statics, Dictionary<Mesh, double> coverage, LightingSettings lighting,
                                                     TrackBakeSettings settings, TrackBakeReport report)
        {
            var materials = new HashSet<Material>();
            foreach (var s in statics)
            {
                var renderer = s.Renderer;
                SetContributeGI(renderer.gameObject, true);
                Undo.RecordObject(renderer, RendererUndo);
                renderer.receiveGI = ReceiveGI.Lightmaps;
                var lightmapScale = report.texelDensity * s.Scale / Math.Sqrt(coverage[s.Mesh]) / lighting.lightmapResolution;
                renderer.scaleInLightmap = (float)CapToAtlas(lightmapScale, s.Area, lighting, settings, report);
                if (renderer.shadowCastingMode == ShadowCastingMode.On) renderer.shadowCastingMode = ShadowCastingMode.TwoSided;
                foreach (var m in renderer.sharedMaterials) materials.Add(m);
                report.staticRenderers++;
            }
            return materials;
        }

        static double CapToAtlas(double lightmapScale, double area, LightingSettings lighting, TrackBakeSettings settings, TrackBakeReport report)
        {
            var maxScale = settings.AtlasFillSafety * (settings.LightmapSize - 2 * settings.Padding) / (Math.Sqrt(area) * lighting.lightmapResolution);
            if (!(lightmapScale > maxScale)) return lightmapScale;
            report.cappedRenderers++;
            return maxScale;
        }

        // The lightmapper sees alpha-clipped holes only on materials tagged cutout, reading _MainTex and _Color (MHK/Lit
        // keeps hidden copies), and treats back faces of cull-off materials as valid surfaces only when double-sided.
        static void SyncLightmapperCopies(Material m)
        {
            var tag = m.IsKeywordEnabled(MHKLitIds.AlphaTestKeyword) ? CutoutRenderType : "";
            var doubleSided = m.HasProperty(MHKLitIds.Cull) && Mathf.Approximately(m.GetFloat(MHKLitIds.Cull), (float)CullMode.Off);
            var copy = m.HasProperty(MHKLitIds.MainTex) && m.HasProperty(MHKLitIds.BaseMap);
            if (m.GetTag(RenderTypeTag, false) == (tag == "" ? OpaqueRenderType : tag) && m.doubleSidedGI == doubleSided && (!copy || CopiesInSync(m)))
                return;
            Undo.RecordObject(m, MaterialUndo);
            m.SetOverrideTag(RenderTypeTag, tag);
            m.doubleSidedGI = doubleSided;
            if (copy)
            {
                m.SetTexture(MHKLitIds.MainTex, m.GetTexture(MHKLitIds.BaseMap));
                m.SetTextureScale(MHKLitIds.MainTex, m.GetTextureScale(MHKLitIds.BaseMap));
                m.SetTextureOffset(MHKLitIds.MainTex, m.GetTextureOffset(MHKLitIds.BaseMap));
                m.SetColor(MHKLitIds.Color, m.GetColor(MHKLitIds.BaseColor));
            }
            EditorUtility.SetDirty(m);
        }

        static bool CopiesInSync(Material m) =>
            m.GetTexture(MHKLitIds.MainTex) == m.GetTexture(MHKLitIds.BaseMap) && m.GetColor(MHKLitIds.Color) == m.GetColor(MHKLitIds.BaseColor) &&
            m.GetTextureScale(MHKLitIds.MainTex) == m.GetTextureScale(MHKLitIds.BaseMap) && m.GetTextureOffset(MHKLitIds.MainTex) == m.GetTextureOffset(MHKLitIds.BaseMap);

        static void MarkGlowBakedEmissive(Material m)
        {
            if (!m.IsKeywordEnabled(MHKLitIds.GlowKeyword) || m.globalIlluminationFlags == MaterialGlobalIlluminationFlags.BakedEmissive) return;
            Undo.RecordObject(m, MaterialUndo);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            EditorUtility.SetDirty(m);
        }
    }
}
