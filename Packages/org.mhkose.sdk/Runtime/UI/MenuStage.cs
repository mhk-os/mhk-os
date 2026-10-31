using System.Collections.Generic;
using System.Threading;
using MHK.Core;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MHK.UI
{
    /// <summary>
    /// 3D stage of the main menu: a neutral background, the background race (the menu camera stacks on its camera) or
    /// its still picture, faded through a full-view quad, and the turntable that spins the showcased driver and kart.
    /// The showcase has its own lighting over every background: the menu light (the race's lights skip the Menu layer)
    /// and, while the menu camera renders, its own flat ambient instead of the scene's ambient, bake and fog.
    /// </summary>
    public class MenuStage : MonoBehaviour
    {
        [SerializeField, Tooltip("Menu camera. Draws the Menu layer (showcase, fade quad) over the neutral background, or over the race camera's picture while a race shows.")]
        Camera targetCamera;
        [SerializeField, Tooltip("Colour behind the menu while no race or picture shows (the theme's gray-200).")]
        Color neutralColor = new(0.804f, 0.82f, 0.839f);
        [SerializeField, Tooltip("Colour behind the menu when no content can show a race (ShowEmpty), with the showcase light off.")]
        Color emptyColor = new(0.16f, 0.16f, 0.17f);
        [SerializeField, Tooltip("Unlit transparent material (URP Unlit) of the full-view fade quad. Colour and texture are set per frame.")]
        Material fadeMaterial;
        [SerializeField, Tooltip("Distance of the fade quad in front of the menu camera (m): behind the showcase, inside the far clip plane.")]
        float fadeDistance = 60f;
        [SerializeField, Tooltip("Light of the showcase over every background (only the Menu layer: the race's lights never reach the showcase). Off on the empty background.")]
        Light menuLight;
        [SerializeField, Tooltip("Flat ambient light of the showcase over every background (the menu camera's MHK/Lit ambient, no bake, no fog).")]
        Color showcaseAmbient = new(0.86f, 0.74f, 0.66f);
        [SerializeField, Tooltip("Share of the neutral colour laid over the race while the showcase shows, so the driver and kart stand out.")]
        [Range(0f, 1f)] float showcaseVeil = 0.35f;
        [Tooltip("Spun around its up axis (the showcase with the podium top).")]
        public Transform turntable;
        [Tooltip("Turntable spin while playing, degrees per second.")]
        public float spinDegreesPerSecond = 14f;
        [SerializeField, Tooltip("Front bottom edge of the podium relative to the turntable (m, z along the camera's flat view direction). A framed turntable stands with this point on the bottom centre of its area.")]
        Vector3 turntableBase = new(0f, -0.55f, -1.15f);
        [SerializeField, Tooltip("Width and height (m) the podium with driver and kart needs. A framing area too small for it moves the turntable away from the camera, never closer than its place in the scene.")]
        Vector2 turntableExtent = new(3.2f, 2.4f);

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        const float VeilRate = 2f;

        UniversalAdditionalCameraData menuData;
        Camera raceCamera;
        Transform quad;
        Renderer quadRenderer;
        MaterialPropertyBlock block;
        Texture picture;
        float shown, goal, fadeSpeed, veil;
        float fitAspect = -1f, fitFov = -1f;
        Vector3 turntableHome;
        float homeDepth;
        Rect? frame;
        readonly Vector4[] showcaseCube = new Vector4[6];
        readonly List<Vector4> sceneCube = new(6);
        Vector4 sceneBakedIndirect;
        float sceneProbeAmbient;
        bool sceneFog, showcaseLit;
        bool Fading => !Mathf.Approximately(shown, goal);

        void Awake()
        {
            menuData = targetCamera.GetUniversalAdditionalCameraData();
            targetCamera.clearFlags = CameraClearFlags.SolidColor;
            targetCamera.backgroundColor = neutralColor;
            targetCamera.cullingMask = GameLayers.MenuMask;
            // A light meets a camera only where their culling masks overlap: the menu light lights the showcase alone.
            if (menuLight != null) menuLight.cullingMask = GameLayers.MenuMask;
            var ambient = (Vector4)showcaseAmbient.linear;
            for (var i = 0; i < showcaseCube.Length; i++) showcaseCube[i] = ambient;
            block = new MaterialPropertyBlock();
            CreateQuad();
            if (turntable != null)
            {
                turntableHome = turntable.position;
                homeDepth = Vector3.Dot(turntableHome - targetCamera.transform.position, targetCamera.transform.forward);
            }
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += BeforeRender;
            RenderPipelineManager.endCameraRendering += AfterRender;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeRender;
            RenderPipelineManager.endCameraRendering -= AfterRender;
            RestoreSceneLighting();
        }

        void OnDestroy()
        {
            Unstack();
            if (quad != null) Destroy(quad.gameObject);
        }

        /// <summary>
        /// Puts the menu camera on top of <paramref name="race"/> and fades from the neutral background to the race.
        /// The race's lights (every light but the menu light) stop lighting the Menu layer.
        /// </summary>
        public void ShowRace(Camera race, float seconds)
        {
            ShowNeutral();
            Unstack();
            foreach (var light in FindObjectsByType<Light>(FindObjectsInactive.Include))
                if (light != menuLight) light.cullingMask &= ~GameLayers.MenuMask;
            raceCamera = race;
            var stack = race.GetUniversalAdditionalCameraData().cameraStack;
            if (!stack.Contains(targetCamera)) stack.Add(targetCamera);
            menuData.renderType = CameraRenderType.Overlay;
            picture = null;
            shown = 0f;
            FadeTo(1f, seconds);
        }

        /// <summary>Fades from the neutral background to <paramref name="still"/> (a picture of the race).</summary>
        public void ShowPicture(Texture still, float seconds)
        {
            ShowNeutral();
            Unstack();
            picture = still;
            shown = 0f;
            FadeTo(1f, seconds);
        }

        /// <summary>No content to show a race: a dark background without the showcase light, until a race or picture shows.</summary>
        public void ShowEmpty()
        {
            targetCamera.backgroundColor = emptyColor;
            if (menuLight != null) menuLight.enabled = false;
        }

        void ShowNeutral()
        {
            targetCamera.backgroundColor = neutralColor;
            if (menuLight != null) menuLight.enabled = true;
        }

        /// <summary>
        /// Stands the turntable on the bottom centre of <paramref name="viewportArea"/> (viewport units, y up), far enough
        /// away to fit into it. Null puts it back on its place in the scene.
        /// </summary>
        public void FrameTurntable(Rect? viewportArea) => frame = viewportArea;

        /// <summary>Fades back to the neutral background, then takes the menu camera off the race camera.</summary>
        public async Awaitable HideAsync(float seconds, CancellationToken ct)
        {
            FadeTo(0f, seconds);
            while (Fading) await Awaitable.NextFrameAsync(ct);
            Unstack();
            picture = null;
        }

        void FadeTo(float value, float seconds)
        {
            goal = value;
            fadeSpeed = seconds > 0f ? 1f / seconds : float.PositiveInfinity;
        }

        void Unstack()
        {
            if (raceCamera != null) raceCamera.GetUniversalAdditionalCameraData().cameraStack.Remove(targetCamera);
            raceCamera = null;
            if (menuData != null) menuData.renderType = CameraRenderType.Base;
        }

        void CreateQuad()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Fade";
            go.layer = GameLayers.Menu;
            Destroy(go.GetComponent<Collider>());
            quadRenderer = go.GetComponent<MeshRenderer>();
            quadRenderer.sharedMaterial = fadeMaterial;
            quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
            quadRenderer.receiveShadows = false;
            quad = go.transform;
            quad.SetParent(targetCamera.transform, false);
            quad.localPosition = new Vector3(0f, 0f, fadeDistance);
        }

        void LateUpdate()
        {
            var dt = Time.unscaledDeltaTime;
            shown = Mathf.MoveTowards(shown, goal, fadeSpeed * dt);
            if (turntable != null && spinDegreesPerSecond != 0f) turntable.Rotate(0f, spinDegreesPerSecond * dt, 0f, Space.Self);
            var racing = raceCamera != null;
            var showcase = turntable != null && turntable.gameObject.activeInHierarchy;
            veil = Mathf.MoveTowards(veil, showcase ? showcaseVeil : 0f, VeilRate * dt);
            // The quad covers the race with the neutral colour (fade, veil), or shows the picture over it.
            var alpha = racing ? Mathf.Max(1f - shown, veil) : picture != null ? shown : 0f;
            quadRenderer.enabled = alpha > 0f && fadeMaterial != null;
            block.SetColor(BaseColorId, racing ? WithAlpha(neutralColor, alpha) : new Color(1f, 1f, 1f, alpha));
            block.SetTexture(BaseMapId, !racing && picture != null ? picture : Texture2D.whiteTexture);
            quadRenderer.SetPropertyBlock(block);
            targetCamera.enabled = !racing || showcase || quadRenderer.enabled;
        }

        static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, a);

        // Lights the showcase and fits right before rendering: the camera may render to a target with another aspect
        // (resize, captures).
        void BeforeRender(ScriptableRenderContext context, Camera cam)
        {
            if (cam != targetCamera || quad == null) return;
            UseShowcaseLighting();
            if (turntable != null) turntable.position = frame.HasValue ? FramedPosition(frame.Value) : turntableHome;
            if (Mathf.Approximately(fitAspect, cam.aspect) && Mathf.Approximately(fitFov, cam.fieldOfView)) return;
            fitAspect = cam.aspect;
            fitFov = cam.fieldOfView;
            var viewH = 2f * fadeDistance * Mathf.Tan(fitFov * 0.5f * Mathf.Deg2Rad);
            quad.localScale = new Vector3(viewH * fitAspect, viewH, 1f);
        }

        void AfterRender(ScriptableRenderContext context, Camera cam)
        {
            if (cam == targetCamera) RestoreSceneLighting();
        }

        // MHK/Lit's ambient cube path with the showcase colour for the menu camera's draws only (it submits between its
        // begin and end callbacks): the race's ambient probe, bake, baked probes and fog stay off the showcase.
        void UseShowcaseLighting()
        {
            sceneBakedIndirect = Shader.GetGlobalVector(MHKLitIds.BakedIndirect);
            sceneProbeAmbient = Shader.GetGlobalFloat(MHKLitIds.ProbeAmbient);
            Shader.GetGlobalVectorArray(MHKLitIds.AmbientCube, sceneCube);
            sceneFog = RenderSettings.fog;
            Shader.SetGlobalVector(MHKLitIds.BakedIndirect, Vector4.zero);
            Shader.SetGlobalFloat(MHKLitIds.ProbeAmbient, 1f);
            Shader.SetGlobalVectorArray(MHKLitIds.AmbientCube, showcaseCube);
            RenderSettings.fog = false;
            showcaseLit = true;
        }

        // An ambient cube never uploaded yet comes with AmbientCube's first upload.
        void RestoreSceneLighting()
        {
            if (!showcaseLit) return;
            showcaseLit = false;
            Shader.SetGlobalVector(MHKLitIds.BakedIndirect, sceneBakedIndirect);
            Shader.SetGlobalFloat(MHKLitIds.ProbeAmbient, sceneProbeAmbient);
            if (sceneCube.Count > 0) Shader.SetGlobalVectorArray(MHKLitIds.AmbientCube, sceneCube);
            RenderSettings.fog = sceneFog;
        }

        // The podium's front edge stands on the area's bottom, its axis (the pivot) on the area's centre line.
        Vector3 FramedPosition(Rect area)
        {
            var cam = targetCamera.transform;
            var viewHeight = 2f * Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var fitWidth = turntableExtent.x / (area.width * viewHeight * targetCamera.aspect);
            var fitHeight = turntableExtent.y / (area.height * viewHeight);
            var depth = Mathf.Max(homeDepth, Mathf.Max(fitWidth, fitHeight));
            var offset = Quaternion.Euler(0f, cam.eulerAngles.y, 0f) * turntableBase;
            var edgeDepth = depth + Vector3.Dot(offset, cam.forward);
            var pivot = targetCamera.ViewportToWorldPoint(new Vector3(area.center.x, area.yMin, edgeDepth)) - offset;
            var pivotHeight = targetCamera.WorldToViewportPoint(pivot).y;
            return targetCamera.ViewportToWorldPoint(new Vector3(area.center.x, pivotHeight, depth));
        }
    }
}
