using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MHK.Core
{
    /// <summary>
    /// Switches to the Quality level of a graphics tier ("PC Low", "Mobile High", ... in the family of the active level)
    /// and its URP asset. Values the asset does not hold go to a runtime clone, so project assets are never written.
    /// </summary>
    static partial class QualityLevels
    {
        [AutoStaticsCleanup] static RenderPipelineAsset[] authored;
        [AutoStaticsCleanup] static UniversalRenderPipelineAsset clone;
        [AutoStaticsCleanup] static UniversalRenderPipelineAsset cloneSource;
        [AutoStaticsCleanup] static int level = -1;
        [AutoStaticsCleanup] static float renderScale = 1f;
        [AutoStaticsCleanup] static int msaa = 1;

        public static UniversalRenderPipelineAsset Active => GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        /// <summary>Activates the level of <paramref name="tier"/> rendering at the given scale and MSAA sample count.</summary>
        public static void Select(GraphicsPreset tier, float scale, int samples)
        {
            authored ??= AuthoredAssets();
            level = LevelOf(tier);
            if (QualitySettings.GetQualityLevel() != level) QualitySettings.SetQualityLevel(level, true);
            renderScale = scale;
            msaa = samples;
            UsePipeline();
        }

        /// <summary>Changes the render scale of the selected level (window or screen size changed).</summary>
        public static void SetRenderScale(float scale)
        {
            if (level < 0 || Mathf.Approximately(scale, renderScale)) return;
            renderScale = scale;
            UsePipeline();
        }

        static void UsePipeline()
        {
            var own = authored[level];
            var source = (own != null ? own : GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
            if (source == null) return;
            var fits = Mathf.Approximately(source.renderScale, renderScale) && source.msaaSampleCount == msaa;
            var stale = fits || cloneSource != source ? clone : null;
            if (stale != null) (clone, cloneSource) = (null, null);
            var target = fits ? own : CloneOf(source);
            if (QualitySettings.renderPipeline != target) QualitySettings.renderPipeline = target;
            if (stale != null) CoreUtils.Destroy(stale);
        }

        static UniversalRenderPipelineAsset CloneOf(UniversalRenderPipelineAsset source)
        {
            if (clone == null)
            {
                clone = Object.Instantiate(source);
                clone.name = source.name + " (runtime)";
                cloneSource = source;
            }
            clone.renderScale = renderScale;
            clone.msaaSampleCount = msaa;
            return clone;
        }

        static RenderPipelineAsset[] AuthoredAssets()
        {
            var assets = new RenderPipelineAsset[QualitySettings.count];
            for (var i = 0; i < assets.Length; i++) assets[i] = QualitySettings.GetRenderPipelineAssetAt(i);
            return assets;
        }

        static int LevelOf(GraphicsPreset tier)
        {
            var names = QualitySettings.names;
            var current = QualitySettings.GetQualityLevel();
            var family = names[current].Substring(0, names[current].LastIndexOf(' ') + 1);
            var index = System.Array.IndexOf(names, family + tier);
            return index >= 0 ? index : current;
        }
    }
}
