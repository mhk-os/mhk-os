using System.Collections.Generic;
using MHK.Core;
using MHK.Race;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MHK.Effects
{
    /// <summary>
    /// Snow, rain or blown sand around the race camera: one GPU-animated quad mesh in a camera-following box (optionally
    /// part of it in a smaller, denser box close to the camera), hidden under roofs by a top-down cover map rendered once at
    /// start, and optionally drop splashes landing on the topmost surfaces of that map. The particles take the scene light,
    /// a share of the point and spot lights (headlight beams) and the lightning flash (see the MHK/Weather shader). One
    /// effect per object: a second particle kind (a sandstorm's dust puffs) goes on a child.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)] // after the camera has moved (ChaseCamera.LateUpdate)
    public class WeatherEffect : MonoBehaviour
    {
        /// <summary>Most particles (or splashes) one effect can have (16-bit indices: four vertices each).</summary>
        public const int MaxParticles = 16000;

        [Tooltip("Camera the weather volume follows (culling bounds, camera motion), empty = the main camera (the race camera). No camera: hidden.")]
        [SerializeField] Camera targetCamera;
        [Tooltip("Material with the MHK/Weather shader (particle texture, scene-light share).")]
        [SerializeField] Material material;

        [Header("Volume")]
        [Tooltip("Particles in the volume at full quality, one quad each (the density: area and count together).")]
        [Range(1, MaxParticles)] [SerializeField] int count = 1500;
        [Tooltip("Size of the box around the camera holding the particles (m). Larger = visible further, sparser for the same count.")]
        [SerializeField] Vector3 area = new(30f, 16f, 30f);
        [Tooltip("Box centre relative to the camera (m): x right, y up, z forward along the camera's heading.")]
        [SerializeField] Vector3 center = new(0f, 3f, 8f);
        [Tooltip("Border of the box in which particles fade in and out, as a fraction of its half size.")]
        [Range(0.05f, 1f)] [SerializeField] float edgeFade = 0.35f;
        [Tooltip("Particles nearer to the camera than about this (m) fade out, so none blot the view.")]
        [Min(0.01f)] [SerializeField] float nearFade = 1f;
        [Tooltip("Share of the particles in a smaller box close to the camera (the volume and its centre offset divided by " +
                 "nearDivisor), so the weather is densest where it is seen best. 0 = one evenly filled box.")]
        [Range(0f, 1f)] [SerializeField] float nearShare;
        [Tooltip("The near box is the volume divided by this along each axis (a whole number keeps the motion seamless).")]
        [Range(1, 8)] [SerializeField] int nearDivisor = 2;

        [Header("Motion")]
        [Tooltip("Falling speed (m/s).")]
        [SerializeField] float fallSpeed = 1.2f;
        [Tooltip("Per-particle speed spread: speeds range over fallSpeed (and wind) × (1 ± this).")]
        [Range(0f, 0.9f)] [SerializeField] float speedVariation = 0.3f;
        [Tooltip("Wind (m/s, world axes) added to the fall, scaled with each particle's speed.")]
        [SerializeField] Vector3 wind = new(0.4f, 0f, 0.2f);
        [Tooltip("Drift: radius (m) of each particle's small circling motion (snow), 0 = straight fall.")]
        [Min(0f)] [SerializeField] float swayAmplitude = 0.25f;
        [Tooltip("Drift circles per second (each particle 0.75 to 1.25 × this).")]
        [Min(0f)] [SerializeField] float swayFrequency = 0.5f;

        [Header("Look")]
        [Tooltip("Colour and opacity (multiplies the material's texture).")]
        [SerializeField] Color color = new(1f, 1f, 1f, 0.85f);
        [Tooltip("Quad width and length (m). Length above width = a streak along the motion (rain), else a camera-facing flake.")]
        [SerializeField] Vector2 size = new(0.06f, 0.06f);
        [Tooltip("Per-particle size spread: size × (1 ± this).")]
        [Range(0f, 0.9f)] [SerializeField] float sizeVariation = 0.3f;
        [Tooltip("Streaks only: share of the camera's own motion in the streak direction (rain slants towards a moving camera).")]
        [Range(0f, 1f)] [SerializeField] float cameraMotionStretch = 0.3f;

        [Header("Splashes (land on the cover's topmost surfaces: need cover)")]
        [Tooltip("Drop splashes on the ground around the camera at full quality, 0 = none (snow).")]
        [Range(0, MaxParticles)] [SerializeField] int splashCount;
        [Tooltip("Side of the square around the camera the splashes land in (m).")]
        [Min(1f)] [SerializeField] float splashArea = 24f;
        [Tooltip("Centre of that square ahead of the camera along its heading (m).")]
        [SerializeField] float splashAhead = 6f;
        [Tooltip("Life of one splash (s); each splash lands somewhere else the next time.")]
        [Min(0.05f)] [SerializeField] float splashLifetime = 0.35f;
        [Tooltip("Splash quad width and height (m), varied by sizeVariation.")]
        [SerializeField] Vector2 splashSize = new(0.4f, 0.25f);
        [Tooltip("Splash opacity relative to the colour's.")]
        [Range(0f, 2f)] [SerializeField] float splashOpacity = 1f;

        [Header("Cover (roofs, tunnels)")]
        [Tooltip("Objects whose topmost opaque surfaces stop the particles. Empty and no coverTrackVisual = no cover: it falls everywhere.")]
        [SerializeField] Transform[] cover;
        [Tooltip("Also stop the particles under the loaded track's Visual group (TrackRuntime.Current), which an overlay scene " +
                 "cannot reference.")]
        [SerializeField] bool coverTrackVisual;
        [Tooltip("Cover map texel size (m) over the covered area's ground plan.")]
        [Min(0.1f)] [SerializeField] float coverTexel = 1f;
        [Tooltip("Largest cover map side (texels), larger tracks get coarser texels. 1024² = 2 MB.")]
        [Range(64, 2048)] [SerializeField] int coverMaxResolution = 1024;
        [Tooltip("Particles disappear this far (m) above the topmost surface...")]
        [SerializeField] float coverBias = 0.1f;
        [Tooltip("...fading in over this height (m) above that.")]
        [Min(0.01f)] [SerializeField] float coverFade = 0.5f;
        [Tooltip("Alpha-tested (cut-out) materials such as foliage count as cover too (as solid quads).")]
        [SerializeField] bool coverIncludesCutout;
        [Tooltip("Transparent surfaces (water, ice) count as cover, so nothing shows below them. Sky materials (queue below Geometry) never do.")]
        [SerializeField] bool coverIncludesTransparent = true;

        [Header("Quality")]
        [Tooltip("Share of count (and splashCount) drawn per graphics render-resolution tier: [0] 720p or lower (Low preset), " +
                 "[1] 1080p (Medium), [2] native (High). 0 = off at that tier.")]
        [SerializeField] float[] qualityScale = { 0.5f, 1f, 1f };

        const string SplashKeyword = "_WEATHER_SPLASH";

        static readonly int AreaId = Shader.PropertyToID("_WeatherArea");
        static readonly int CenterId = Shader.PropertyToID("_WeatherCenter");
        static readonly int OffsetId = Shader.PropertyToID("_WeatherOffset");
        static readonly int VelocityId = Shader.PropertyToID("_WeatherVelocity");
        static readonly int CameraVelocityId = Shader.PropertyToID("_WeatherCameraVelocity");
        static readonly int SizeId = Shader.PropertyToID("_WeatherSize");
        static readonly int NearId = Shader.PropertyToID("_WeatherNear");
        static readonly int SplashId = Shader.PropertyToID("_WeatherSplash");
        static readonly int SplashSizeId = Shader.PropertyToID("_WeatherSplashSize");
        static readonly int CoverMapId = Shader.PropertyToID("_MHKWeatherCover");
        static readonly int CoverRectId = Shader.PropertyToID("_WeatherCoverRect");
        static readonly int CoverHeightId = Shader.PropertyToID("_WeatherCoverHeight");
        static readonly int CoverOnId = Shader.PropertyToID("_WeatherCoverOn");
        static readonly int CoverEncodeId = Shader.PropertyToID("_WeatherCoverEncode");

        // Shader coupling: speed multipliers are multiples of 1/SpeedSteps (the displacement wraps at that many boxes
        // without a jump, also in a near box of a whole fraction of the size), sway frequencies multiples of 1/8 (the sway
        // phase wraps at 8 turns) and a splash's landing spots repeat after SplashCycles lives (its clock wraps there).
        const int SpeedSteps = 64;
        const float SwayPeriod = Mathf.PI * 2f * 8f;
        const int SplashCycles = 4096;
        const float CameraCutSpeed = 120f;
        const float CameraVelocitySharpness = 10f;
        const float BoundsPadding = 1f;
        const int CoverMinResolution = 16;
        const float CoverDepthPadding = 1f;

        Mesh mesh, splashMesh;
        MeshFilter meshFilter, splashFilter;
        MeshRenderer meshRenderer, splashRenderer;
        Material splashMaterial;
        int drawn = -1, splashDrawn = -1;
        MaterialPropertyBlock block;
        Vector3 offset;
        float swayPhase, splashClock;
        Camera lastCamera;
        Vector3 lastCameraPos;
        Vector3 cameraVelocity;
        RenderTexture coverMap;
        Vector4 coverRect, coverHeight;
        // The track whose Visual group the cover map holds (coverTrackVisual).
        TrackRuntime coverTrack;

        void Awake()
        {
            block = new MaterialPropertyBlock();
            meshRenderer = CreateRenderer("Weather particles", out meshFilter);
            splashRenderer = CreateRenderer("Weather splashes", out splashFilter);
            BuildMeshes();
        }

        // Plain renderers (not Graphics.RenderMesh), so manual Camera.Render calls (screenshots) include them too.
        MeshRenderer CreateRenderer(string childName, out MeshFilter filter)
        {
            var child = new GameObject(childName) { hideFlags = HideFlags.DontSave, layer = gameObject.layer };
            child.transform.SetParent(transform, false);
            filter = child.AddComponent<MeshFilter>();
            var r = child.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic = false;
            r.enabled = false;
            return r;
        }

        void Start() => BuildCover();

        void OnDisable()
        {
            if (meshRenderer != null) Hide();
        }

        void OnDestroy()
        {
            DestroyMeshes();
            if (splashMaterial != null) Destroy(splashMaterial);
            ReleaseCover();
        }

        void BuildMeshes()
        {
            mesh = BuildQuads(Mathf.Clamp(count, 1, MaxParticles), " particles");
            meshFilter.sharedMesh = mesh;
            splashMesh = splashCount > 0 ? BuildQuads(splashCount, " splashes") : null;
            splashFilter.sharedMesh = splashMesh;
            drawn = splashDrawn = -1;
        }

        void DestroyMeshes()
        {
            if (mesh != null) Destroy(mesh);
            if (splashMesh != null) Destroy(splashMesh);
        }

        Mesh BuildQuads(int n, string suffix)
        {
            var rng = new System.Random(n);
            var positions = new Vector3[n * 4];
            var uvs = new Vector4[n * 4];
            var indices = new ushort[n * 6];
            for (var i = 0; i < n; i++)
            {
                var seed = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
                var k = 1f + speedVariation * ((float)rng.NextDouble() * 2f - 1f);
                k = Mathf.Max(1, Mathf.RoundToInt(k * SpeedSteps)) / (float)SpeedSteps;
                var r = (float)rng.NextDouble();
                for (var c = 0; c < 4; c++)
                {
                    positions[i * 4 + c] = seed;
                    uvs[i * 4 + c] = new Vector4(c is 0 or 3 ? -1f : 1f, c < 2 ? -1f : 1f, k, r);
                }
                WriteQuadIndices(indices, i);
            }
            var quads = new Mesh { name = name + suffix, hideFlags = HideFlags.DontSave };
            quads.vertices = positions;
            quads.SetUVs(0, uvs);
            quads.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
            quads.SetIndexBufferData(indices, 0, 0, indices.Length, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            quads.subMeshCount = 1;
            quads.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length), MeshUpdateFlags.DontRecalculateBounds);
            quads.bounds = new Bounds(Vector3.one * 0.5f, Vector3.one);
            return quads;
        }

        static void WriteQuadIndices(ushort[] indices, int quad)
        {
            var v = (ushort)(quad * 4);
            indices[quad * 6] = v;
            indices[quad * 6 + 1] = (ushort)(v + 1);
            indices[quad * 6 + 2] = (ushort)(v + 2);
            indices[quad * 6 + 3] = v;
            indices[quad * 6 + 4] = (ushort)(v + 2);
            indices[quad * 6 + 5] = (ushort)(v + 3);
        }

        // How many of the built quads to draw for wanted (count or splashCount) at the current graphics tier.
        int WantedCount(int wanted, Mesh quads)
        {
            if (quads == null) return 0;
            var built = quads.vertexCount / 4;
            if (qualityScale == null || qualityScale.Length == 0) return Mathf.Min(wanted, built);
            var tier = Mathf.Min(GraphicsQuality.ResolutionTier, qualityScale.Length - 1);
            return Mathf.Clamp(Mathf.RoundToInt(wanted * Mathf.Max(0f, qualityScale[tier])), 0, built);
        }

        void LateUpdate()
        {
            if (material == null || mesh == null) return;
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam == null || !cam.isActiveAndEnabled)
            {
                lastCamera = null;
                Hide();
                return;
            }
            if (coverTrackVisual && coverTrack != TrackRuntime.Current) BuildCover(); // the track loaded after Start
            SyncDrawnCount();
            var dt = Time.deltaTime;
            var velocity = AdvanceMotion(dt);
            var ct = cam.transform;
            TrackCameraVelocity(cam, ct.position, dt);
            if (drawn <= 0)
            {
                Hide();
                return;
            }
            if (coverMap != null && !coverMap.IsCreated()) BuildCover(); // contents lost (mobile app switch)
            UploadParameters(velocity);
            var heading = FitBounds(ct);
            UpdateSplashes(ct.position, heading);
        }

        void Hide()
        {
            meshRenderer.enabled = false;
            splashRenderer.enabled = false;
        }

        void SyncDrawnCount()
        {
            drawn = SetDrawn(mesh, WantedCount(count, mesh), drawn);
            splashDrawn = SetDrawn(splashMesh, WantedCount(splashCount, splashMesh), splashDrawn);
        }

        static int SetDrawn(Mesh quads, int wanted, int current)
        {
            if (wanted == current) return current;
            if (wanted > 0) quads.SetSubMesh(0, new SubMeshDescriptor(0, wanted * 6), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            return wanted;
        }

        Vector3 AdvanceMotion(float dt)
        {
            var velocity = new Vector3(wind.x, wind.y - fallSpeed, wind.z);
            offset += velocity * dt;
            offset.x = Mathf.Repeat(offset.x, Mathf.Max(0.01f, area.x) * SpeedSteps);
            offset.y = Mathf.Repeat(offset.y, Mathf.Max(0.01f, area.y) * SpeedSteps);
            offset.z = Mathf.Repeat(offset.z, Mathf.Max(0.01f, area.z) * SpeedSteps);
            swayPhase = Mathf.Repeat(swayPhase + dt * swayFrequency * Mathf.PI * 2f, SwayPeriod);
            splashClock = Mathf.Repeat(splashClock + dt, splashLifetime * SplashCycles);
            return velocity;
        }

        void TrackCameraVelocity(Camera cam, Vector3 camPos, float dt)
        {
            if (cam != lastCamera) cameraVelocity = Vector3.zero;
            else if (dt > 0f)
            {
                var v = (camPos - lastCameraPos) / dt;
                if (v.sqrMagnitude > CameraCutSpeed * CameraCutSpeed) v = cameraVelocity;
                cameraVelocity = Vector3.Lerp(cameraVelocity, v, Smoothing.Factor(CameraVelocitySharpness, dt));
            }
            lastCamera = cam;
            lastCameraPos = camPos;
        }

        void UploadParameters(Vector3 velocity)
        {
            var streak = size.y > size.x;
            block.SetVector(AreaId, new Vector4(area.x, area.y, area.z, 1f / edgeFade));
            block.SetVector(CenterId, new Vector4(center.x, center.y, center.z, nearFade));
            block.SetVector(OffsetId, new Vector4(offset.x, offset.y, offset.z, swayPhase));
            block.SetVector(VelocityId, new Vector4(velocity.x, velocity.y, velocity.z, swayAmplitude));
            block.SetVector(CameraVelocityId, cameraVelocity * cameraMotionStretch);
            block.SetVector(SizeId, new Vector4(size.x, size.y, sizeVariation, streak ? 1f : 0f));
            block.SetVector(NearId, new Vector4(nearShare, 1f / nearDivisor, 0f, 0f));
            block.SetColor(MHKLitIds.BaseColor, color);
            block.SetFloat(CoverOnId, coverMap != null ? 1f : 0f);
            if (coverMap == null) return;
            block.SetTexture(CoverMapId, coverMap);
            block.SetVector(CoverRectId, coverRect);
            block.SetVector(CoverHeightId, coverHeight);
        }

        // Returns the camera's heading on the ground plane.
        Vector3 FitBounds(Transform ct)
        {
            var f = ct.forward;
            var heading = new Vector3(f.x, 0f, f.z);
            heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.forward;
            var bounds = BoxBounds(ct.position, heading, 1f);
            if (nearShare > 0f) bounds.Encapsulate(BoxBounds(ct.position, heading, 1f / nearDivisor));
            bounds.Expand(2f * (Mathf.Max(size.x, size.y) * (1f + sizeVariation) + swayAmplitude + BoundsPadding));
            meshRenderer.sharedMaterial = material;
            meshRenderer.SetPropertyBlock(block);
            meshRenderer.bounds = bounds;
            meshRenderer.enabled = true;
            return heading;
        }

        // The particle box (the shader's WeatherBoxCenter): the volume, or the near box at a scale of 1 / nearDivisor.
        Bounds BoxBounds(Vector3 camPos, Vector3 heading, float scale)
        {
            var c = center * scale;
            return new Bounds(camPos + new Vector3(heading.z, 0f, -heading.x) * c.x + Vector3.up * c.y + heading * c.z, area * scale);
        }

        // Splashes stand on the cover map's surfaces, so they need one. They reuse the particles' block values (fades,
        // size variation, cover) with their own colour and square.
        void UpdateSplashes(Vector3 camPos, Vector3 heading)
        {
            if (splashDrawn <= 0 || coverMap == null)
            {
                splashRenderer.enabled = false;
                return;
            }
            if (splashMaterial == null)
            {
                splashMaterial = new Material(material) { name = material.name + " splashes", hideFlags = HideFlags.DontSave };
                splashMaterial.EnableKeyword(SplashKeyword);
            }
            var splashColor = color;
            splashColor.a *= splashOpacity;
            block.SetColor(MHKLitIds.BaseColor, splashColor);
            block.SetVector(SplashId, new Vector4(splashArea, splashAhead, splashLifetime, splashClock));
            block.SetVector(SplashSizeId, new Vector4(splashSize.x, splashSize.y, 0f, 0f));
            var square = camPos + heading * splashAhead;
            var margin = 2f * (Mathf.Max(splashSize.x, splashSize.y) * (1f + sizeVariation) + BoundsPadding);
            splashRenderer.sharedMaterial = splashMaterial;
            splashRenderer.SetPropertyBlock(block);
            splashRenderer.bounds = new Bounds(new Vector3(square.x, coverHeight.x + coverHeight.y * 0.5f, square.z),
                new Vector3(splashArea + margin, coverHeight.y + margin, splashArea + margin));
            splashRenderer.enabled = true;
        }

        // Height of the topmost opaque surface of cover over its ground plan, seen from above with an orthographic
        // projection (the depth test keeps the highest surface).
        void BuildCover()
        {
            ReleaseCover();
            coverTrack = coverTrackVisual ? TrackRuntime.Current : null;
            if (material == null) return;
            var pass = material.FindPass("Cover");
            if (pass < 0) return;
            var parts = new List<(Renderer renderer, int submesh)>();
            if (!CollectCoverParts(parts, out var bounds)) return;
            var sizeXZ = new Vector2(Mathf.Max(1f, bounds.size.x), Mathf.Max(1f, bounds.size.z));
            coverMap = CreateCoverTarget(sizeXZ);
            RenderCover(parts, bounds, sizeXZ, pass);
        }

        bool CollectCoverParts(List<(Renderer renderer, int submesh)> parts, out Bounds bounds)
        {
            var renderers = new List<MeshRenderer>();
            var materials = new List<Material>();
            bounds = new Bounds();
            var any = false;
            if (cover != null)
                foreach (var root in cover)
                    AddCoverParts(root, parts, renderers, materials, ref bounds, ref any);
            if (coverTrack != null)
                AddCoverParts(coverTrack.transform.Find(TrackRuntime.VisualGroup), parts, renderers, materials, ref bounds, ref any);
            return any;
        }

        void AddCoverParts(Transform root, List<(Renderer renderer, int submesh)> parts, List<MeshRenderer> renderers,
            List<Material> materials, ref Bounds bounds, ref bool any)
        {
            if (root == null) return;
            root.GetComponentsInChildren(false, renderers);
            foreach (var r in renderers)
            {
                if (!r.enabled || r.forceRenderingOff || !r.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) continue;
                r.GetSharedMaterials(materials);
                var added = false;
                for (var i = 0; i < materials.Count && i < filter.sharedMesh.subMeshCount; i++)
                {
                    if (!IsCoverMaterial(materials[i])) continue;
                    parts.Add((r, i));
                    added = true;
                }
                if (!added) continue;
                if (any) bounds.Encapsulate(r.bounds);
                else bounds = r.bounds;
                any = true;
            }
        }

        bool IsCoverMaterial(Material m)
        {
            if (m == null) return false;
            var queue = m.renderQueue;
            if (queue < (int)RenderQueue.Geometry) return false;
            if (queue >= (int)RenderQueue.Transparent) return coverIncludesTransparent;
            return queue < (int)RenderQueue.AlphaTest || coverIncludesCutout;
        }

        RenderTexture CreateCoverTarget(Vector2 sizeXZ)
        {
            var res = new Vector2Int(
                Mathf.Clamp(Mathf.CeilToInt(sizeXZ.x / coverTexel), CoverMinResolution, coverMaxResolution),
                Mathf.Clamp(Mathf.CeilToInt(sizeXZ.y / coverTexel), CoverMinResolution, coverMaxResolution));
            var format = SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.Render)
                ? GraphicsFormat.R16_SFloat
                : GraphicsFormat.R8G8B8A8_UNorm;
            var target = new RenderTexture(new RenderTextureDescriptor(res.x, res.y, format, GraphicsFormat.D24_UNorm_S8_UInt))
            {
                name = name + " cover",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            target.Create();
            return target;
        }

        void RenderCover(List<(Renderer renderer, int submesh)> parts, Bounds bounds, Vector2 sizeXZ, int pass)
        {
            var minY = bounds.min.y - CoverDepthPadding;
            var range = bounds.size.y + 2f * CoverDepthPadding;
            var eye = new Vector3(bounds.center.x, minY + range + CoverDepthPadding, bounds.center.z);
            var view = Matrix4x4.TRS(eye, Quaternion.Euler(90f, 0f, 0f), new Vector3(1f, 1f, -1f)).inverse;
            var proj = Matrix4x4.Ortho(-sizeXZ.x * 0.5f, sizeXZ.x * 0.5f, -sizeXZ.y * 0.5f, sizeXZ.y * 0.5f, 0.5f * CoverDepthPadding, range + 1.5f * CoverDepthPadding);
            var cmd = new CommandBuffer { name = "MHK Weather cover" };
            cmd.SetRenderTarget(coverMap);
            cmd.ClearRenderTarget(true, true, Color.clear);
            cmd.SetViewProjectionMatrices(view, proj);
            cmd.SetGlobalVector(CoverEncodeId, new Vector4(minY, 1f / range, 0f, 0f));
            foreach (var (r, submesh) in parts) cmd.DrawRenderer(r, material, submesh, pass);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
            coverRect = new Vector4(bounds.center.x - sizeXZ.x * 0.5f, bounds.center.z - sizeXZ.y * 0.5f, 1f / sizeXZ.x, 1f / sizeXZ.y);
            coverHeight = new Vector4(minY, range, coverBias, 1f / coverFade);
        }

        void ReleaseCover()
        {
            if (coverMap == null) return;
            coverMap.Release();
            Destroy(coverMap);
            coverMap = null;
        }

        void OnValidate()
        {
            area = Vector3.Max(area, Vector3.one);
            if (!Application.isPlaying || mesh == null) return;
            var splashes = splashMesh != null ? splashMesh.vertexCount / 4 : 0;
            if (mesh.vertexCount / 4 == Mathf.Clamp(count, 1, MaxParticles) && splashes == splashCount) return;
            DestroyMeshes();
            BuildMeshes();
        }
    }
}
