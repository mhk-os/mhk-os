using MHK.Rendering;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Lighting
{
    /// <summary>
    /// The scene's ambient probe as six axis colours for MHK/Lit (<c>_MHKAmbientCube</c>), and the light-probe
    /// upkeep of additively loaded tracks (re-tetrahedralization, <c>_MHKProbeAmbient</c>).
    /// </summary>
    public static partial class AmbientCube
    {
        static readonly Vector3[] Directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        static readonly Color[] colors = new Color[6];
        static readonly Vector4[] cube = new Vector4[6];
        [AutoStaticsCleanup] static int uploadedProbeCount = -1;
        [AutoStaticsCleanup] static SphericalHarmonicsL2 uploadedAmbient;
        [AutoStaticsCleanup] static bool ambientUploaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void SubscribeProbeEvents()
        {
            LightProbes.needsRetetrahedralization -= Retetrahedralize;
            LightProbes.needsRetetrahedralization += Retetrahedralize;
        }

        static void Retetrahedralize() => LightProbes.TetrahedralizeAsync();

        /// <summary>
        /// Uploads the ambient probe and whether baked light probes are loaded. URP binds no SH ambient for lightmapped
        /// renderers and baked probes hold baked light, so MHK/Lit shades those cases with this cube. Cheap when unchanged,
        /// called every frame (GraphicsQuality.Tick in play mode, the editor otherwise).
        /// </summary>
        public static void Update()
        {
            UploadProbeFlag();
            var sh = RenderSettings.ambientProbe;
            if (ambientUploaded && sh == uploadedAmbient) return;
            sh.Evaluate(Directions, colors);
            for (var i = 0; i < cube.Length; i++) cube[i] = colors[i];
            Shader.SetGlobalVectorArray(MHKLitIds.AmbientCube, cube);
            uploadedAmbient = sh;
            ambientUploaded = true;
        }

        static void UploadProbeFlag()
        {
            var probes = LightmapSettings.lightProbes;
            var probeCount = probes != null ? probes.count : 0;
            if (probeCount == uploadedProbeCount) return;
            Shader.SetGlobalFloat(MHKLitIds.ProbeAmbient, probeCount > 0 ? 1f : 0f);
            uploadedProbeCount = probeCount;
        }
    }
}
