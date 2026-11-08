using System.Collections.Generic;
using MHK.Core;
using MHK.Effects;
using MHK.Race;
using MHK.Rendering;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Application = UnityEngine.Device.Application;

namespace MHK.Karts
{
    /// <summary>
    /// Headlights of a kart visual, live kart or ghost: a glowing dot at each lamp and one realtime spot light lighting
    /// the road ahead. The lamps sit on the nose of the kart model (measured from its renderer bounds, the driver left
    /// out), so any model gets them, and ride the node carrying the chassis, so they follow its animations (power-start
    /// wheelie, crash reactions) as well as spins and the body bob. Every lit kart shows its lamps, but only the few lit
    /// karts nearest the race camera get their spot light (fewer on lower graphics tiers and mobile), so a full grid with
    /// ghosts stays within the visible light limit. Lamps and light go out while the kart is hidden; the lamps also while
    /// the kart model is not drawn (first-person view), where the light still shows the road. The object has no colliders.
    /// </summary>
    public sealed partial class KartHeadlights : MonoBehaviour
    {
        const float LightRange = 60f;
        // A wide, even beam: as bright across most of the cone, softening only at its edge, so it lights what the chase
        // camera sees around the kart, not just a spot ahead.
        const float SpotAngle = 110f, InnerSpotAngle = 90f;
        // MHK/Lit is saturated Lambert with inverse-square falloff. The range only sets where the light is cut off, so
        // the beam fades out on its own and still reaches walls about 40 m ahead; brighter, the overlapping beams of a
        // pack saturate everything around it and night is gone.
        const float LightIntensity = 350f;
        // Down so that the cone's lower edge still meets the road just ahead of the nose; the road far ahead stays in the
        // even inner cone and the upper cone reaches the horizon.
        const float LightPitchDegrees = 15f;
        // The spot sits this high above the lamps and this far ahead of the model. On the road the light falls with the
        // cube of the distance (inverse square times the grazing angle), so its height decides how far ahead the road
        // is lit: from about 3 m up it reaches about 20 m, where walls and karts ahead keep their brightness. It never
        // lights the kart's own front.
        const float LightRaise = 3f, LightAhead = 0.1f;
        // Lamps: a third of the half width either side of the centre line, raised from the front wheel centres towards
        // the model's middle and set back a little from its frontmost point, so they sit on the nose.
        const float LampHeightShare = 0.3f, LampRaiseShare = 0.3f, LampSpacingShare = 1f / 3f, LampBackShare = 0.02f;
        const float GlowSizeShare = 0.2f, MinGlowSize = 0.15f, MaxGlowSize = 0.5f;
        const int GlowTextureSize = 64;
        const float HiddenOpacity = 0.01f;
        // A kart keeps its spot light until another lit kart is this much nearer, so lights do not flicker between karts.
        const float KeepFactor = 1.2f;
        const int HighTierLights = 8, MediumTierLights = 6, LowTierLights = 4;

        static readonly Color LightColour = new(1f, 0.94f, 0.82f);
        static readonly Color GlowColour = new(1f, 0.95f, 0.85f);
        // Kart-space box measured when neither a model nor a physics profile is there.
        static readonly Bounds DefaultBounds = new(new Vector3(0f, 0.3f, 0f), new Vector3(1f, 0.5f, 1.6f));

        [AutoStaticsCleanup] static readonly List<KartHeadlights> lit = new();
        [AutoStaticsCleanup] static readonly List<float> scores = new();
        [AutoStaticsCleanup] static int refreshedFrame;
        [AutoStaticsCleanup] static Material glowMaterial;
        [AutoStaticsCleanup] static Texture2D glowTexture;
        [AutoStaticsCleanup] static Mesh glowMesh;

        KartVisual visual;
        IKartView view;
        // The largest kart model renderer: the lamps show while it is drawn.
        Renderer modelRenderer;
        Light spot;
        MeshRenderer[] glows;
        MaterialPropertyBlock block;
        float shownOpacity = 1f;
        bool granted;

        /// <summary>The headlights are switched on.</summary>
        public bool On { get; private set; }

