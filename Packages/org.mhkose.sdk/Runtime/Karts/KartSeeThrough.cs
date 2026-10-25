using System.Collections.Generic;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Effect looks of a kart visual: the burnt or effect tint, see-through opacity and hiding. Fading swaps the opaque
    /// MHK/Lit materials of kart and driver (not effect attachments) for alpha-blended copies with depth writes on and no
    /// shadows.
    /// </summary>
    public sealed class KartSeeThrough
    {
        const float FadeSpeed = 3f;
        const float TintInRate = 6f, TintOutRate = 0.8f;
        static readonly Color BurntTint = new(0.12f, 0.1f, 0.1f);

        struct FadedRenderer
        {
            public Renderer renderer;
            public Material[] original;
            public ShadowCastingMode shadows;
        }

        readonly Transform root;
        readonly IKartView view;
        readonly Renderer[] tinted;
        readonly MaterialPropertyBlock block = new();
        readonly List<FadedRenderer> fadedRenderers = new();
        readonly List<Renderer> fadeScratch = new();
        readonly List<Renderer> hiddenRenderers = new();
        // Opaque material to its alpha-blended copy, with the original colour to scale the alpha from.
        readonly Dictionary<Material, Material> fadeCopies = new();
        readonly Dictionary<Material, Color> fadeColours = new();
        // Tint shown now: Color.white blended towards tintColour by tintAmount.
        Color tintColour = Color.white;
        float tintAmount;
        bool hidden;

        /// <summary>Looks of the visual below <paramref name="root"/>. Tints apply to <paramref name="tinted"/>.</summary>
        public KartSeeThrough(Transform root, IKartView view, Renderer[] tinted)
        {
            this.root = root;
            this.view = view;
            this.tinted = tinted;
        }

        /// <summary>Opacity drawn now (follows <see cref="IKartView.Opacity"/> at a fixed rate).</summary>
        public float ShownOpacity { get; private set; } = 1f;

        /// <summary>
        /// Moves the shown tint towards the burnt look while <paramref name="burned"/>, else towards <paramref name="tint"/>
        /// (alpha = strength, 0 = none). A new colour shows at once, the strength fades. Back at 0 the renderers drop their
        /// property block.
        /// </summary>
        public void UpdateTint(bool burned, Color tint, float dt)
        {
            var goal = burned ? BurntTint : tint;
            var target = Mathf.Clamp01(goal.a);
            var colour = target > 0f ? new Color(goal.r, goal.g, goal.b) : tintColour;
            if (Mathf.Approximately(tintAmount, target) && colour == tintColour) return;
            tintColour = colour;
            tintAmount = Mathf.MoveTowards(tintAmount, target, dt * (target > tintAmount ? TintInRate : TintOutRate));
            var clear = tintAmount <= 0f;
            var shown = Color.Lerp(Color.white, tintColour, tintAmount);
            foreach (var r in tinted)
            {
                if (r == null) continue;
                if (clear)
                {
                    r.SetPropertyBlock(null);
                    continue;
                }
                r.GetPropertyBlock(block);
                block.SetColor(MHKLitIds.Tint, shown);
                r.SetPropertyBlock(block);
            }
        }

        /// <summary>Moves the shown opacity towards <paramref name="target"/>, swapping to faded materials below 1.</summary>
        public void UpdateFade(float target, float dt)
        {
            if (Mathf.Approximately(ShownOpacity, target)) return;
            ShownOpacity = Mathf.MoveTowards(ShownOpacity, target, FadeSpeed * dt);
            if (ShownOpacity >= 1f)
            {
                EndFade();
                return;
            }
            if (fadedRenderers.Count == 0) BeginFade();
            foreach (var pair in fadeCopies)
            {
                var c = fadeColours[pair.Key];
                c.a *= ShownOpacity;
                pair.Value.SetColor(MHKLitIds.BaseColor, c);
            }
        }

        /// <summary>Switches the renderers of kart and driver (not effect attachments) off while <paramref name="hide"/> is set.</summary>
        public void UpdateHidden(bool hide)
        {
            if (hide == hidden) return;
            hidden = hide;
            if (!hide)
            {
                foreach (var r in hiddenRenderers)
                    if (r != null) r.enabled = true;
                hiddenRenderers.Clear();
                return;
            }
            root.GetComponentsInChildren(fadeScratch);
            foreach (var r in fadeScratch)
            {
                if (!r.enabled || view.IsEffectAttachment(r.transform)) continue;
                r.enabled = false;
                hiddenRenderers.Add(r);
            }
            fadeScratch.Clear();
        }

        /// <summary>Destroys the faded material copies (owner destroyed).</summary>
        public void DestroyCopies()
        {
            foreach (var copy in fadeCopies.Values) Object.Destroy(copy);
        }

        void BeginFade()
        {
            root.GetComponentsInChildren(true, fadeScratch);
            foreach (var r in fadeScratch)
            {
                if (r is not MeshRenderer && r is not SkinnedMeshRenderer) continue;
                if (view.IsEffectAttachment(r.transform)) continue;
                TrySwapToFaded(r);
            }
            fadeScratch.Clear();
        }

        void TrySwapToFaded(Renderer r)
        {
            var original = r.sharedMaterials;
            var swapped = new Material[original.Length];
            var any = false;
            for (var i = 0; i < original.Length; i++)
            {
                var m = original[i];
                swapped[i] = m;
                if (m == null || m.shader == null || m.shader.name != MHKLitIds.ShaderName || m.GetFloat(MHKLitIds.DstBlend) != 0f) continue;
                swapped[i] = GetFadedCopy(m);
                any = true;
            }
            if (!any) return;
            fadedRenderers.Add(new FadedRenderer { renderer = r, original = original, shadows = r.shadowCastingMode });
            r.sharedMaterials = swapped;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        Material GetFadedCopy(Material m)
        {
            if (fadeCopies.TryGetValue(m, out var copy)) return copy;
            copy = new Material(m) { name = m.name + " (faded)", renderQueue = (int)RenderQueue.Transparent };
            copy.SetFloat(MHKLitIds.SrcBlend, (float)BlendMode.SrcAlpha);
            copy.SetFloat(MHKLitIds.DstBlend, (float)BlendMode.OneMinusSrcAlpha);
            copy.SetOverrideTag("RenderType", "Transparent");
            fadeCopies[m] = copy;
            fadeColours[m] = m.GetColor(MHKLitIds.BaseColor);
            return copy;
        }

        void EndFade()
        {
            ShownOpacity = 1f;
            foreach (var f in fadedRenderers)
            {
                if (f.renderer == null) continue;
                f.renderer.sharedMaterials = f.original;
                f.renderer.shadowCastingMode = f.shadows;
            }
            fadedRenderers.Clear();
            foreach (var copy in fadeCopies.Values) Object.Destroy(copy);
            fadeCopies.Clear();
            fadeColours.Clear();
        }
    }
}
