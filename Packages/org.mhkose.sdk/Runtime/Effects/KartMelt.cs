using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Rendering;
using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// An attached status-effect visual: kart and driver soften into one warm, wobbling pool in MHK/Lit.
    /// Property blocks, the visual Body pose and conservative renderer bounds change; physics and meshes stay untouched.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class KartMelt : MonoBehaviour
    {
        [Tooltip("Fraction of the effect before the kart has fully pooled. The rest holds the melted shape.")]
        [SerializeField, Range(0.1f, 1f)] float meltCompletion = 0.85f;
        [Tooltip("Horizontal spread of the melted kart, relative to its original width and length.")]
        [SerializeField, Range(0f, 2f)] float pooling = 0.9f;
        [Tooltip("Uniform visual scale when fully melted, blended from the normal model size. Does not change the chassis.")]
        [SerializeField, Range(0.1f, 1f)] float flattenedScale = 1f;
        [Tooltip("Minimum full-melt clearance above the wheel-contact support plane (metres). Only lifts the visual Body.")]
        [SerializeField, Min(0f)] float groundClearance = 0.02f;
        [Tooltip("Boiling ripple amplitude as a fraction of the combined kart and driver height.")]
        [SerializeField, Range(0f, 0.1f)] float wobbleHeight = 0.025f;
        [Tooltip("Warm light added while melting; does not replace other effect tints or opacity.")]
        [SerializeField, ColorUsage(false, true)] Color warmth = new(0.24f, 0.055f, 0.008f, 1f);
        [Tooltip("White steam and water particles authored in a unit-sized body volume.")]
        [SerializeField] GameObject boilingVfxPrefab;
        [Tooltip("Translucent white-water sheen on the kart and driver; alpha is its strength.")]
        [SerializeField] Color waterOverlay = new(0.9f, 0.97f, 1f, 0.55f);

        struct MeltRenderer
        {
            public Renderer renderer;
            public Bounds originalBounds;
        }

        readonly List<MeltRenderer> renderers = new();
        MaterialPropertyBlock block;
        Kart kart;
        Transform body;
        KartEffects.ActiveEffect effect;
        Bounds bodyBounds;
        Vector3 originalBodyPosition, originalBodyScale, appliedBodyPosition;
        float appliedScale = 1f;
        bool showing;
        GameObject boilingVfx;
        ParticleSystem boilingParticles;
        bool boilingStopped;

        void Awake() => block = new MaterialPropertyBlock();

        void Start()
        {
            // The effect's Vfx is assigned after Instantiate, so bind here rather than in Awake.
            kart = GetComponentInParent<Kart>();
            if (kart == null || kart.Visual == null || kart.Effects == null) return;
            foreach (var active in kart.Effects.Active)
                if (active.Vfx == gameObject) { effect = active; break; }
            if (effect == null) return;
            body = kart.Visual.Body;
            if (body == null) return;
            originalBodyPosition = body.localPosition;
            originalBodyScale = body.localScale;
            appliedBodyPosition = originalBodyPosition;
            CacheRenderers();
            if (renderers.Count == 0) return;
            ExpandBounds();
            SpawnBoilingVfx();
            showing = true;
            kart.Effects.Ended += OnEnded;
            kart.Respawned += OnRespawned;
        }

        void SpawnBoilingVfx()
        {
            if (boilingVfxPrefab == null) return;
            var origin = body.TransformPoint(new Vector3(bodyBounds.center.x, bodyBounds.min.y, bodyBounds.center.z));
            boilingVfx = Instantiate(boilingVfxPrefab, origin, body.rotation, transform);
            boilingParticles = boilingVfx.GetComponent<ParticleSystem>();
            var size = Vector3.Scale(bodyBounds.size, body.lossyScale);
            var parentScale = transform.lossyScale;
            boilingVfx.transform.localScale = new Vector3(
                Mathf.Abs(size.x) / Mathf.Max(0.001f, Mathf.Abs(parentScale.x)),
                Mathf.Abs(size.y) / Mathf.Max(0.001f, Mathf.Abs(parentScale.y)),
                Mathf.Abs(size.z) / Mathf.Max(0.001f, Mathf.Abs(parentScale.z)));
        }

        void CacheRenderers()
        {
            var materials = new List<Material>();
            foreach (var r in body.GetComponentsInChildren<Renderer>(true))
            {
                if (!BoundsUtil.ModelSpace(r, out var space) || r.transform.IsChildOf(transform)
                    || kart.Effects.IsEffectAttachment(r.transform)) continue;
                materials.Clear();
                r.GetSharedMaterials(materials);
                var supported = false;
                foreach (var material in materials)
                    if (material != null && material.shader != null && material.shader.name == MHKLitIds.ShaderName)
                    { supported = true; break; }
                if (!supported) continue;
                var bounds = r.localBounds;
                var toBody = body.worldToLocalMatrix * space;
                for (var i = 0; i < 8; i++)
                {
                    var p = toBody.MultiplyPoint3x4(BoundsUtil.Corner(bounds, i));
                    if (renderers.Count == 0 && i == 0) bodyBounds = new Bounds(p, Vector3.zero);
                    else bodyBounds.Encapsulate(p);
                }
                renderers.Add(new MeltRenderer { renderer = r, originalBounds = bounds });
            }
        }

        void ExpandBounds()
        {
            var pooled = bodyBounds;
            pooled.size = Vector3.Scale(pooled.size, new Vector3(1f + pooling, 1f, 1f + pooling));
            // Covers the waves, driver animation and the height-dependent sideways sag without clipping the pool.
            pooled.Expand(bodyBounds.size.magnitude * 0.25f);
            foreach (var entry in renderers)
            {
                BoundsUtil.ModelSpace(entry.renderer, out var space);
                var toRenderer = space.inverse * body.localToWorldMatrix;
                var bounds = entry.originalBounds;
                for (var i = 0; i < 8; i++) bounds.Encapsulate(toRenderer.MultiplyPoint3x4(BoundsUtil.Corner(pooled, i)));
                entry.renderer.localBounds = bounds;
            }
        }

        void LateUpdate()
        {
            if (!showing) return;
            if (kart == null || !kart.isActiveAndEnabled || body == null) { ResetMelt(); return; }
            var progress = effect.Progress;
            var stopBoiling = progress >= meltCompletion;
            if (boilingStopped != stopBoiling)
            {
                if (boilingParticles != null)
                {
                    if (stopBoiling) boilingParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    else boilingParticles.Play(true); // A refreshed hit starts a new active melting interval.
                }
                boilingStopped = stopBoiling;
            }
            var strength = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(progress / meltCompletion));
            var scale = Mathf.Lerp(1f, flattenedScale, strength);
            ApplyPose(scale, strength);
            var origin = body.TransformPoint(new Vector3(bodyBounds.center.x, bodyBounds.min.y, bodyBounds.center.z));
            var height = body.TransformVector(Vector3.up * bodyBounds.size.y).magnitude;
            var up = body.up;
            var right = body.right;
            var forward = body.forward;
            var meltOrigin = new Vector4(origin.x, origin.y, origin.z, strength);
            var meltUp = new Vector4(up.x, up.y, up.z, Mathf.Max(height, 0.001f));
            var meltRight = new Vector4(right.x, right.y, right.z, pooling);
            var meltForward = new Vector4(forward.x, forward.y, forward.z, height * wobbleHeight);
            var meltHeat = new Vector4(warmth.r, warmth.g, warmth.b, effect.Elapsed);
            var meltWater = new Vector4(waterOverlay.r, waterOverlay.g, waterOverlay.b, waterOverlay.a);
            foreach (var entry in renderers)
            {
                var r = entry.renderer;
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.SetVector(MHKLitIds.MeltOrigin, meltOrigin);
                block.SetVector(MHKLitIds.MeltUp, meltUp);
                block.SetVector(MHKLitIds.MeltRight, meltRight);
                block.SetVector(MHKLitIds.MeltForward, meltForward);
                block.SetVector(MHKLitIds.MeltHeat, meltHeat);
                block.SetVector(MHKLitIds.MeltWater, meltWater);
                r.SetPropertyBlock(block);
            }
        }

        void ApplyPose(float scale, float strength)
        {
            if (scale != appliedScale)
            {
                body.localScale = originalBodyScale * scale;
                appliedScale = scale;
            }
            var anchor = new Vector3(bodyBounds.center.x, bodyBounds.min.y, bodyBounds.center.z);
            // Shrink about the cached model base, then lift that base to the actual road, not its mesh bounds.
            var position = originalBodyPosition
                + body.localRotation * Vector3.Scale(anchor, originalBodyScale * (1f - scale));
            var origin = body.parent.TransformPoint(position) + body.TransformVector(anchor);
            position += body.parent.InverseTransformVector(GroundLift(origin, strength));
            if (position.Equals(appliedBodyPosition)) return;
            body.localPosition = position;
            appliedBodyPosition = position;
        }

        Vector3 GroundLift(Vector3 origin, float strength)
        {
            var physics = kart.Physics;
            if (strength <= 0f || physics == null || !physics.IsGrounded) return Vector3.zero;
            var normal = Vector3.zero;
            for (var i = 0; i < 4; i++)
                if (physics.WheelOnGround(i)) normal += physics.WheelContactNormal(i);
            if (normal.sqrMagnitude < 0.001f) return Vector3.zero;
            normal.Normalize();
            // The shader clamps pooled height to zero. Its base is the lowest point only while facing the road.
            if (Vector3.Dot(normal, body.up) <= 0f) return Vector3.zero;
            var roadHeight = float.NegativeInfinity;
            for (var i = 0; i < 4; i++)
                if (physics.WheelOnGround(i))
                    roadHeight = Mathf.Max(roadHeight, Vector3.Dot(physics.WheelContactPoint(i) - origin, normal));
            // Enclose the shader's spread and sideways sway, so a bank cannot bury the outer edge of the pool.
            var height = body.TransformVector(Vector3.up * bodyBounds.size.y).magnitude;
            var spread = 1f + strength * pooling;
            var sway = height * wobbleHeight * strength * (1f - 0.6f * strength);
            var halfWidth = body.TransformVector(Vector3.right * bodyBounds.extents.x).magnitude * spread + sway;
            var halfLength = body.TransformVector(Vector3.forward * bodyBounds.extents.z).magnitude * spread;
            var edgeDepth = Mathf.Abs(Vector3.Dot(normal, body.right)) * halfWidth
                + Mathf.Abs(Vector3.Dot(normal, body.forward)) * halfLength;
            // Blend in without a start pop; at full melt the entire conservative pool clears the support plane.
            return normal * (Mathf.Max(0f, roadHeight + edgeDepth + groundClearance) * strength);
        }

        void OnEnded(StatusEffectDefinition definition)
        {
            if (effect != null && effect.Definition == definition) ResetMelt();
        }

        void OnRespawned(Kart _) => ResetMelt();
        void OnDisable() => ResetMelt();
        void OnDestroy() => ResetMelt();

        void ResetMelt()
        {
            if (!showing) return;
            showing = false;
            if (body != null)
            {
                body.localPosition = originalBodyPosition;
                body.localScale = originalBodyScale;
            }
            if (boilingVfx != null) Destroy(boilingVfx);
            boilingVfx = null;
            boilingParticles = null;
            boilingStopped = false;
            if (kart != null)
            {
                kart.Effects.Ended -= OnEnded;
                kart.Respawned -= OnRespawned;
            }
            foreach (var entry in renderers)
            {
                var r = entry.renderer;
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.SetVector(MHKLitIds.MeltOrigin, Vector4.zero);
                block.SetVector(MHKLitIds.MeltUp, Vector4.zero);
                block.SetVector(MHKLitIds.MeltRight, Vector4.zero);
                block.SetVector(MHKLitIds.MeltForward, Vector4.zero);
                block.SetVector(MHKLitIds.MeltHeat, Vector4.zero);
                block.SetVector(MHKLitIds.MeltWater, Vector4.zero);
                r.SetPropertyBlock(block);
                r.localBounds = entry.originalBounds;
            }
        }
    }
}