        /// <summary>
        /// Puts switched-off headlights (child "Headlights") on the nose of the kart model's mesh renderers below
        /// <paramref name="body"/> other than the driver's (<paramref name="driver"/>, may be null), measured in the
        /// model's pose as instantiated. Without such renderers the chassis box of <paramref name="profile"/> places them.
        /// Lamp height follows the front wheels of <paramref name="model"/> when it has some. They hang on the node
        /// carrying the chassis (see <see cref="Chassis"/>), keeping that placement as it moves.
        /// </summary>
        internal static KartHeadlights Build(KartVisual visual, IKartView view, Transform body, KartModel model, Transform driver,
            KartPhysicsProfile profile)
        {
            var renderers = ModelRenderers(body, driver);
            var bounds = Measure(body, renderers, profile);
            var largest = Largest(renderers);
            var go = new GameObject("Headlights");
            go.SetActive(false);
            go.transform.SetParent(body, false);
            var h = go.AddComponent<KartHeadlights>();
            h.visual = visual;
            h.view = view;
            h.modelRenderer = largest;
            h.block = new MaterialPropertyBlock();
            var lampY = LampHeight(body, model, bounds);
            h.Assemble(bounds, lampY);
            var nose = new Vector3(bounds.center.x, lampY, bounds.max.z);
            go.transform.SetParent(Chassis(body, model, largest, nose), true);
            return h;
        }

        /// <summary>Switches the headlights on or off.</summary>
        public void SetOn(bool on)
        {
            if (on == On) return;
            On = on;
            gameObject.SetActive(on);
        }

        void Assemble(Bounds b, float lampY)
        {
            var front = b.max.z;
            var lampZ = front - b.size.z * LampBackShare;
            var spotObject = new GameObject("Spot");
            spotObject.transform.SetParent(transform, false);
            spotObject.transform.SetLocalPositionAndRotation(new Vector3(b.center.x, lampY + LightRaise, front + LightAhead),
                Quaternion.Euler(LightPitchDegrees, 0f, 0f));
            spot = spotObject.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.range = LightRange;
            spot.spotAngle = SpotAngle;
            spot.innerSpotAngle = InnerSpotAngle;
            spot.color = LightColour;
            spot.intensity = LightIntensity;
            spot.shadows = LightShadows.None;
            spot.enabled = false;

            var material = GlowMaterial();
            if (material == null)
            {
                glows = System.Array.Empty<MeshRenderer>();
                return;
            }
            var size = Mathf.Clamp(b.size.x * GlowSizeShare, MinGlowSize, MaxGlowSize);
            var spacing = b.extents.x * LampSpacingShare;
            glows = new[]
            {
                AddGlow("Lamp L", new Vector3(b.center.x - spacing, lampY, lampZ), size, material),
                AddGlow("Lamp R", new Vector3(b.center.x + spacing, lampY, lampZ), size, material),
            };
        }

        MeshRenderer AddGlow(string objectName, Vector3 position, float size, Material material)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = position;
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = GlowMesh();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            go.AddComponent<Billboard>();
            return r;
        }

        void OnEnable()
        {
            lit.Add(this);
            granted = false;
            spot.enabled = false;
        }

        void OnDisable() => lit.Remove(this);

        // The first lit headlights to update each frame hand out the spot lights for all of them.
        void LateUpdate()
        {
            if (Time.frameCount == refreshedFrame) return;
            refreshedFrame = Time.frameCount;
            RefreshAll();
        }

        static void RefreshAll()
        {
            var race = RaceManager.Current;
            var cam = race != null ? race.RaceCamera : Camera.main;
            var eye = cam != null ? cam.transform.position : Vector3.zero;
            scores.Clear();
            for (var i = 0; i < lit.Count; i++)
            {
                var h = lit[i];
                var keep = h.granted ? KeepFactor * KeepFactor : 1f;
                scores.Add(cam != null && !h.Hidden ? (h.transform.position - eye).sqrMagnitude / keep : float.PositiveInfinity);
                h.granted = false;
            }
            var budget = LightBudget;
            for (var n = 0; n < budget; n++)
            {
                var nearest = -1;
                for (var i = 0; i < lit.Count; i++)
                    if (!lit[i].granted && scores[i] < float.PositiveInfinity && (nearest < 0 || scores[i] < scores[nearest])) nearest = i;
                if (nearest < 0) break;
                lit[nearest].granted = true;
            }
            for (var i = 0; i < lit.Count; i++) lit[i].Show();
        }

