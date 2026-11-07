using System.Collections.Generic;
using MHK.Lighting;
using MHK.Modding;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Race
{
    /// <summary>
    /// Applies the environment settings of a variant's overlays (<see cref="TrackOverlay"/>) to the loaded track scene: its
    /// sun and fill lights, ambient light, fog, camera background, baked light, sky textures and turned textures. Overlays
    /// apply in the order given (modifier order), each on top of the result of the ones before. Changed materials are
    /// replaced by copies, assets are never edited. The returned <see cref="State"/> restores everything (editor previews)
    /// or only frees the copies.
    /// </summary>
    public static class TrackEnvironment
    {
        /// <summary>What <see cref="Apply"/> changed, to put it back.</summary>
        public sealed class State
        {
            internal readonly List<(Light light, Color color, float intensity)> lights = new();
            internal readonly List<(BakedIndirectLighting baked, float intensity)> baked = new();
            internal readonly List<(Renderer renderer, Material[] materials, bool enabled)> renderers = new();
            internal readonly List<Material> copies = new();
            internal Scene scene;
            internal TrackRuntime runtime;
            internal Color background, ambientLight, fogColor;
            internal SphericalHarmonicsL2 ambientProbe;
            internal float fogDensity, fogStart, fogEnd;
            internal bool fog;
            internal FogMode fogMode;

            /// <summary>The first cover map of the overlays (what covers the track for wet options), null when none has one.</summary>
            public TrackCoverMap Cover { get; internal set; }

            /// <summary>Puts every changed value back and frees the material copies.</summary>
            public void Restore()
            {
                foreach (var (light, color, intensity) in lights)
                {
                    if (light == null) continue;
                    light.color = color;
                    light.intensity = intensity;
                }
                foreach (var (b, intensity) in baked)
                {
                    if (b == null) continue;
                    b.intensity = intensity;
                    if (b.isActiveAndEnabled) b.Apply();
                }
                // Last change first: a renderer changed twice (sky, then a flipped texture) ends as it was.
                for (var i = renderers.Count - 1; i >= 0; i--)
                {
                    var (r, materials, enabled) = renderers[i];
                    if (r == null) continue;
                    r.sharedMaterials = materials;
                    r.enabled = enabled;
                }
                if (runtime != null) runtime.backgroundColor = background;
                if (scene.IsValid() && scene.isLoaded)
                    WithActive(scene, () =>
                    {
                        RenderSettings.ambientLight = ambientLight;
                        RenderSettings.ambientProbe = ambientProbe;
                        RenderSettings.fog = fog;
                        RenderSettings.fogMode = fogMode;
                        RenderSettings.fogColor = fogColor;
                        RenderSettings.fogDensity = fogDensity;
                        RenderSettings.fogStartDistance = fogStart;
                        RenderSettings.fogEndDistance = fogEnd;
                    });
                Release();
            }

            /// <summary>Frees the material copies (the race's track scene is unloading).</summary>
            public void Release()
            {
                foreach (var m in copies)
                {
                    if (m == null) continue;
                    if (Application.isPlaying) Object.Destroy(m);
                    else Object.DestroyImmediate(m);
                }
                copies.Clear();
            }
        }

        /// <summary>
        /// Applies <paramref name="overlays"/> (in order, each with the option it belongs to) to <paramref name="trackScene"/>
        /// whose gameplay root is <paramref name="runtime"/>. <paramref name="variant"/> decides sky entries that require an
        /// option (null: those never apply).
        /// </summary>
        public static State Apply(Scene trackScene, TrackRuntime runtime, IReadOnlyList<(TrackOverlay overlay, TrackOptionDefinition option)> overlays,
            TrackVariant variant)
        {
            var state = new State { scene = trackScene, runtime = runtime };
            if (runtime != null) state.background = runtime.backgroundColor;
            WithActive(trackScene, () =>
            {
                state.ambientLight = RenderSettings.ambientLight;
                state.ambientProbe = RenderSettings.ambientProbe;
                state.fog = RenderSettings.fog;
                state.fogMode = RenderSettings.fogMode;
                state.fogColor = RenderSettings.fogColor;
                state.fogDensity = RenderSettings.fogDensity;
                state.fogStart = RenderSettings.fogStartDistance;
                state.fogEnd = RenderSettings.fogEndDistance;
            });
            if (overlays == null || overlays.Count == 0) return state;
            FindLights(trackScene, out var sun, out var fills);
            if (sun != null) state.lights.Add((sun, sun.color, sun.intensity));
            foreach (var f in fills) state.lights.Add((f, f.color, f.intensity));
            var baked = Components<BakedIndirectLighting>(trackScene);
            foreach (var b in baked) state.baked.Add((b, b.intensity));
            foreach (var (overlay, _) in overlays)
            {
                if (overlay == null) continue;
                if (state.Cover == null && overlay.cover != null) state.Cover = overlay.cover;
                ApplyLights(overlay, sun, fills);
                foreach (var b in baked) b.intensity = Mathf.Max(0f, overlay.bakedIndirect.Apply(b.intensity));
                if (runtime != null) runtime.backgroundColor = overlay.background.Apply(runtime.backgroundColor);
                WithActive(trackScene, () => ApplySceneSettings(overlay));
            }
            foreach (var b in baked)
                if (b.isActiveAndEnabled) b.Apply();
            ApplySky(trackScene, overlays, variant, state);
            ApplyFlips(trackScene, overlays, state);
            return state;
        }

        // The sun is the shadowed directional light that is not fully baked (as the light baker picks it; players only know
        // the bake's output), the other directional lights are fill lights. Shadows as authored: the graphics settings may
        // have turned them off.
        static void FindLights(Scene scene, out Light sun, out List<Light> fills)
        {
            sun = null;
            fills = new List<Light>();
            var directional = new List<Light>();
            foreach (var l in Components<Light>(scene))
                if (l.type == LightType.Directional) directional.Add(l);
            foreach (var l in directional)
                if (Core.SceneGraphics.AuthoredShadows(l) != LightShadows.None && l.bakingOutput.lightmapBakeType != LightmapBakeType.Baked) { sun = l; break; }
            if (sun == null)
                foreach (var l in directional)
                    if (Core.SceneGraphics.AuthoredShadows(l) != LightShadows.None) { sun = l; break; }
            if (sun == null && directional.Count > 0) sun = directional[0];
            foreach (var l in directional)
                if (l != sun) fills.Add(l);
        }

        static void ApplyLights(TrackOverlay overlay, Light sun, List<Light> fills)
        {
            if (sun != null)
            {
                sun.color = overlay.sunColor.Apply(sun.color);
                sun.intensity = Mathf.Max(0f, overlay.sunIntensity.Apply(sun.intensity));
            }
            foreach (var f in fills)
            {
                f.color = overlay.fillColor.Apply(f.color);
                f.intensity = Mathf.Max(0f, overlay.fillIntensity.Apply(f.intensity));
            }
        }

        // Runs with the track scene active: RenderSettings are the active scene's.
        static void ApplySceneSettings(TrackOverlay overlay)
        {
            switch (overlay.ambient.mode)
            {
                case OverrideMode.Set:
                {
                    RenderSettings.ambientLight = overlay.ambient.Apply(RenderSettings.ambientLight);
                    var sh = new SphericalHarmonicsL2();
                    sh.AddAmbientLight(overlay.ambient.value);
                    RenderSettings.ambientProbe = sh;
                    break;
                }
                case OverrideMode.Multiply:
                {
                    RenderSettings.ambientLight = overlay.ambient.Apply(RenderSettings.ambientLight);
                    var sh = RenderSettings.ambientProbe;
                    for (var c = 0; c < 3; c++)
                    for (var i = 0; i < 9; i++)
                        sh[c, i] *= overlay.ambient.value[c];
                    RenderSettings.ambientProbe = sh;
                    break;
                }
            }
            if (overlay.enableFog && !RenderSettings.fog)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogStartDistance = overlay.fogStart;
                RenderSettings.fogEndDistance = Mathf.Max(overlay.fogStart + 1f, overlay.fogEnd);
            }
            RenderSettings.fogColor = overlay.fogColor.Apply(RenderSettings.fogColor);
            var distance = overlay.fogDistance.Apply(1f);
            if (overlay.fogDistance.mode == OverrideMode.Keep || distance <= 0f) return;
            if (RenderSettings.fogMode == FogMode.Linear)
            {
                RenderSettings.fogStartDistance *= distance;
                RenderSettings.fogEndDistance *= distance;
            }
            else
            {
                RenderSettings.fogDensity /= distance;
            }
        }

        // Every renderer of the track scene drawing a material an overlay lists in flipTextures gets a copy whose texture is
        // turned by 180° (tiling and offset mirrored on both axes) and whose UV scroll runs the other way. A material listed
        // by two overlays turns twice: back as it was.
        static void ApplyFlips(Scene scene, IReadOnlyList<(TrackOverlay overlay, TrackOptionDefinition option)> overlays, State state)
        {
            var flips = new List<Material>();
            foreach (var (overlay, _) in overlays)
            {
                if (overlay == null) continue;
                foreach (var m in overlay.flipTextures)
                {
                    if (m == null) continue;
                    var index = flips.FindIndex(f => f == m || f.name == m.name);
                    if (index >= 0) flips.RemoveAt(index);
                    else flips.Add(m);
                }
            }
            if (flips.Count == 0) return;
            var made = new Dictionary<Material, Material>();
            foreach (var r in Components<Renderer>(scene))
            {
                var materials = r.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || !flips.Exists(f => f == source || f.name == source.name)) continue;
                    if (!made.TryGetValue(source, out var copy))
                    {
                        copy = new Material(source) { name = source.name };
                        if (copy.HasProperty(MHKLitIds.BaseMap))
                        {
                            var scale = copy.GetTextureScale(MHKLitIds.BaseMap);
                            var offset = copy.GetTextureOffset(MHKLitIds.BaseMap);
                            copy.SetTextureScale(MHKLitIds.BaseMap, -scale);
                            copy.SetTextureOffset(MHKLitIds.BaseMap, Vector2.one - offset);
                        }
                        if (copy.HasProperty(MHKLitIds.UVScroll)) copy.SetVector(MHKLitIds.UVScroll, -copy.GetVector(MHKLitIds.UVScroll));
                        made[source] = copy;
                        state.copies.Add(copy);
                    }
                    if (!changed) state.renderers.Add((r, r.sharedMaterials, r.enabled));
                    changed = true;
                    materials[i] = copy;
                }
                if (changed) r.sharedMaterials = materials;
            }
        }

        // Per sky material, the matching entry that wins: one requiring a selected option beats one without, a later
        // overlay beats an earlier one. Hidden renderers are switched off when every material of theirs is hidden.
        static void ApplySky(Scene scene, IReadOnlyList<(TrackOverlay overlay, TrackOptionDefinition option)> overlays, TrackVariant variant, State state)
        {
            var made = new Dictionary<(Material, SkyTexture), Material>();
            foreach (var r in SkyRenderers(scene))
            {
                var materials = r.sharedMaterials;
                var changed = false;
                var hidden = 0;
                for (var i = 0; i < materials.Length; i++)
                {
                    var entry = Pick(materials[i], overlays, variant);
                    if (entry == null) continue;
                    if (entry.hide)
                    {
                        hidden++;
                        continue;
                    }
                    if (!made.TryGetValue((materials[i], entry), out var copy))
                    {
                        copy = new Material(materials[i]) { name = materials[i].name };
                        if (entry.texture != null)
                        {
                            copy.SetTexture(MHKLitIds.BaseMap, entry.texture);
                            copy.mainTexture = entry.texture;
                        }
                        if (copy.HasProperty(MHKLitIds.BaseColor)) copy.SetColor(MHKLitIds.BaseColor, copy.GetColor(MHKLitIds.BaseColor) * entry.tint);
                        made[(materials[i], entry)] = copy;
                        state.copies.Add(copy);
                    }
                    if (!changed) state.renderers.Add((r, r.sharedMaterials, r.enabled));
                    changed = true;
                    materials[i] = copy;
                }
                if (changed) r.sharedMaterials = materials;
                if (hidden == 0 || hidden < materials.Length) continue;
                if (!changed) state.renderers.Add((r, r.sharedMaterials, r.enabled));
                r.enabled = false;
            }
        }

        static SkyTexture Pick(Material material, IReadOnlyList<(TrackOverlay overlay, TrackOptionDefinition option)> overlays, TrackVariant variant)
        {
            if (material == null) return null;
            SkyTexture plain = null, conditional = null;
            foreach (var (overlay, _) in overlays)
            {
                if (overlay == null) continue;
                foreach (var e in overlay.skyTextures)
                {
                    if (e == null || e.material == null) continue;
                    if (e.material != material && e.material.name != material.name) continue;
                    if (string.IsNullOrEmpty(e.requiresOption)) plain = e;
                    else if (variant != null && variant.Has(e.requiresOption)) conditional = e;
                }
            }
            return conditional ?? plain;
        }

        static IEnumerable<Renderer> SkyRenderers(Scene scene)
        {
            var seen = new HashSet<Renderer>();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var dome in root.GetComponentsInChildren<FollowCameraPosition>(true))
                foreach (var r in dome.GetComponentsInChildren<Renderer>(true))
                    if (seen.Add(r)) yield return r;
                foreach (var dome in root.GetComponentsInChildren<WorldSkyDome>(true))
                foreach (var r in dome.GetComponentsInChildren<Renderer>(true))
                    if (seen.Add(r)) yield return r;
            }
        }

        static List<T> Components<T>(Scene scene) where T : Component
        {
            var list = new List<T>();
            if (!scene.IsValid() || !scene.isLoaded) return list;
            foreach (var root in scene.GetRootGameObjects()) list.AddRange(root.GetComponentsInChildren<T>(true));
            return list;
        }

        static void WithActive(Scene scene, System.Action action)
        {
            var active = SceneManager.GetActiveScene();
            var swap = scene.IsValid() && scene.isLoaded && active != scene;
            if (swap) SceneManager.SetActiveScene(scene);
            try
            {
                action();
            }
            finally
            {
                if (swap && active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
            }
        }
    }
}
