using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Lighting
{
    /// <summary>
    /// Tunables of the track lighting tools (<see cref="TrackLightBaker"/>): sun shadow look, lightmap budget and
    /// light-probe layout. One asset at <see cref="AssetPath"/>, created with the defaults on first use.
    /// </summary>
    public class TrackBakeSettings : ScriptableObject
    {
        public const string AssetPath = "Assets/Settings/TrackBakeSettings.asset";

        [Header("Sun")]
        [Tooltip("Shadow strength of every shadow-casting track light (realtime and baked shadows alike): 0 = no shadow, 1 = black.")]
        [SerializeField, Range(0f, 1f)] private float shadowStrength = 0.5f;
        [Tooltip("Baked shadow softness: the sun's angular diameter (degrees) used by the lightmapper for penumbrae.")]
        [SerializeField, Range(0f, 10f)] private float bakedShadowAngle = 1.5f;

        [Header("Lightmaps")]
        [Tooltip("Size of one lightmap atlas (texels per side). 1024 keeps every track Android-friendly.")]
        [SerializeField] private int lightmapSize = 1024;
        [Tooltip("Lightmap texels per track, as a number of full atlases. The texel density follows from the static surface area: " +
                 "sum(area * (density * scale)^2 / UV coverage) = budget * packing efficiency.")]
        [SerializeField] private float lightmapBudget = 6f;
        [Tooltip("Share of an atlas the packer fills with charts (padding and packing losses). Rects are enlarged by 1 / sqrt(UV coverage) to keep the density.")]
        [SerializeField, Range(0.3f, 1f)] private float packingEfficiency = 0.7f;
        [Tooltip("Lowest and highest texel density (texels per metre) a track may get from the budget.")]
        [SerializeField] private Vector2 texelDensityRange = new(0.5f, 4f);
        [Tooltip("Geometry farther than this (m) from every racing line gets less lightmap resolution, down to Far Scale at twice the distance.")]
        [SerializeField] private float farDistance = 60f;
        [Tooltip("Lightmap resolution factor of far geometry (see Far Distance).")]
        [SerializeField, Range(0.05f, 1f)] private float farScale = 0.35f;
        [Tooltip("Texels between the lightmap charts of different renderers.")]
        [SerializeField] private int padding = 2;
        [Tooltip("Texels between the UV islands inside one mesh's lightmap UVs (bilinear filtering must not reach the next island).")]
        [SerializeField] private float islandMarginTexels = 1.5f;
        [Tooltip("Lightmap UV unwrapping: edges between faces at a larger angle (degrees) become island seams.")]
        [SerializeField, Range(1f, 180f)] private float unwrapHardAngle = 88f;
        [Tooltip("Lightmap texture compression (shadowmask included).")]
        [SerializeField] private LightmapCompression compression = LightmapCompression.NormalQuality;
        [Tooltip("Size of the colour lightmap files (texels per side). MHK/Lit never samples them (only the shadowmask), so each bake rewrites them this small.")]
        [SerializeField] private int colorLightmapMaxSize = 32;
        [Tooltip("Lightmapper samples per texel for the sun (shadow quality and noise).")]
        [SerializeField] private int directSamples = 64;
        [Tooltip("Indirect and environment samples. Their result (colour lightmap, probe light) is not used, so they are kept minimal.")]
        [SerializeField] private int indirectSamples = 8;
        [Tooltip("Lightmapper backend. Switching changes the bake look and noise, never gameplay.")]
        [SerializeField] private LightingSettings.Lightmapper lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
        [Tooltip("Share of one atlas a single renderer's lightmap rect may fill. Larger rects make atlassing fail.")]
        [SerializeField] private double atlasFillSafety = 0.9;
        [Tooltip("Lowest UV coverage assumed for a mesh's lightmap UVs when sizing its rect and island margins.")]
        [SerializeField] private double minCoverageFloor = 0.05;
        [Tooltip("Unwraps per mesh at most, refining the island margin to the coverage the previous unwrap reached.")]
        [SerializeField, Min(1)] private int unwrapIterations = 3;
        [Tooltip("Margin refinement stops when the wanted margin differs from the used one by less than this share.")]
        [SerializeField, Range(0f, 1f)] private float marginConvergence = 0.2f;

        [Header("Baked indirect light (scenes with BakedIndirectLighting)")]
        [Tooltip("Indirect and environment samples per texel in scenes whose colour lightmaps are used (sky light, bounce, glow).")]
        [SerializeField] private int bakedIndirectSamples = 512;
        [Tooltip("Light bounces in those scenes.")]
        [SerializeField, Range(1, 4)] private int bakedIndirectBounces = 2;
        [Tooltip("Denoise their indirect light (OpenImageDenoise), else a Gaussian filter only.")]
        [SerializeField] private bool bakedIndirectDenoise = true;
        [Tooltip("Gaussian filter radius (texels) of their indirect light, after denoising.")]
        [SerializeField, Range(0f, 5f)] private float bakedIndirectFilterRadius = 1f;

        [Header("Light probes (racing lines)")]
        [Tooltip("Distance between probe rings along every racing line (m).")]
        [SerializeField] private float probeSpacing = 8f;
        [Tooltip("Lateral probe offset on each side, as a share of the track width.")]
        [SerializeField, Range(0f, 0.5f)] private float probeLateral = 0.35f;
        [Tooltip("Probe heights above the ground (m), from just above the road to above the drivers. The lowest must stay below the kart bodies' renderer centres (about 0.15 m): a renderer below the probes samples outside the probe hull and gets an extrapolated, often sunlit occlusion.")]
        [SerializeField] private float[] probeHeights = { 0.1f, 2.5f };
        [Tooltip("Probes closer than this (m) to an earlier one are dropped (overlapping routes). Keep it below the height steps.")]
        [SerializeField] private float probeMergeDistance = 1f;
        [Tooltip("Realtime cut-out renderers (foliage) larger than this (m) use no light probes: one probe sample cannot shade a mesh spread over the track.")]
        [SerializeField] private float foliageProbeMaxSize = 30f;
        [Tooltip("Height (m) of the wall check between a ring's centre and side probes, above road camber and kerbs.")]
        [SerializeField] private float probeWallCheckHeight = 1f;
        [Tooltip("Ground search starts this far (m) above a probe point, below tunnel ceilings.")]
        [SerializeField] private float groundSearchLift = 1.5f;
        [Tooltip("Ground search depth (m) below its start. Points without ground keep their height.")]
        [SerializeField] private float groundSearchDepth = 8f;

        public float ShadowStrength => shadowStrength;
        public float BakedShadowAngle => bakedShadowAngle;
        public int LightmapSize => lightmapSize;
        public float LightmapBudget => lightmapBudget;
        public float PackingEfficiency => packingEfficiency;
        public Vector2 TexelDensityRange => texelDensityRange;
        public float FarDistance => farDistance;
        public float FarScale => farScale;
        public int Padding => padding;
        public float IslandMarginTexels => islandMarginTexels;
        public float UnwrapHardAngle => unwrapHardAngle;
        public LightmapCompression Compression => compression;
        public int ColorLightmapMaxSize => colorLightmapMaxSize;
        public int DirectSamples => directSamples;
        public int IndirectSamples => indirectSamples;
        public LightingSettings.Lightmapper Lightmapper => lightmapper;
        public double AtlasFillSafety => atlasFillSafety;
        public double MinCoverageFloor => minCoverageFloor;
        public int UnwrapIterations => unwrapIterations;
        public float MarginConvergence => marginConvergence;
        public int BakedIndirectSamples => bakedIndirectSamples;
        public int BakedIndirectBounces => bakedIndirectBounces;
        public bool BakedIndirectDenoise => bakedIndirectDenoise;
        public float BakedIndirectFilterRadius => bakedIndirectFilterRadius;
        public float ProbeSpacing => probeSpacing;
        public float ProbeLateral => probeLateral;
        public IReadOnlyList<float> ProbeHeights => probeHeights;
        public float ProbeMergeDistance => probeMergeDistance;
        public float FoliageProbeMaxSize => foliageProbeMaxSize;
        public float ProbeWallCheckHeight => probeWallCheckHeight;
        public float GroundSearchLift => groundSearchLift;
        public float GroundSearchDepth => groundSearchDepth;

        /// <summary>The project's settings asset, created with the defaults if missing.</summary>
        public static TrackBakeSettings Load()
        {
            var settings = AssetDatabase.LoadAssetAtPath<TrackBakeSettings>(AssetPath);
            if (settings != null) return settings;
            settings = CreateInstance<TrackBakeSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