        /// <summary>Real spot lights at once: by graphics tier, halved on mobile (lower visible light limit).</summary>
        static int LightBudget
        {
            get
            {
                var count = GraphicsQuality.Tier switch
                {
                    GraphicsPreset.High => HighTierLights,
                    GraphicsPreset.Medium => MediumTierLights,
                    _ => LowTierLights,
                };
                return Application.isMobilePlatform ? count / 2 : count;
            }
        }

        bool Hidden => view.Hidden || visual.ShownOpacity <= HiddenOpacity;

        void Show()
        {
            var hidden = Hidden;
            var opacity = visual.ShownOpacity;
            spot.enabled = granted && !hidden;
            spot.intensity = LightIntensity * opacity;
            var lampsShown = !hidden && (modelRenderer == null || modelRenderer.enabled);
            for (var i = 0; i < glows.Length; i++) glows[i].enabled = lampsShown;
            if (!lampsShown || Mathf.Approximately(opacity, shownOpacity)) return;
            shownOpacity = opacity;
            // Additive: a fading kart's lamps fade by their colour. At full opacity the shared material draws them.
            if (opacity >= 1f)
            {
                for (var i = 0; i < glows.Length; i++) glows[i].SetPropertyBlock(null);
                return;
            }
            block.SetColor(MHKLitIds.BaseColor, GlowColour * opacity);
            for (var i = 0; i < glows.Length; i++) glows[i].SetPropertyBlock(block);
        }

        static List<Renderer> ModelRenderers(Transform body, Transform driver)
        {
            var found = new List<Renderer>();
            body.GetComponentsInChildren(found);
            found.RemoveAll(r => r is not (MeshRenderer or SkinnedMeshRenderer) || !r.enabled || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly
                                 || (driver != null && r.transform.IsChildOf(driver)));
            return found;
        }

        // Body-space box of the renderers' own bounds (tighter than their world boxes), else the chassis box.
        static Bounds Measure(Transform body, List<Renderer> renderers, KartPhysicsProfile profile)
        {
            var found = false;
            var b = default(Bounds);
            foreach (var r in renderers)
            {
                var space = r is SkinnedMeshRenderer s && s.rootBone != null ? s.rootBone : r.transform;
                var local = r.localBounds;
                for (var i = 0; i < 8; i++)
                {
                    var sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                    var p = body.InverseTransformPoint(space.TransformPoint(local.center + Vector3.Scale(local.extents, sign)));
                    if (found) b.Encapsulate(p);
                    else b = new Bounds(p, Vector3.zero);
                    found = true;
                }
            }
            if (found) return b;
            if (profile == null) return DefaultBounds;
            KartPhysics.ChassisBox(profile, out var centre, out var halfExtents);
            return new Bounds(centre, halfExtents * 2f);
        }

        // Front wheel centre height, else low on the model, never above the model's middle; then raised towards it.
        static float LampHeight(Transform body, KartModel model, Bounds b)
        {
            var sum = 0f;
            var count = 0;
            if (model != null)
                foreach (var w in model.frontWheels)
                {
                    if (w == null) continue;
                    sum += body.InverseTransformPoint(w.position).y;
                    count++;
                }
            var y = count > 0 ? sum / count : Mathf.Lerp(b.min.y, b.max.y, LampHeightShare);
            return Mathf.Lerp(Mathf.Clamp(y, b.min.y, b.center.y), b.center.y, LampRaiseShare);
        }

