using System.Collections.Generic;
using MHK.Core;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace MHK.Effects
{
    /// <summary>
    /// Snow or rain around the race camera: one GPU-animated quad mesh in a camera-following box, hidden under roofs by
    /// a top-down cover map rendered once at start.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)] // after the camera has moved (ChaseCamera.LateUpdate)
    public class WeatherEffect : MonoBehaviour
    {
        /// <summary>Most particles one effect can have (16-bit indices: four vertices each).</summary>
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

        [Header("Cover (roofs, tunnels)")]
        [Tooltip("Objects whose topmost opaque surfaces stop the particles (the track's Visual group). Empty = no cover: it falls everywhere.")]
        [SerializeField] Transform[] cover;
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
        [Tooltip("Share of count drawn per graphics render-resolution tier: [0] 720p or lower (Low preset), [1] 1080p (Medium), " +
                 "[2] native (High). 0 = off at that tier.")]
        [SerializeField] float[] qualityScale = { 0.5f, 1f, 1f };

        static readonly int AreaId = Shader.PropertyToID("_WeatherArea");
        static readonly int CenterId = Shader.PropertyToID("_WeatherCenter");
        static readonly int OffsetId = Shader.PropertyToID("_WeatherOffset");
        static readonly int VelocityId = Shader.PropertyToID("_WeatherVelocity");
        static readonly int CameraVelocityId = Shader.PropertyToID("_WeatherCameraVelocity");
        static readonly int SizeId = Shader.PropertyToID("_WeatherSize");
        static readonly int CoverMapId = Shader.PropertyToID("_MHKWeatherCover");
        static readonly int CoverRectId = Shader.PropertyToID("_WeatherCoverRect");
        static readonly int CoverHeightId = Shader.PropertyToID("_WeatherCoverHeight");
        static readonly int CoverOnId = Shader.PropertyToID("_WeatherCoverOn");
        static readonly int CoverEncodeId = Shader.PropertyToID("_WeatherCoverEncode");

        // Shader coupling: speed multipliers are multiples of 1/SpeedSteps (the displacement wraps at that many boxes
        // without a jump) and sway frequencies multiples of 1/8 (the sway phase wraps at 8 turns).
        const int SpeedSteps = 64;
        const float SwayPeriod = Mathf.PI * 2f * 8f;
        const float CameraCutSpeed = 120f;
        const float CameraVelocitySharpness = 10f;
        const float BoundsPadding = 1f;
        const int CoverMinResolution = 16;
        const float CoverDepthPadding = 1f;

        Mesh mesh;
        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        int drawn = -1;
        MaterialPropertyBlock block;
        Vector3 offset;
        float swayPhase;
        Camera lastCamera;
        Vector3 lastCameraPos;
        Vector3 cameraVelocity;
        RenderTexture coverMap;
        Vector4 coverRect, coverHeight;

        void Awake()
        {
            block = new MaterialPropertyBlock();
            BuildMesh();
            CreateRenderer();
        }

        // A plain renderer (not Graphics.RenderMesh), so manual Camera.Render calls (screenshots) include it too.
        void CreateRenderer()
        {
            var child = new GameObject("Weather particles") { hideFlags = HideFlags.DontSave, layer = gameObject.layer };
            child.transform.SetParent(transform, false);
            meshFilter = child.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = mesh;
            meshRenderer = child.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            meshRenderer.allowOcclusionWhenDynamic = false;
            meshRenderer.enabled = false;
        }

        void Start() => BuildCover();

        void OnDisable()
        {
            if (meshRenderer != null) meshRenderer.enabled = false;
        }

        void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
            ReleaseCover();
        }

        void BuildMesh()
        {
            var n = Mathf.Clamp(count, 1, MaxParticles);
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
            mesh = new Mesh { name = name + " particles", hideFlags = HideFlags.DontSave };
            mesh.vertices = positions;
            mesh.SetUVs(0, uvs);
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length), MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = new Bounds(Vector3.one * 0.5f, Vector3.one);
            drawn = -1;
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

        int WantedCount()
        {
            if (qualityScale == null || qualityScale.Length == 0) return count;
            var tier = Mathf.Min(GraphicsQuality.ResolutionTier, qualityScale.Length - 1);
            return Mathf.Clamp(Mathf.RoundToInt(count * Mathf.Max(0f, qualityScale[tier])), 0, mesh.vertexCount / 4);
        }

        void LateUpdate()
        {
            if (material == null || mesh == null) return;
            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (cam == null || !cam.isActiveAndEnabled)
            {
                lastCamera = null;
                meshRenderer.enabled = false;
                return;
            }
            SyncDrawnCount();
            var dt = Time.deltaTime;
            var velocity = AdvanceMotion(dt);
            var ct = cam.transform;
            TrackCameraVelocity(cam, ct.position, dt);
            if (drawn <= 0)
            {
                meshRenderer.enabled = false;
                return;
            }
            if (coverMap != null && !coverMap.IsCreated()) BuildCover(); // contents lost (mobile app switch)
            UploadParameters(velocity);
            FitBounds(ct);
        }

        void SyncDrawnCount()
        {
            var wanted = WantedCount();
            if (wanted == drawn) return;
            drawn = wanted;
            if (drawn > 0) mesh.SetSubMesh(0, new SubMeshDescriptor(0, drawn * 6), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        Vector3 AdvanceMotion(float dt)
        {
            var velocity = new Vector3(wind.x, wind.y - fallSpeed, wind.z);
            offset += velocity * dt;
            offset.x = Mathf.Repeat(offset.x, Mathf.Max(0.01f, area.x) * SpeedSteps);
            offset.y = Mathf.Repeat(offset.y, Mathf.Max(0.01f, area.y) * SpeedSteps);
            offset.z = Mathf.Repeat(offset.z, Mathf.Max(0.01f, area.z) * SpeedSteps);
            swayPhase = Mathf.Repeat(swayPhase + dt * swayFrequency * Mathf.PI * 2f, SwayPeriod);
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
            block.SetColor(MHKLitIds.BaseColor, color);
            block.SetFloat(CoverOnId, coverMap != null ? 1f : 0f);
            if (coverMap == null) return;
            block.SetTexture(CoverMapId, coverMap);
            block.SetVector(CoverRectId, coverRect);
            block.SetVector(CoverHeightId, coverHeight);
        }

        void FitBounds(Transform ct)
        {
            var f = ct.forward;
            var heading = new Vector3(f.x, 0f, f.z);
            heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : Vector3.forward;
            var boxCenter = ct.position + new Vector3(heading.z, 0f, -heading.x) * center.x + Vector3.up * center.y + heading * center.z;
            var margin = Mathf.Max(size.x, size.y) * (1f + sizeVariation) + swayAmplitude + BoundsPadding;
            meshRenderer.sharedMaterial = material;
            meshRenderer.SetPropertyBlock(block);
            meshRenderer.bounds = new Bounds(boxCenter, area + Vector3.one * (2f * margin));
            meshRenderer.enabled = true;
        }

        // Height of the topmost opaque surface of cover over its ground plan, seen from above with an orthographic
        // projection (the depth test keeps the highest surface).
        void BuildCover()
        {
            ReleaseCover();
            if (cover == null || cover.Length == 0 || material == null) return;
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
            foreach (var root in cover)
            {
                if (root == null) continue;
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
            return any;
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
            if (!Application.isPlaying || mesh == null || mesh.vertexCount / 4 == Mathf.Clamp(count, 1, MaxParticles)) return;
            Destroy(mesh);
            BuildMesh();
            if (meshFilter != null) meshFilter.sharedMesh = mesh;
        }
    }
}
