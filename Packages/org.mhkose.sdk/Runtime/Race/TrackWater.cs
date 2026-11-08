using System.Collections.Generic;
using System.Text.RegularExpressions;
using MHK.Core;
using MHK.Rendering;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Race
{
    /// <summary>
    /// HD water of the running race: at its start every MHK/Lit water material of the loaded track (by name, see
    /// <see cref="IsWaterBody"/>) is swapped on its renderers for a copy drawn with <see cref="GameConfig.waterShader"/>
    /// (MHK/Water: waves, sky reflection and highlights over the original's colour), keeping its texture, animation, colour
    /// and blending. Sky domes keep theirs. The race's end puts the originals back and frees the copies. Without the shader
    /// in the config the water stays as the track made it.
    /// </summary>
    public static partial class TrackWater
    {
        static readonly int WaveMap = Shader.PropertyToID("_WaveMap");
        // Lava and magma are not water, though the wet look leaves them out by the same names.
        static readonly Regex LavaName = new("lava|magma", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [AutoStaticsCleanup] static readonly List<(Renderer renderer, Material[] materials)> swapped = new();
        [AutoStaticsCleanup] static readonly List<Material> copies = new();

        /// <summary>
        /// Swaps the water of the loaded track for HD water. Call after the variant's overlays loaded and before
        /// <see cref="TrackWeather.Begin"/> (which then leaves the HD water alone) and the sky domes are prepared.
        /// </summary>
        public static void Begin(GameConfig config)
        {
            Restore();
            var shader = config != null ? config.waterShader : null;
            if (shader == null || !shader.isSupported) return;
            var made = new Dictionary<Material, Material>();
            var materials = new List<Material>();
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include))
            {
                if (r.GetComponentInParent<WorldSkyDome>(true) != null || r.GetComponentInParent<FollowCameraPosition>(true) != null) continue;
                r.GetSharedMaterials(materials);
                Material[] hd = null;
                for (var i = 0; i < materials.Count; i++)
                {
                    var m = materials[i];
                    if (m == null) continue;
                    if (!made.TryGetValue(m, out var copy))
                    {
                        copy = IsWaterBody(m) ? HdCopy(m, shader, config.waterWaves) : null;
                        made[m] = copy;
                    }
                    if (copy == null) continue;
                    hd ??= materials.ToArray();
                    hd[i] = copy;
                }
                if (hd == null) continue;
                swapped.Add((r, r.sharedMaterials));
                r.sharedMaterials = hd;
            }
        }

        /// <summary>Ends the race's HD water: the track's own water materials are back, the copies freed.</summary>
        public static void End() => Restore();

        /// <summary>
        /// A water body's material: MHK/Lit, fogged (not a sky) and not self-lit (not an effect), named water but not lava
        /// (<see cref="TrackWeather.HasWaterName"/>). A frame-animated texture named water on a material named otherwise is
        /// an effect, such as MHK 3 UFO's force fields.
        /// </summary>
        static bool IsWaterBody(Material m)
        {
            if (m.shader == null || m.shader.name != MHKLitIds.ShaderName) return false;
            if (m.GetFloat(MHKLitIds.Fog) < 0.5f || m.GetFloat(MHKLitIds.Emission) >= 0.5f) return false;
            if (!TrackWeather.HasWaterName(m, out var byTexture) || (byTexture && m.IsKeywordEnabled(MHKLitIds.FlipbookKeyword))) return false;
            var texture = m.GetTexture(MHKLitIds.BaseMap);
            return !LavaName.IsMatch(m.name) && (texture == null || !LavaName.IsMatch(texture.name));
        }

        static Material HdCopy(Material m, Shader shader, Texture2D waves)
        {
            var hd = new Material(shader) { name = m.name + " (HD water)", hideFlags = HideFlags.DontSave };
            hd.SetTexture(MHKLitIds.BaseMap, m.GetTexture(MHKLitIds.BaseMap));
            hd.SetTextureScale(MHKLitIds.BaseMap, m.GetTextureScale(MHKLitIds.BaseMap));
            hd.SetTextureOffset(MHKLitIds.BaseMap, m.GetTextureOffset(MHKLitIds.BaseMap));
            hd.SetColor(MHKLitIds.BaseColor, m.GetColor(MHKLitIds.BaseColor));
            hd.SetColor(MHKLitIds.Tint, m.GetColor(MHKLitIds.Tint));
            hd.SetVector(MHKLitIds.UVScroll, m.GetVector(MHKLitIds.UVScroll));
            if (m.IsKeywordEnabled(MHKLitIds.FlipbookKeyword))
            {
                hd.EnableKeyword(MHKLitIds.FlipbookKeyword);
                hd.SetFloat(MHKLitIds.FlipbookOn, 1f);
                hd.SetTexture(MHKLitIds.FlipbookArray, m.GetTexture(MHKLitIds.FlipbookArray));
                hd.SetVector(MHKLitIds.Flipbook, m.GetVector(MHKLitIds.Flipbook));
            }
            if (waves != null) hd.SetTexture(WaveMap, waves);
            hd.SetFloat(MHKLitIds.Fog, m.GetFloat(MHKLitIds.Fog));
            foreach (var id in new[] { MHKLitIds.SrcBlend, MHKLitIds.DstBlend, MHKLitIds.ZWrite, MHKLitIds.Cull })
                hd.SetFloat(id, m.GetFloat(id));
            hd.renderQueue = m.renderQueue;
            if (m.GetFloat(MHKLitIds.DstBlend) != (float)BlendMode.Zero) hd.SetOverrideTag("RenderType", "Transparent");
            copies.Add(hd);
            return hd;
        }

        static void Restore()
        {
            foreach (var (r, materials) in swapped)
                if (r != null) r.sharedMaterials = materials;
            swapped.Clear();
            foreach (var m in copies)
            {
                if (m == null) continue;
                if (Application.isPlaying) Object.Destroy(m);
                else Object.DestroyImmediate(m);
            }
            copies.Clear();
        }
    }
}