        /// <summary>
        /// The node carrying the chassis, which kart animations and reactions move: the largest model renderer's own
        /// transform for a plain mesh. For a skinned model the nearest common parent of the model's wheels (they hang on
        /// the chassis) when it is one of its bones, else its bone nearest <paramref name="nose"/> (body space). The body
        /// without a model renderer.
        /// </summary>
        static Transform Chassis(Transform body, KartModel model, Renderer largest, Vector3 nose)
        {
            if (largest is not SkinnedMeshRenderer skinned) return largest != null ? largest.transform : body;
            var bones = skinned.bones;
            var wheelParent = WheelParent(model);
            if (wheelParent != null && System.Array.IndexOf(bones, wheelParent) >= 0) return wheelParent;
            Transform nearest = null;
            var nearestSq = float.PositiveInfinity;
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                var sq = (body.InverseTransformPoint(bone.position) - nose).sqrMagnitude;
                if (sq >= nearestSq) continue;
                nearest = bone;
                nearestSq = sq;
            }
            if (nearest != null) return nearest;
            return skinned.rootBone != null ? skinned.rootBone : skinned.transform;
        }

        // Nearest common parent of the model's wheels, null with fewer than two.
        static Transform WheelParent(KartModel model)
        {
            if (model == null) return null;
            Transform common = null;
            var count = 0;
            foreach (var wheels in new[] { model.frontWheels, model.rearWheels })
                foreach (var w in wheels)
                {
                    if (w == null) continue;
                    count++;
                    if (common == null) common = w.parent;
                    while (common != null && !w.IsChildOf(common)) common = common.parent;
                }
            return count >= 2 ? common : null;
        }

        static Renderer Largest(List<Renderer> renderers)
        {
            Renderer largest = null;
            var largestSize = -1f;
            foreach (var r in renderers)
            {
                var size = r.localBounds.size.sqrMagnitude;
                if (size <= largestSize) continue;
                largest = r;
                largestSize = size;
            }
            return largest;
        }

        // MHK/Lit as built with the game, additive, fully self-lit and unfogged (fog would add its colour to the black rim),
        // with a soft dot. Null without the shader in the config.
        static Material GlowMaterial()
        {
            if (glowMaterial != null) return glowMaterial;
            var config = GameRoot.Instance != null ? GameRoot.Instance.Config : null;
            var shader = config != null ? config.litShader : null;
            if (shader == null) return null;
            glowMaterial = new Material(shader) { name = "Headlight glow", renderQueue = (int)RenderQueue.Transparent };
            glowMaterial.SetTexture(MHKLitIds.BaseMap, GlowTexture());
            glowMaterial.SetColor(MHKLitIds.BaseColor, GlowColour);
            glowMaterial.SetFloat(MHKLitIds.Emission, 1f);
            glowMaterial.SetFloat(MHKLitIds.SrcBlend, (float)BlendMode.One);
            glowMaterial.SetFloat(MHKLitIds.DstBlend, (float)BlendMode.One);
            glowMaterial.SetFloat(MHKLitIds.ZWrite, 0f);
            glowMaterial.SetFloat(MHKLitIds.Cull, (float)CullMode.Off);
            glowMaterial.SetFloat(MHKLitIds.Fog, 0f);
            glowMaterial.SetOverrideTag("RenderType", "Transparent");
            return glowMaterial;
        }

        // Linear grey dot, bright in the middle and black at the rim (additive: black adds nothing).
        static Texture2D GlowTexture()
        {
            if (glowTexture != null) return glowTexture;
            glowTexture = new Texture2D(GlowTextureSize, GlowTextureSize, TextureFormat.RGBA32, false, true)
            {
                name = "Headlight glow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[GlowTextureSize * GlowTextureSize];
            var half = (GlowTextureSize - 1) * 0.5f;
            for (var y = 0; y < GlowTextureSize; y++)
                for (var x = 0; x < GlowTextureSize; x++)
                {
                    var d = Mathf.Clamp01(new Vector2(x - half, y - half).magnitude / half);
                    var v = Mathf.SmoothStep(1f, 0f, d);
                    var b = (byte)Mathf.RoundToInt(v * v * 255f);
                    pixels[y * GlowTextureSize + x] = new Color32(b, b, b, b);
                }
            glowTexture.SetPixels32(pixels);
            glowTexture.Apply(false, true);
            return glowTexture;
        }

        static Mesh GlowMesh()
        {
            if (glowMesh != null) return glowMesh;
            glowMesh = new Mesh
            {
                name = "Headlight glow",
                vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
                uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
            };
            return glowMesh;
        }
    }
}
