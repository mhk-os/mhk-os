using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MHK.UI
{
    /// <summary>
    /// Blurred copy of the main camera's picture at a quarter of its size (<see cref="Texture"/>), rendered once per
    /// frame by a render-graph pass after post-processing while glass plates are on screen (<see cref="Acquire"/>).
    /// </summary>
    public static partial class UiGlassBlur
    {
        const int DownLevels = 4;
        const int OutputShift = 2;
        const int DownPass = 0;
        const int UpPass = 1;

        [AutoStaticsCleanup] static int users;
        [AutoStaticsCleanup] static Material material;
        [AutoStaticsCleanup] static RenderTexture texture;
        [AutoStaticsCleanup] static RTHandle textureHandle;
        [AutoStaticsCleanup] static Pass pass;
        [AutoStaticsCleanup] static int version;
        [AutoStaticsCleanup] static Camera dataCamera;
        [AutoStaticsCleanup] static UniversalAdditionalCameraData cameraData;

        /// <summary>The blurred picture, null while no glass plate is on screen or the shader is missing.</summary>
        public static RenderTexture Texture => texture;

        /// <summary>Changes whenever <see cref="Texture"/> is replaced (window resized).</summary>
        public static int Version => version;

        /// <summary>A glass plate came on screen: blur from now on.</summary>
        public static void Acquire()
        {
            if (users++ > 0) return;
            var root = GameRoot.Instance;
            var shader = root != null ? root.Config.uiGlassShader : null;
            if (shader == null || !shader.isSupported) return;
            material = CoreUtils.CreateEngineMaterial(shader);
            pass = new Pass(material);
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        /// <summary>A glass plate left the screen: stop blurring when it was the last.</summary>
        public static void Release()
        {
            if (users <= 0 || --users > 0) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            ReleaseTexture();
            CoreUtils.Destroy(material);
            material = null;
            pass = null;
        }

        // Every base game camera that draws to the screen. Several of them overwrite the texture in rendering order, so the
        // last (topmost) one wins.
        static void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (pass == null || cam.cameraType != CameraType.Game || cam.targetTexture != null || UiGlass.Lite) return;
            if (cam != dataCamera)
            {
                dataCamera = cam;
                cameraData = cam.GetUniversalAdditionalCameraData();
            }
            if (cameraData == null || cameraData.renderType != CameraRenderType.Base || cameraData.scriptableRenderer == null) return;
            EnsureTexture(cam.pixelWidth >> OutputShift, cam.pixelHeight >> OutputShift);
            pass.Target = textureHandle;
            cameraData.scriptableRenderer.EnqueuePass(pass);
        }

        static void EnsureTexture(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (texture != null && texture.width == width && texture.height == height) return;
            ReleaseTexture();
            texture = new RenderTexture(width, height, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.None)
            {
                name = "UiGlassBlur",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            texture.Create();
            textureHandle = RTHandles.Alloc(texture);
            version++;
        }

        static void ReleaseTexture()
        {
            textureHandle?.Release();
            textureHandle = null;
            if (texture != null) Object.Destroy(texture);
            texture = null;
            version++;
        }

        // Halves the camera colour DownLevels times, then doubles it back up into the persistent quarter-size texture.
        sealed class Pass : ScriptableRenderPass
        {
            static readonly string[] LevelNames = { "_UiGlass1", "_UiGlass2", "_UiGlass3", "_UiGlass4" };

            readonly Material material;
            readonly TextureHandle[] levels = new TextureHandle[DownLevels];

            public RTHandle Target { get; set; }

            public Pass(Material material)
            {
                this.material = material;
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || Target == null) return;
                var source = resources.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(source);
                desc.clearBuffer = false;
                desc.msaaSamples = MSAASamples.None;
                desc.depthBufferBits = DepthBits.None;
                desc.filterMode = FilterMode.Bilinear;
                desc.wrapMode = TextureWrapMode.Clamp;
                var previous = source;
                for (var i = 0; i < DownLevels; i++)
                {
                    desc.width = Mathf.Max(1, desc.width / 2);
                    desc.height = Mathf.Max(1, desc.height / 2);
                    desc.name = LevelNames[i];
                    levels[i] = renderGraph.CreateTexture(desc);
                    renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(previous, levels[i], material, DownPass), "UI Glass Down");
                    previous = levels[i];
                }
                for (var i = DownLevels - 2; i >= OutputShift; i--)
                {
                    renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(previous, levels[i], material, UpPass), "UI Glass Up");
                    previous = levels[i];
                }
                var output = renderGraph.ImportTexture(Target);
                renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(previous, output, material, UpPass), "UI Glass Up");
            }
        }
    }
}
