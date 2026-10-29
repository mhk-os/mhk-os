using System.Collections.Generic;
using MHK.Rendering;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Effect looks of a kart visual: the burnt or effect tint, see-through opacity and hiding. Fading swaps the opaque
    /// MHK/Lit materials of kart and driver (not effect attachments) for alpha-blended copies with depth writes on and no
    /// shadows. The copies are shared by every visual per material and opacity step, so dozens of ghosts of the same
    /// kart and character draw with the same few materials.
    /// </summary>
    public sealed partial class KartSeeThrough
    {
        const float FadeSpeed = 3f;
        const float TintInRate = 6f, TintOutRate = 0.8f;
        // Opacity steps of the shared copies: an 8-bit render target shows no finer alpha anyway.
        const int OpacitySteps = 255;
        static readonly Color BurntTint = new(0.12f, 0.1f, 0.1f);

        struct FadedRenderer
        {
            public Renderer renderer;
            public Material[] original;
            /// <summary>Per material slot: index into fadeSources, -1 to keep the original.</summary>
            public int[] sources;
            /// <summary>Materials assigned now (reused at every opacity step).</summary>
            public Material[] shown;
            public ShadowCastingMode shadows;
        }

        sealed class SharedCopy
        {
            public Material material;
            public int users;
        }

        // (opaque material, opacity step) to its alpha-blended copy and the visuals holding it.
        [AutoStaticsCleanup] static readonly Dictionary<(Material, int), SharedCopy> SharedCopies = new();

        readonly Transform root;
        readonly IKartView view;
        readonly Renderer[] tinted;
        readonly MaterialPropertyBlock block = new();
        readonly List<FadedRenderer> fadedRenderers = new();
        readonly List<Renderer> fadeScratch = new();
        readonly List<Renderer> hiddenRenderers = new();
        // Distinct opaque materials faded on this visual, and their copies at the step held now.
        readonly List<Material> fadeSources = new();
        readonly List<Material> heldCopies = new();
        int heldStep = -1;
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
            var step = Mathf.Clamp(Mathf.RoundToInt(ShownOpacity * OpacitySteps), 0, OpacitySteps - 1);
            if (step != heldStep && fadedRenderers.Count > 0) ShowStep(step);
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

        /// <summary>Gives back the shared faded materials (owner destroyed).</summary>
        public void ReleaseFadedMaterials()
        {
            ReleaseHeld();
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
            int[] sources = null;
            for (var i = 0; i < original.Length; i++)
            {
                var m = original[i];
                if (m == null || m.shader == null || m.shader.name != MHKLitIds.ShaderName || m.GetFloat(MHKLitIds.DstBlend) != 0f) continue;
                if (sources == null)
                {
                    sources = new int[original.Length];
                    for (var j = 0; j < sources.Length; j++) sources[j] = -1;
                }
                var index = fadeSources.IndexOf(m);
                if (index < 0)
                {
                    index = fadeSources.Count;
                    fadeSources.Add(m);
                }
                sources[i] = index;
            }
            if (sources == null) return;
            fadedRenderers.Add(new FadedRenderer
            {
                renderer = r, original = original, sources = sources, shown = (Material[])original.Clone(), shadows = r.shadowCastingMode,
            });
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        // Takes the shared copies of the new step before giving back the old ones, so a copy two visuals pass between survives.
        void ShowStep(int step)
        {
            var previous = heldStep;
            heldStep = step;
            var count = heldCopies.Count;
            foreach (var m in fadeSources) heldCopies.Add(Acquire(m, step));
            foreach (var f in fadedRenderers)
            {
                if (f.renderer == null) continue;
                for (var i = 0; i < f.sources.Length; i++)
                    if (f.sources[i] >= 0) f.shown[i] = heldCopies[count + f.sources[i]];
                f.renderer.sharedMaterials = f.shown;
            }
            if (previous >= 0)
                foreach (var m in fadeSources) Release(m, previous);
            heldCopies.RemoveRange(0, count);
        }

        static Material Acquire(Material source, int step)
        {
            if (!SharedCopies.TryGetValue((source, step), out var shared))
            {
                var copy = new Material(source) { name = source.name + " (faded)", renderQueue = (int)RenderQueue.Transparent };
                copy.SetFloat(MHKLitIds.SrcBlend, (float)BlendMode.SrcAlpha);
                copy.SetFloat(MHKLitIds.DstBlend, (float)BlendMode.OneMinusSrcAlpha);
                copy.SetOverrideTag("RenderType", "Transparent");
                var c = source.GetColor(MHKLitIds.BaseColor);
                c.a *= step / (float)OpacitySteps;
                copy.SetColor(MHKLitIds.BaseColor, c);
                SharedCopies[(source, step)] = shared = new SharedCopy { material = copy };
            }
            shared.users++;
            return shared.material;
        }

        static void Release(Material source, int step)
        {
            if (!SharedCopies.TryGetValue((source, step), out var shared) || --shared.users > 0) return;
            SharedCopies.Remove((source, step));
            Object.Destroy(shared.material);
        }

        void ReleaseHeld()
        {
            if (heldStep >= 0)
                foreach (var m in fadeSources) Release(m, heldStep);
            heldStep = -1;
            heldCopies.Clear();
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
            ReleaseHeld();
            fadeSources.Clear();
        }
    }
}
