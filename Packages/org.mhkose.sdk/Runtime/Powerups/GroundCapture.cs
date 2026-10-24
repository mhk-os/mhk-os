using System;
using System.Collections.Generic;
using MHK.Karts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace MHK.Powerups
{
    /// <summary>
    /// Unlit top-down picture of the ground over an area, rendered once by a temporary orthographic camera. Geometry
    /// laid on the ground uses it as texture via <see cref="UvAt"/>.
    /// </summary>
    public sealed partial class GroundCapture
    {
        const float DepthMargin = 3f;
        const float MinTexel = 0.005f;
        const int MinSize = 16;
        const int DepthBits = 24;

        static readonly int EmissionId = Shader.PropertyToID("_Emission");
        [AutoStaticsCleanup] static MaterialPropertyBlock unlitBlock;

        public RenderTexture Texture { get; private set; }

        Vector3 centre, axisX, axisZ;
        float sizeX, sizeZ, depth;

        /// <summary>Texture coordinate of a world point (planar projection along the vertical).</summary>
        public Vector2 UvAt(Vector3 world)
        {
            var d = world - centre;
            return new Vector2(Vector3.Dot(d, axisX) / sizeX + 0.5f, Vector3.Dot(d, axisZ) / sizeZ + 0.5f);
        }

        /// <summary>True when <paramref name="world"/> lies at least <paramref name="inset"/> metres inside the captured area (horizontally).</summary>
        public bool Contains(Vector3 world, float inset)
        {
            var d = world - centre;
            return Mathf.Abs(Vector3.Dot(d, axisX)) <= sizeX * 0.5f - inset && Mathf.Abs(Vector3.Dot(d, axisZ)) <= sizeZ * 0.5f - inset;
        }

        /// <summary>Frees the texture.</summary>
        public void Release()
        {
            if (Texture == null) return;
            Texture.Release();
            Object.Destroy(Texture);
            Texture = null;
        }

        /// <summary>
        /// Renders the ground under <paramref name="points"/> (the highest one sets the clipping height) plus
        /// <paramref name="margin"/> metres on every side, in a box aligned with the horizontal direction
        /// <paramref name="axis"/>. Null when there is nothing to capture.
        /// </summary>
        public static GroundCapture Render(IReadOnlyList<Vector3> points, Vector3 axis, float margin, float metresPerTexel, int maxSize,
            int cullingMask, float clearance, Color clearColor)
        {
            if (points == null || points.Count == 0) return null;
            var c = Frame(points, axis, margin, clearance);
            c.Texture = CreateTexture(c.sizeX, c.sizeZ, metresPerTexel, maxSize);
            // Karts (their visuals and dust sit on the default layer) and powerup objects are not ground: hidden, so
            // neither they nor their shadows end up in the picture. Everything else in the volume renders unlit.
            using (var scope = new RenderScope())
            {
                foreach (var k in Kart.All) scope.Hide(k.transform);
                scope.Hide(PowerupFx.Root);
                scope.Unlit(c.Volume(), cullingMask);
                c.RenderOrtho(cullingMask, clearColor);
            }
            return c;
        }

        static GroundCapture Frame(IReadOnlyList<Vector3> points, Vector3 axis, float margin, float clearance)
        {
            axis.y = 0f;
            var axisZ = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward;
            var axisX = new Vector3(axisZ.z, 0f, -axisZ.x);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in points)
            {
                var x = Vector3.Dot(p, axisX);
                var z = Vector3.Dot(p, axisZ);
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            minX -= margin; maxX += margin; minZ -= margin; maxZ += margin;
            var c = new GroundCapture { axisX = axisX, axisZ = axisZ, sizeX = maxX - minX, sizeZ = maxZ - minZ };
            c.centre = axisX * ((minX + maxX) * 0.5f) + axisZ * ((minZ + maxZ) * 0.5f);
            c.centre.y = maxY + clearance;
            c.depth = clearance + (maxY - minY) + DepthMargin;
            return c;
        }

        static RenderTexture CreateTexture(float sizeX, float sizeZ, float metresPerTexel, int maxSize)
        {
            var texel = Mathf.Max(MinTexel, metresPerTexel);
            var w = sizeX / texel;
            var h = sizeZ / texel;
            var fit = Mathf.Min(1f, maxSize / Mathf.Max(w, h));
            var texture = new RenderTexture(Mathf.Clamp(Mathf.RoundToInt(w * fit), MinSize, maxSize),
                Mathf.Clamp(Mathf.RoundToInt(h * fit), MinSize, maxSize), DepthBits, RenderTextureFormat.ARGB32)
            {
                name = "GroundCapture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = true,
                autoGenerateMips = true,
            };
            texture.Create();
            return texture;
        }

        // World bounds of the captured volume, for picking the renderers to show unlit.
        Bounds Volume()
        {
            var volume = new Bounds(centre + Vector3.down * (depth * 0.5f), Vector3.zero);
            for (var sx = -1; sx <= 1; sx += 2)
            for (var sz = -1; sz <= 1; sz += 2)
                volume.Encapsulate(centre + axisX * (sx * sizeX * 0.5f) + axisZ * (sz * sizeZ * 0.5f));
            volume.Encapsulate(centre + Vector3.down * depth);
            return volume;
        }

        void RenderOrtho(int cullingMask, Color clearColor)
        {
            var go = new GameObject("GroundCaptureCamera");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = sizeZ * 0.5f;
                cam.aspect = sizeX / sizeZ;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = depth;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = clearColor;
                cam.cullingMask = cullingMask;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.useOcclusionCulling = false;
                SkipUnusedPasses(cam);
                go.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(Vector3.down, axisZ));
                cam.targetTexture = Texture;
                cam.Render();
                cam.targetTexture = null;
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        // Everything is drawn fully self-lit, so shadows, depth and colour copies and post-processing are wasted work.
        static void SkipUnusedPasses(Camera cam)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderShadows = false;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.renderPostProcessing = false;
        }

        /// <summary>Hides renderers and makes MHK/Lit renderers fully self-lit (_Emission = 1) until disposed.</summary>
        sealed class RenderScope : IDisposable
        {
            readonly List<Renderer> hidden = ListPool<Renderer>.Get();
            readonly List<Renderer> unlit = ListPool<Renderer>.Get();
            readonly List<MaterialPropertyBlock> savedBlocks = ListPool<MaterialPropertyBlock>.Get();

            public void Hide(Transform root)
            {
                if (root == null) return;
                using (ListPool<Renderer>.Get(out var renderers))
                {
                    root.GetComponentsInChildren(renderers);
                    foreach (var r in renderers)
                    {
                        if (!r.enabled) continue;
                        r.enabled = false;
                        hidden.Add(r);
                    }
                }
            }

            public void Unlit(Bounds volume, int cullingMask)
            {
                unlitBlock ??= new MaterialPropertyBlock();
                unlitBlock.Clear();
                unlitBlock.SetFloat(EmissionId, 1f);
                foreach (var r in Object.FindObjectsByType<MeshRenderer>())
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy || (cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                    if (!r.bounds.Intersects(volume)) continue;
                    savedBlocks.Add(MakeUnlit(r));
                    unlit.Add(r);
                }
            }

            static MaterialPropertyBlock MakeUnlit(MeshRenderer r)
            {
                if (!r.HasPropertyBlock())
                {
                    r.SetPropertyBlock(unlitBlock);
                    return null;
                }
                var saved = new MaterialPropertyBlock();
                r.GetPropertyBlock(saved);
                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
                block.SetFloat(EmissionId, 1f);
                r.SetPropertyBlock(block);
                return saved;
            }

            public void Dispose()
            {
                foreach (var r in hidden)
                    if (r != null) r.enabled = true;
                for (var i = 0; i < unlit.Count; i++)
                    if (unlit[i] != null) unlit[i].SetPropertyBlock(savedBlocks[i]);
                ListPool<Renderer>.Release(hidden);
                ListPool<Renderer>.Release(unlit);
                ListPool<MaterialPropertyBlock>.Release(savedBlocks);
            }
        }
    }
}
