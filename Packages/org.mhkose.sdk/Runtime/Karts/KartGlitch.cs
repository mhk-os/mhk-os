using System.Collections.Generic;
using MHK.Core;
using MHK.Rendering;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Jammed TV look of a kart (<see cref="Modding.StatusEffectDefinition.glitchVisual"/>, the TINcan jamming signal).
    /// While it lasts, bursts of a few frames tear the kart model into sideways shifted bands with static and colour
    /// fringes (MHK/Lit _Glitch), and stripes of TV static flash around it (MHK/GlitchStatic). Between the bursts the
    /// kart looks normal and the static only flickers faintly. The driver keeps its look, unless it is the kart body
    /// itself (a driver racing without a kart).
    /// </summary>
    public sealed partial class KartGlitch
    {
        const float BandsPerMetre = 11f;
        const float WidestTear = 0.22f;
        const float MinGap = 0.1f, MaxGap = 0.5f;
        const float MinBurst = 0.06f, MaxBurst = 0.18f;
        // A new tear pattern this often during a burst, about every second or third frame at 60 fps.
        const float PatternSeconds = 1f / 24f;
        const float QuietStatic = 0.3f;
        const float StaticRows = 26f, StaticColumns = 90f;
        // The static quad's size relative to the kart model's bounds.
        const float StaticWidth = 1.3f, StaticHeight = 1.7f;

        [AutoStaticsCleanup] static Mesh quad;
        [AutoStaticsCleanup] static Material staticMaterial;
        static readonly int StaticId = Shader.PropertyToID("_GlitchStatic");

        readonly Transform body;
        readonly List<Renderer> renderers = new();
        readonly MaterialPropertyBlock block = new();
        readonly Bounds bounds;
        MaterialPropertyBlock staticBlock;
        Renderer staticRenderer;
        bool shown, bursting;
        float phaseLeft, patternLeft, seed;

        /// <summary>
        /// The glitch of the kart model below <paramref name="body"/>: its renderers apart from the driver's
        /// (<paramref name="driver"/>, may be null), or the driver's alone when <paramref name="model"/> is null and the
        /// driver is the kart body. Built right after the rig, before effect attachments join the body.
        /// </summary>
        public KartGlitch(Transform body, KartModel model, Transform driver)
        {
            this.body = body;
            var root = model != null ? body : driver;
            if (root == null) return;
            var found = false;
            var b = default(Bounds);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is not MeshRenderer && r is not SkinnedMeshRenderer) continue;
                if (model != null && driver != null && r.transform.IsChildOf(driver)) continue;
                renderers.Add(r);
                var local = new Bounds(body.InverseTransformPoint(r.bounds.center), body.InverseTransformVector(r.bounds.size));
                local.size = new Vector3(Mathf.Abs(local.size.x), Mathf.Abs(local.size.y), Mathf.Abs(local.size.z));
                if (found) b.Encapsulate(local);
                else b = local;
                found = true;
            }
            bounds = b;
        }

        /// <summary>Advances the glitch by <paramref name="dt"/> real seconds, running while <paramref name="jammed"/>.</summary>
        public void Update(bool jammed, float dt)
        {
            if (!jammed)
            {
                if (shown) Stop();
                return;
            }
            if (!shown)
            {
                shown = true;
                bursting = false;
                phaseLeft = 0f;
            }
            phaseLeft -= dt;
            if (phaseLeft <= 0f)
            {
                bursting = !bursting;
                phaseLeft = bursting ? Random.Range(MinBurst, MaxBurst) : Random.Range(MinGap, MaxGap);
                patternLeft = 0f;
            }
            patternLeft -= dt;
            if (patternLeft > 0f) return;
            patternLeft = PatternSeconds;
            seed = Random.Range(1f, 997f);
            SetModel(bursting ? 1f : 0f);
            SetStatic(bursting ? 1f : QuietStatic * Random.value);
        }

        /// <summary>Faces the static towards <paramref name="camera"/>, upright.</summary>
        public void FaceCamera(Camera camera)
        {
            if (staticRenderer == null || !staticRenderer.enabled || camera == null) return;
            var t = staticRenderer.transform;
            var away = t.position - camera.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-4f) t.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        void Stop()
        {
            shown = bursting = false;
            SetModel(0f);
            if (staticRenderer != null) staticRenderer.enabled = false;
        }

        void SetModel(float strength)
        {
            var origin = body.position;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.SetVector(MHKLitIds.Glitch, new Vector4(strength, seed, BandsPerMetre, WidestTear));
                block.SetVector(MHKLitIds.GlitchOrigin, new Vector4(origin.x, origin.y, origin.z, 0f));
                r.SetPropertyBlock(block);
            }
        }

        void SetStatic(float strength)
        {
            if (staticRenderer == null && !BuildStatic()) return;
            staticRenderer.enabled = strength > 0f;
            staticBlock.SetVector(StaticId, new Vector4(strength, seed, StaticRows, StaticColumns));
            staticRenderer.SetPropertyBlock(staticBlock);
        }

        bool BuildStatic()
        {
            var shader = GameRoot.Instance.Config.glitchStaticShader;
            if (shader == null || renderers.Count == 0) return false;
            if (staticMaterial == null) staticMaterial = new Material(shader) { name = "Glitch static" };
            if (quad == null) quad = BuildQuad();
            var go = new GameObject("Glitch static") { layer = body.gameObject.layer };
            go.transform.SetParent(body, false);
            go.transform.localPosition = bounds.center;
            var width = Mathf.Max(bounds.size.x, bounds.size.z) * StaticWidth;
            go.transform.localScale = new Vector3(width, bounds.size.y * StaticHeight, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            staticRenderer = go.AddComponent<MeshRenderer>();
            staticRenderer.sharedMaterial = staticMaterial;
            staticRenderer.shadowCastingMode = ShadowCastingMode.Off;
            staticRenderer.receiveShadows = false;
            staticBlock = new MaterialPropertyBlock();
            return true;
        }

        static Mesh BuildQuad()
        {
            var mesh = new Mesh { name = "Glitch static quad" };
            mesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
