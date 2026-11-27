using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Attract
{
    /// <summary>
    /// Lets a freshly loaded scene be drawn a few new materials per frame (<see cref="Renderer.forceRenderingOff"/>) while
    /// something still covers it: drawing a material the first time creates its shader programs and pipeline states and
    /// uploads its textures, which for a whole track at once stalls one frame for a long moment. Renderers sharing
    /// materials show together; renderers outside the camera's view stay cheap until they first come into it. A slow
    /// device gets fewer new materials per frame: each frame slower than <see cref="SlowFrameSeconds"/> halves the next
    /// frame's share, each quicker one raises it by one again.
    /// </summary>
    public static class RenderWarmUp
    {
        /// <summary>A frame that took longer than this (s) drew too many new materials at once.</summary>
        public const float SlowFrameSeconds = 0.05f;

        /// <summary>
        /// Hides the renderers drawn in <paramref name="scenes"/> and below <paramref name="root"/>, then shows them again over
        /// the following frames, at most <paramref name="materialsPerFrame"/> materials not drawn before per frame (a
        /// renderer with more still shows alone). Cancelling shows everything at once.
        /// </summary>
        public static async Awaitable RevealAsync(IReadOnlyList<Scene> scenes, Transform root, int materialsPerFrame, CancellationToken ct)
        {
            var hidden = Collect(scenes, root);
            var seen = new HashSet<Material>();
            var materials = new List<Material>();
            var share = Mathf.Max(1, materialsPerFrame);
            var next = 0;
            try
            {
                while (next < hidden.Count)
                {
                    for (var fresh = 0; next < hidden.Count; next++)
                    {
                        var r = hidden[next];
                        if (r == null) continue;
                        r.GetSharedMaterials(materials);
                        var added = 0;
                        foreach (var m in materials)
                            if (m != null && !seen.Contains(m)) added++;
                        if (fresh > 0 && fresh + added > share) break;
                        foreach (var m in materials)
                            if (m != null) seen.Add(m);
                        fresh += added;
                        r.forceRenderingOff = false;
                    }
                    await Awaitable.NextFrameAsync(ct);
                    // The frame that drew them tells what they cost.
                    share = Time.unscaledDeltaTime > SlowFrameSeconds ? Mathf.Max(1, share / 2) : Mathf.Min(materialsPerFrame, share + 1);
                }
            }
            finally
            {
                for (var i = next; i < hidden.Count; i++)
                    if (hidden[i] != null) hidden[i].forceRenderingOff = false;
            }
        }

        // The drawn renderers, hidden, grouped by their first material so a material's renderers show in the same frame.
        static List<Renderer> Collect(IReadOnlyList<Scene> scenes, Transform root)
        {
            var found = new List<Renderer>();
            var part = new List<Renderer>();
            foreach (var scene in scenes)
            {
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                {
                    go.GetComponentsInChildren(false, part);
                    found.AddRange(part);
                }
            }
            if (root != null)
            {
                root.GetComponentsInChildren(false, part);
                found.AddRange(part);
            }
            var hidden = new List<Renderer>(found.Count);
            foreach (var r in found)
            {
                if (!r.enabled || r.forceRenderingOff) continue;
                r.forceRenderingOff = true;
                hidden.Add(r);
            }
            hidden.Sort((a, b) => FirstMaterialId(a).CompareTo(FirstMaterialId(b)));
            return hidden;
        }

        static int FirstMaterialId(Renderer r)
        {
            var m = r.sharedMaterial;
            return m != null ? m.GetHashCode() : 0;
        }
    }
}
