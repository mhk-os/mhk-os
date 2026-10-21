using MHK.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MHK.Effects
{
    /// <summary>
    /// Screen-edge zoom blur and speed streaks on the race camera: one render-graph full-screen pass before
    /// post-processing, driven each frame through <see cref="Set"/> and skipped while off or at the Low preset.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class SpeedLines : MonoBehaviour
    {
        [Tooltip("The Hidden/MHK/SpeedLines shader (referenced here so builds include it).")]
        [SerializeField] Shader shader;

        [Header("Edge mask")]
        [Tooltip("Elliptical distance from the screen centre (1 = the left / right edge) where the effect starts...")]
        [SerializeField, Range(0f, 1.5f)] float innerRadius = 0.55f;
        [Tooltip("...and where it is at full strength (beyond 1 = only the corners get the full effect).")]
        [SerializeField, Range(0f, 2f)] float outerRadius = 1.15f;
        [Tooltip("Weight of the top and bottom edges: 1 = like the sides, 0 = sides only.")]
        [SerializeField, Range(0f, 1f)] float verticalWeight = 0.55f;

        [Header("Edge blur")]
        [Tooltip("Zoom-blur length at full strength, as a share of the pixel's distance from the screen centre.")]
        [SerializeField, Range(0f, 0.5f)] float blurLength = 0.07f;
        [Tooltip("Blur samples at the High graphics preset...")]
        [SerializeField, Range(1, 16)] int blurSamples = 8;
        [Tooltip("...and at Medium (fewer = cheaper).")]
        [SerializeField, Range(1, 16)] int blurSamplesMedium = 4;

        [Header("Streaks")]
        [Tooltip("Streak lanes around the screen centre (whole number).")]
        [SerializeField, Min(4)] int streakLanes = 90;
        [Tooltip("Share of the dashes drawn per lane at strength 1. Scales with the strength, so boosts fill the lanes up.")]
        [SerializeField, Range(0f, 1f)] float streakDensity = 0.3f;
        [Tooltip("Streak half-width as a share of a lane (varies ±50 % per dash).")]
        [SerializeField, Range(0f, 0.5f)] float streakWidth = 0.12f;
        [Tooltip("Dash length as a share of the dash spacing (varies ±40 % per dash).")]
        [SerializeField, Range(0.05f, 1f)] float streakLength = 0.5f;
        [Tooltip("Dash spacing along a lane: dashes per screen height.")]
        [SerializeField, Min(0.1f)] float streakFrequency = 1.5f;
        [Tooltip("Outward speed of the dashes (dash spacings per second) at the kart's top speed. Boosts add to it.")]
        [SerializeField, Min(0f)] float streakSpeed = 5f;
        [Tooltip("Streak colour. Alpha = its opacity at full strength.")]
        [SerializeField] Color streakColor = new(1f, 1f, 1f, 0.45f);

        static readonly int IntensityId = Shader.PropertyToID("_SpeedIntensity");
        static readonly int MaskId = Shader.PropertyToID("_SpeedMask");
        static readonly int BlurId = Shader.PropertyToID("_SpeedBlur");
        static readonly int StreaksId = Shader.PropertyToID("_SpeedStreaks");
        static readonly int StreakMotionId = Shader.PropertyToID("_SpeedStreakMotion");
        static readonly int StreakColorId = Shader.PropertyToID("_SpeedStreakColor");

        // The shader's dash hash repeats with this period, so wrapping the phase here is seamless.
        const float PhasePeriod = 256f;
        const float MinIntensity = 0.001f;

        Camera cam;
        UniversalAdditionalCameraData cameraData;
        Material material;
        Pass pass;
        float intensity;
        float phase;

        void Awake()
        {
            cam = GetComponent<Camera>();
            cameraData = cam.GetUniversalAdditionalCameraData();
        }

        void OnEnable()
        {
            if (shader == null || !shader.isSupported) return;
            material = CoreUtils.CreateEngineMaterial(shader);
            pass = new Pass(material);
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            CoreUtils.Destroy(material);
            material = null;
            pass = null;
        }

        /// <summary>
        /// Sets this frame's strength (0 = off, 1 = full, more while boosting) and the streak speed (share of the top
        /// speed, boosts add to it). <paramref name="dt"/> advances the streaks.
        /// </summary>
        public void Set(float strength, float speed, float dt)
        {
            intensity = Mathf.Max(0f, strength);
            phase = Mathf.Repeat(phase + streakSpeed * Mathf.Max(0f, speed) * dt, PhasePeriod);
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera rendering)
        {
            if (rendering != cam || intensity <= MinIntensity || cameraData == null) return;
            var level = GraphicsQuality.SpeedEffect;
            if (level == SpeedEffectLevel.Off) return;
            UploadParameters(level);
            cameraData.scriptableRenderer.EnqueuePass(pass);
        }

        void UploadParameters(SpeedEffectLevel level)
        {
            material.SetFloat(IntensityId, intensity);
            material.SetVector(MaskId, new Vector4(innerRadius, Mathf.Max(innerRadius + 0.01f, outerRadius), verticalWeight, cam.aspect));
            material.SetVector(BlurId, new Vector4(blurLength, level == SpeedEffectLevel.High ? blurSamples : blurSamplesMedium, 0f, 0f));
            material.SetVector(StreaksId, new Vector4(streakLanes, streakDensity, streakWidth, streakLength));
            material.SetVector(StreakMotionId, new Vector4(streakFrequency, phase, PhasePeriod, 0f));
            material.SetColor(StreakColorId, streakColor);
        }

        // Blits the camera colour through the effect into a new texture that becomes the camera colour (no copy back).
        sealed class Pass : ScriptableRenderPass
        {
            readonly Material material;

            public Pass(Material material)
            {
                this.material = material;
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return;
                var source = resources.cameraColor;
                var desc = renderGraph.GetTextureDesc(source);
                desc.name = "_SpeedLinesColor";
                desc.clearBuffer = false;
                var destination = renderGraph.CreateTexture(desc);
                renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0), "Speed Lines");
                resources.cameraColor = destination;
            }
        }
    }
}
