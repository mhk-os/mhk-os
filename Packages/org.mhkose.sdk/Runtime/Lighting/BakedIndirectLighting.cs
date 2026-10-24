using MHK.Rendering;
using UnityEngine;

namespace MHK.Lighting
{
    /// <summary>
    /// Opts a track scene into baked indirect light. Sets the global <c>_MHKBakedIndirect</c> vector (x = on,
    /// y = <see cref="intensity"/>, z = <see cref="minimum"/>) that MHK/Lit uses instead of the flat ambient. Bake via
    /// MHK ▸ Lighting.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class BakedIndirectLighting : MonoBehaviour
    {
        [Tooltip("Multiplier of the baked indirect light (lightmaps and probes). Changes apply without a new bake.")]
        [Min(0f)] public float intensity = 1f;
        [Tooltip("Lowest indirect light (grey level, 0 to 1): unlit places of the bake stay this dim instead of black. " +
                 "Changes apply without a new bake.")]
        [Range(0f, 0.5f)] public float minimum;

        [Header("Bake (MHK ▸ Lighting)")]
        [Tooltip("Shadow strength of the sun (realtime and baked) in this scene instead of the project-wide value. " +
                 "1 keeps the sun out of interiors lit by the bake.")]
        [Range(0f, 1f)] public float sunShadowStrength = 1f;
        [Tooltip("Size cap (texels per side) of the colour lightmaps the bake keeps, 0 keeps the full atlas size.")]
        [Min(0)] public int lightmapMaxSize = 512;

        void OnEnable() => Apply();

        void OnDisable() => Shader.SetGlobalVector(MHKLitIds.BakedIndirect, Vector4.zero);

        void OnValidate()
        {
            if (isActiveAndEnabled) Apply();
        }

        void Apply() => Shader.SetGlobalVector(MHKLitIds.BakedIndirect, new Vector4(1f, intensity, minimum, 0f));
    }
}
