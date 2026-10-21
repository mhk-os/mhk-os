using UnityEditor;
using UnityEngine;

namespace MHK.Editor
{
    /// <summary>Child renderers that inherit a source renderer's settings (chunks, split-off submeshes).</summary>
    public static class RendererSettingsUtil
    {
        /// <summary>
        /// New child of <paramref name="parent"/> with its layer and tag, <paramref name="flags"/> and a MeshFilter plus
        /// MeshRenderer drawing <paramref name="mesh"/>. Materials and renderer settings are left to the caller.
        /// </summary>
        public static MeshRenderer CreateChildRenderer(GameObject parent, string name, Mesh mesh, StaticEditorFlags flags)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            child.layer = parent.layer;
            child.tag = parent.tag;
            GameObjectUtility.SetStaticEditorFlags(child, flags);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            return child.AddComponent<MeshRenderer>();
        }

        /// <summary>Enabled state, shadows, reflection probes, motion vectors, occlusion and rendering layers.</summary>
        public static void CopyDrawSettings(MeshRenderer from, MeshRenderer to)
        {
            to.enabled = from.enabled;
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
            to.motionVectorGenerationMode = from.motionVectorGenerationMode;
            to.allowOcclusionWhenDynamic = from.allowOcclusionWhenDynamic;
            to.renderingLayerMask = from.renderingLayerMask;
        }

        /// <summary>Every renderer setting except materials, including probes, sorting and the lightmap placement.</summary>
        public static void CopySettings(MeshRenderer from, MeshRenderer to)
        {
            CopyDrawSettings(from, to);
            to.staticShadowCaster = from.staticShadowCaster;
            to.lightProbeUsage = from.lightProbeUsage;
            to.lightProbeProxyVolumeOverride = from.lightProbeProxyVolumeOverride;
            to.probeAnchor = from.probeAnchor;
            to.rendererPriority = from.rendererPriority;
            to.rayTracingMode = from.rayTracingMode;
            to.sortingLayerID = from.sortingLayerID;
            to.sortingOrder = from.sortingOrder;
            to.receiveGI = from.receiveGI;
            to.scaleInLightmap = from.scaleInLightmap;
            to.stitchLightmapSeams = from.stitchLightmapSeams;
            to.lightmapIndex = from.lightmapIndex;
            to.lightmapScaleOffset = from.lightmapScaleOffset;
            to.realtimeLightmapIndex = from.realtimeLightmapIndex;
            to.realtimeLightmapScaleOffset = from.realtimeLightmapScaleOffset;
        }
    }
}
