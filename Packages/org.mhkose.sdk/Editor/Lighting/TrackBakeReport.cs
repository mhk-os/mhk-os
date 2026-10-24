using System.Collections.Generic;
using System.Text;
using UnityEditor;

namespace MHK.Editor.Lighting
{
    /// <summary>Outcome of preparing or baking one track scene. <see cref="ToString"/> is the log line.</summary>
    public sealed class TrackBakeReport
    {
        internal const double BytesPerMB = 1048576.0;

        public string scenePath;
        public int staticRenderers, dynamicRenderers, realtimeCutouts, cutoutSplits, meshesUnwrapped, meshesSkipped, probes, lights;
        /// <summary>The scene keeps baked indirect light (<see cref="MHK.Lighting.BakedIndirectLighting"/>).</summary>
        public bool bakedIndirect;
        /// <summary>Baked-indirect scenes: lights baked into lightmaps and probes, and static renderers left out of the bake so light passes through.</summary>
        public int bakedLights, passThrough;
        /// <summary>Renderers whose lightmap rect was reduced to fit one atlas.</summary>
        public int cappedRenderers;
        /// <summary>What the scene's <see cref="TrackLightingProfile"/> entry set, null without one.</summary>
        public string profile;
        /// <summary>Static surfaces too large to bake (more than one atlas at the lowest texel density), left realtime.</summary>
        public int oversized;
        public double staticArea;
        public float texelDensity;
        public readonly List<string> warnings = new();
        public bool baked;
        public double bakeSeconds;
        public int lightmapCount;
        public string lightmapSizes = "";
        /// <summary>Lightmap memory on the active build target, and the shadowmasks' estimated size on Android.</summary>
        public long shadowmaskBytes, colorBytes, shadowmaskAndroidBytes;

        public override string ToString()
        {
            var sb = new StringBuilder($"[Lighting] {scenePath}: {staticRenderers} static renderers ({staticArea / 1000.0:0} k m², " +
                                       $"{texelDensity:0.00} texels/m near the road), {dynamicRenderers} dynamic ({realtimeCutouts} static cut-outs, " +
                                       $"{cutoutSplits} split off, {oversized} too large to bake), {meshesUnwrapped} meshes unwrapped, {cappedRenderers} capped to one atlas, {probes} light probes, {lights} shadowed lights");
            if (bakedIndirect) sb.Append($"; baked indirect light: {bakedLights} baked lights, {passThrough} static renderers letting light through");
            if (profile != null) sb.Append("; " + profile);
            if (baked)
                sb.Append($"; baked in {bakeSeconds:0} s: {lightmapCount} lightmaps {lightmapSizes}, shadowmask {shadowmaskBytes / BytesPerMB:0.00} MB + " +
                          $"colour {colorBytes / BytesPerMB:0.00} MB ({EditorUserBuildSettings.activeBuildTarget}), shadowmask on Android ≈ {shadowmaskAndroidBytes / BytesPerMB:0.00} MB");
            foreach (var w in warnings) sb.Append("\n  " + w);
            return sb.ToString();
        }
    }
}
