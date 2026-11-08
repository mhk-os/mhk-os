using System.Collections.Generic;
using System.Text.RegularExpressions;
using MHK.Modding;
using MHK.Rendering;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Weather of the running race, from its variant's rules: the outdoor tyre grip (<see cref="TrackVariant.OutdoorGrip"/>)
    /// for kart physics and computer drivers, and the wet look of MHK/Lit (<see cref="TrackVariant.Wetness"/>) through
    /// shader globals. Both apply only where the track's <see cref="TrackCoverMap"/> shows nothing overhead (the wet look
    /// blends over the cover's edge along smooth lines). The grip reads the baked map on the CPU, so every machine
    /// computes the same. Outside a race (menus, the editor) all is dry. Water
    /// and lava never look wet: MHK/Lit leaves out frame-animated, UV-scrolled and blended materials itself, and for a wet
    /// race the opaque still ones (named so: <see cref="IsWaterMaterial"/>) are swapped for copies with <c>_WetMask</c> 0.
    /// </summary>
    public static partial class TrackWeather
    {
        /// <summary>
        /// Metres the cover map's topmost surface must lie above a point to cover it: the ground itself, kerbs and slopes
        /// within a texel stay uncovered.
        /// </summary>
        public const float CoverBias = 0.5f;
        // Wetness from which puddles form; they reach their full amount at wetness 1.
        const float PuddleStart = 0.4f;

        [AutoStaticsCleanup] static float grip = 1f;
        [AutoStaticsCleanup] static float wetness;
        [AutoStaticsCleanup] static TrackCoverMap cover;
        // Renderers given dry water copies (with their own materials) and the copies, while a wet race runs.
        [AutoStaticsCleanup] static readonly List<(Renderer renderer, Material[] materials)> driedRenderers = new();
        [AutoStaticsCleanup] static readonly List<Material> dryCopies = new();

        // Water and lava words of the ported games (German and English). A material is water when one word of its name is
        // made of them ("boden_wasser", "water1_18", "park_teich", "04_lava"; not "wasserturm"), and so is one whose texture
        // name consists of nothing else ("wasser_000", "waterwater"; not Docklands' "water_platten3" road).
        const string WaterWords = "(?:wasser|water|sea|meer|ocean|teich|pond|lake|lava|magma)+";
        static readonly Regex WaterMaterialName = new(@"(?<!\p{L})" + WaterWords + @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex WaterTextureName = new(@"^\P{L}*" + WaterWords + @"(?:\P{L}+" + WaterWords + @")*\P{L}*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Uncovered surfaces look wet (the wet look of MHK/Lit is on).</summary>
        public static bool Wet => wetness > 0f;

        /// <summary>
        /// Starts the weather of a race on <paramref name="variant"/> (null = dry), with <paramref name="coverMap"/> telling
        /// what is covered (null = nothing). Call after the variant's overlays loaded and before the karts spawn: computer
        /// drivers read the grip into their racing lines once.
        /// </summary>
        public static void Begin(TrackVariant variant, TrackCoverMap coverMap)
        {
            grip = variant != null ? Mathf.Clamp01(variant.OutdoorGrip) : 1f;
            wetness = variant != null ? Mathf.Clamp01(variant.Wetness) : 0f;
            cover = coverMap != null && coverMap.Prepare() ? coverMap : null;
            if (coverMap != null && cover == null)
                Debug.LogWarning($"TrackWeather: cover map '{coverMap.name}' has no readable R16 heights, nothing counts as covered.", coverMap);
            RestoreWater();
            if (wetness > 0f) DryWater();
            Upload();
        }

        /// <summary>Ends the race's weather: full grip and dry surfaces everywhere.</summary>
        public static void End()
        {
            grip = 1f;
            wetness = 0f;
            cover = null;
            RestoreWater();
            Upload();
        }

        /// <summary>
        /// Tyre grip multiplier at <paramref name="worldPosition"/>: the variant's outdoor grip where nothing covers it, 1
        /// under cover and without grip-reducing options.
        /// </summary>
        public static float OutdoorGrip(Vector3 worldPosition)
        {
            if (grip >= 1f) return 1f;
            return cover != null && cover.Covered(worldPosition, CoverBias) ? 1f : grip;
        }

        /// <summary>
        /// An opaque, still MHK/Lit water or lava material (by its or its texture's name) that the wet look must leave out.
        /// MHK/Lit itself leaves out frame-animated, UV-scrolled and blended materials, and ones with <c>_WetMask</c> 0.
        /// </summary>
        static bool IsWaterMaterial(Material m)
        {
            if (m == null || m.shader == null || m.shader.name != MHKLitIds.ShaderName) return false;
            if (m.GetFloat(MHKLitIds.WetMask) <= 0f || m.GetFloat(MHKLitIds.DstBlend) != 0f || m.IsKeywordEnabled(MHKLitIds.FlipbookKeyword)) return false;
            var scroll = m.GetVector(MHKLitIds.UVScroll);
            if (scroll.x != 0f || scroll.y != 0f) return false;
            return HasWaterName(m, out _);
        }

        /// <summary>
        /// Whether <paramref name="m"/> shows water or lava by name: a word of the material's name is made of water and lava
        /// words, or else (<paramref name="byTexture"/>) its texture's name consists of nothing else.
        /// </summary>
        internal static bool HasWaterName(Material m, out bool byTexture)
        {
            byTexture = false;
            if (WaterMaterialName.IsMatch(m.name)) return true;
            var texture = m.GetTexture(MHKLitIds.BaseMap);
            byTexture = texture != null && WaterTextureName.IsMatch(texture.name);
            return byTexture;
        }

        // Every loaded renderer showing water gets a copy of that material with _WetMask 0 (one copy per material).
        static void DryWater()
        {
            var copies = new Dictionary<Material, Material>();
            var materials = new List<Material>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                r.GetSharedMaterials(materials);
                Material[] dried = null;
                for (var i = 0; i < materials.Count; i++)
                {
                    var m = materials[i];
                    if (m == null) continue;
                    if (!copies.TryGetValue(m, out var dry))
                    {
                        dry = IsWaterMaterial(m) ? DryCopy(m) : null;
                        copies[m] = dry;
                    }
                    if (dry == null) continue;
                    dried ??= materials.ToArray();
                    dried[i] = dry;
                }
                if (dried == null) continue;
                driedRenderers.Add((r, r.sharedMaterials));
                r.sharedMaterials = dried;
            }
        }

        static Material DryCopy(Material m)
        {
            var dry = new Material(m) { name = m.name + " (dry)", hideFlags = HideFlags.DontSave };
            dry.SetFloat(MHKLitIds.WetMask, 0f);
            dryCopies.Add(dry);
            return dry;
        }

        // Puts the renderers' own materials back and frees the copies.
        static void RestoreWater()
        {
            foreach (var (r, materials) in driedRenderers)
                if (r != null) r.sharedMaterials = materials;
            driedRenderers.Clear();
            foreach (var m in dryCopies)
            {
                if (m == null) continue;
                if (Application.isPlaying) Object.Destroy(m);
                else Object.DestroyImmediate(m);
            }
            dryCopies.Clear();
        }

        static void Upload()
        {
            var puddles = Mathf.Clamp01((wetness - PuddleStart) / (1f - PuddleStart));
            Shader.SetGlobalVector(MHKLitIds.Wet, new Vector4(wetness, puddles, 0f, 0f));
            if (cover != null)
            {
                Shader.SetGlobalTexture(MHKLitIds.WetCover, cover.heights);
                Shader.SetGlobalVector(MHKLitIds.WetCoverRect, new Vector4(cover.min.x, cover.min.y, 1f / cover.size.x, 1f / cover.size.y));
                Shader.SetGlobalVector(MHKLitIds.WetCoverTexels, new Vector4(cover.heights.width, cover.heights.height, cover.size.x / cover.heights.width, 0f));
                Shader.SetGlobalVector(MHKLitIds.WetCoverHeight, new Vector4(cover.minY, cover.range, CoverBias, 1f));
                return;
            }
            // No reference to an unloaded track's texture stays behind.
            Shader.SetGlobalTexture(MHKLitIds.WetCover, Texture2D.blackTexture);
            Shader.SetGlobalVector(MHKLitIds.WetCoverHeight, Vector4.zero);
        }

#if UNITY_EDITOR
        // Edit mode never shows a race's weather, also when play mode stopped without the race ending it.
        [UnityEditor.InitializeOnLoadMethod]
        static void DryInEditMode()
        {
            End();
            UnityEditor.EditorApplication.playModeStateChanged += state =>
            {
                if (state == UnityEditor.PlayModeStateChange.EnteredEditMode) End();
            };
        }
#endif
    }
}
