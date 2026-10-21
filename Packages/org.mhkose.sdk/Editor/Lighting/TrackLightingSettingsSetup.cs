using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Lighting
{
    /// <summary>Shared track LightingSettings assets and the project's Shadowmask quality mode.</summary>
    internal static class TrackLightingSettingsSetup
    {
        const string QualitySettingsPath = "ProjectSettings/QualitySettings.asset";

        /// <summary>
        /// Baked shadowmask with Android-friendly lightmaps in the shared <c>TrackLighting</c> settings, or with the
        /// baked-indirect samples, bounces and denoising in <c>TrackLightingIndirect</c> when <paramref name="indirect"/>.
        /// </summary>
        public static LightingSettings Configure(TrackBakeSettings settings, bool indirect)
        {
            var ls = LoadOrCreate(indirect ? SdkPaths.TrackLightingIndirect : SdkPaths.TrackLighting);
            ApplyShadowmaskBase(ls, settings);
            if (indirect) ApplyIndirectOverrides(ls, settings);
            EditorUtility.SetDirty(ls);
            return ls;
        }

        /// <summary>Shadowmask (not Distance Shadowmask) on every quality level, so baked objects cast no realtime shadows.</summary>
        public static void ConfigureQualityShadowmask()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath(QualitySettingsPath).FirstOrDefault();
            if (asset == null) return;
            using var so = new SerializedObject(asset);
            var levels = so.FindProperty("m_QualitySettings");
            for (var i = 0; i < levels.arraySize; i++)
                levels.GetArrayElementAtIndex(i).FindPropertyRelative("shadowmaskMode").intValue = (int)ShadowmaskMode.Shadowmask;
            if (so.ApplyModifiedPropertiesWithoutUndo()) QualitySettings.shadowmaskMode = ShadowmaskMode.Shadowmask;
        }

        static LightingSettings LoadOrCreate(string path)
        {
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (ls != null) return ls;
            ls = new LightingSettings { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(ls, path);
            return ls;
        }

        static void ApplyShadowmaskBase(LightingSettings ls, TrackBakeSettings settings)
        {
            ls.bakedGI = true;
            ls.realtimeGI = false;
            ls.mixedBakeMode = MixedLightingMode.Shadowmask;
            ls.lightmapper = settings.Lightmapper;
            ls.directionalityMode = LightmapsMode.NonDirectional;
            ls.lightmapMaxSize = settings.LightmapSize;
            ls.lightmapResolution = 1f; // texels per metre, renderers carry the per-track density in scaleInLightmap
            ls.lightmapPadding = settings.Padding;
            ls.lightmapCompression = settings.Compression;
            ls.ao = false;
            ls.directSampleCount = settings.DirectSamples;
            ls.indirectSampleCount = settings.IndirectSamples;
            ls.environmentSampleCount = settings.IndirectSamples;
            ls.maxBounces = 1;
            ls.minBounces = 1;
            ls.prioritizeView = false;
            ls.filteringMode = LightingSettings.FilterMode.Advanced;
            ls.denoiserTypeDirect = LightingSettings.DenoiserType.None;
            ls.filterTypeDirect = LightingSettings.FilterType.Gaussian;
            ls.filteringGaussianRadiusDirect = 1f;
            ls.denoiserTypeIndirect = LightingSettings.DenoiserType.None;
            ls.filterTypeIndirect = LightingSettings.FilterType.Gaussian;
        }

        static void ApplyIndirectOverrides(LightingSettings ls, TrackBakeSettings settings)
        {
            ls.indirectSampleCount = settings.BakedIndirectSamples;
            ls.environmentSampleCount = settings.BakedIndirectSamples;
            ls.maxBounces = settings.BakedIndirectBounces;
            ls.denoiserTypeIndirect = settings.BakedIndirectDenoise ? LightingSettings.DenoiserType.OpenImage : LightingSettings.DenoiserType.None;
            ls.filteringGaussianRadiusIndirect = settings.BakedIndirectFilterRadius;
        }
    }
}
