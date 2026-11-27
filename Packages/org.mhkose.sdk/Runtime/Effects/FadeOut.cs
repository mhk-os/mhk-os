using System.Collections.Generic;
using MHK.Rendering;
using UnityEngine;
using UnityEngine.Pool;
using BlendMode = UnityEngine.Rendering.BlendMode;
using RenderQueue = UnityEngine.Rendering.RenderQueue;
using ShadowCastingMode = UnityEngine.Rendering.ShadowCastingMode;

namespace MHK.Effects
{
    /// <summary>
    /// A component below a fading object that draws itself (<see cref="AfterimageTrail"/>). It hears how much of the
    /// object is still shown every frame of the fade.
    /// </summary>
    public interface IFadeOutListener
    {
        /// <summary>Called every frame of the fade, from 1 (fully shown) down to 0 (gone).</summary>
        void OnFadeOut(float visibility);
    }

    /// <summary>
    /// Takes a visual away gently instead of letting it vanish (an effect that ended, a powerup object that despawned).
    /// Particle systems stop emitting and let their live particles finish, and trails stop growing. See-through renderers
    /// fade to transparent while opaque ones shrink to nothing. Lights dim and sounds fade with them. Colliders go at
    /// once, and the object is destroyed when nothing of it is left to see.
    /// </summary>
    [AddComponentMenu("")]
    public sealed class FadeOut : MonoBehaviour
    {
        /// <summary>Seconds a fade takes unless the caller says otherwise.</summary>
        public const float DefaultSeconds = 0.4f;
        // Live particles and trails that outlast the fade are cut off this long after it started (s).
        const float MaxLinger = 5f;

        struct FadedColour
        {
            public Renderer renderer;
            public int index;
            public int property;
            public Color colour;
            // Additive or premultiplied blending, so the colour fades along with the alpha.
            public bool fadeRgb;
            // A copy only these remains use, faded directly instead of through a property block.
            public Material owned;
        }

        struct FadedSprite
        {
            public SpriteRenderer renderer;
            public Color colour;
        }

        struct FadedLight
        {
            public Light light;
            public float intensity;
        }

        struct FadedSound
        {
            public AudioSource source;
            public float volume;
        }

        static readonly int TintColor = Shader.PropertyToID("_TintColor");

        readonly List<FadedColour> colours = new();
        readonly List<FadedSprite> sprites = new();
        readonly List<FadedLight> lights = new();
        readonly List<FadedSound> sounds = new();
        readonly List<ParticleSystem> particles = new();
        readonly List<IFadeOutListener> listeners = new();
        // Meshes and materials made for the remains, destroyed with them.
        readonly List<Object> owned = new();
        MaterialPropertyBlock block;
        float duration = DefaultSeconds, delay, age, linger;
        bool started, shrink, gone;

        /// <summary>
        /// Fades <paramref name="visual"/> out where it is, still following its parent (an effect on a kart), and destroys
        /// it afterwards. It is moved below a new pivot object that does the shrinking, so scripts animating the visual's
        /// own scale (<see cref="Pulse"/>) keep running. The fade starts after <paramref name="startDelay"/> seconds.
        /// Returns the fade, null for no visual. A visual already fading keeps its fade.
        /// </summary>
        public static FadeOut Begin(GameObject visual, float seconds = DefaultSeconds, float startDelay = 0f)
        {
            if (visual == null) return null;
            var t = visual.transform;
            if (t.parent != null && t.parent.TryGetComponent<FadeOut>(out var running)) return running;
            var pivot = new GameObject(visual.name + " (fading)") { layer = visual.layer }.transform;
            pivot.SetParent(t.parent, false);
            pivot.SetPositionAndRotation(t.position, t.rotation);
            t.SetParent(pivot, true);
            var fade = pivot.gameObject.AddComponent<FadeOut>();
            fade.duration = Mathf.Max(0.01f, seconds);
            fade.delay = Mathf.Max(0f, startDelay);
            if (fade.delay <= 0f) fade.Setup();
            return fade;
        }

        /// <summary>
        /// Leaves fading remains of <paramref name="owner"/>, a powerup object about to be removed, under
        /// <see cref="Powerups.PowerupFx.Root"/>. Its particle systems and trails move over and finish there, and still
        /// copies of the renderers seen last frame fade out. The owner itself (colliders, scripts) is left alone for the
        /// caller to remove at once. Nothing happens for an inactive owner or outside play mode. Returns the remains, null
        /// when nothing was left.
        /// </summary>
        public static FadeOut LeaveRemains(GameObject owner, float seconds = DefaultSeconds)
        {
            if (owner == null || !owner.activeInHierarchy || !Application.isPlaying) return null;
            var source = owner.transform;
            var remains = new GameObject(owner.name + " (remains)") { layer = owner.layer };
            var root = remains.transform;
            root.SetParent(Powerups.PowerupFx.Root, false);
            root.SetPositionAndRotation(source.position, source.rotation);
            var fade = remains.AddComponent<FadeOut>();
            fade.duration = Mathf.Max(0.01f, seconds);
            var left = MoveParticlesAndTrails(source, root);
            left |= fade.CopyRenderers(source, root);
            left |= CopyLoops(source, root);
            if (!left)
            {
                Destroy(remains);
                return null;
            }
            fade.Setup();
            return fade;
        }

        // Particle systems (the topmost of each branch) and trails below the owner move to the remains as they are.
        static bool MoveParticlesAndTrails(Transform owner, Transform remains)
        {
            var moved = false;
            using (ListPool<ParticleSystem>.Get(out var systems))
            {
                owner.GetComponentsInChildren(systems);
                foreach (var ps in systems)
                {
                    var t = ps.transform;
                    if (t == owner || (ps.particleCount == 0 && !ps.isEmitting) || BelowOtherSystem(t, owner)) continue;
                    t.SetParent(remains, true);
                    moved = true;
                }
            }
            using (ListPool<TrailRenderer>.Get(out var trails))
            {
                owner.GetComponentsInChildren(trails);
                foreach (var tr in trails)
                {
                    if (tr.transform == owner || !tr.enabled || tr.positionCount == 0) continue;
                    tr.transform.SetParent(remains, true);
                    moved = true;
                }
            }
            return moved;
        }

        // Sub-emitters and nested systems go with the topmost system of their branch below the owner.
        static bool BelowOtherSystem(Transform t, Transform owner)
        {
            for (var p = t.parent; p != null && p != owner; p = p.parent)
                if (p.TryGetComponent<ParticleSystem>(out _)) return true;
            return false;
        }

        // Still copies of the mesh and sprite renderers below the owner that were on screen last frame. Materials and
        // readable meshes are copied too, since the owner may destroy the ones it made itself (ring and mound meshes).
        bool CopyRenderers(Transform owner, Transform remains)
        {
            var copied = false;
            using (ListPool<Renderer>.Get(out var renderers))
            {
                owner.GetComponentsInChildren(renderers);
                foreach (var r in renderers)
                {
                    if (!r.enabled || !r.isVisible || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    var t = r.transform;
                    switch (r)
                    {
                        case MeshRenderer when r.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null:
                        {
                            var copy = NewPart(t.name, r.gameObject.layer, remains, t.position, t.rotation, t.lossyScale);
                            var mesh = filter.sharedMesh;
                            if (mesh.isReadable) mesh = Own(Instantiate(mesh));
                            copy.AddComponent<MeshFilter>().sharedMesh = mesh;
                            CopyLook(r, copy.AddComponent<MeshRenderer>());
                            copied = true;
                            break;
                        }
                        case SkinnedMeshRenderer skinned when skinned.sharedMesh != null:
                        {
                            var mesh = new Mesh { name = skinned.sharedMesh.name + " (remains)" };
                            skinned.BakeMesh(mesh, true);
                            Own(mesh);
                            var copy = NewPart(t.name, r.gameObject.layer, remains, t.position, t.rotation, Vector3.one);
                            copy.AddComponent<MeshFilter>().sharedMesh = mesh;
                            CopyLook(r, copy.AddComponent<MeshRenderer>());
                            copied = true;
                            break;
                        }
                        case SpriteRenderer sprite when sprite.sprite != null:
                        {
                            var copy = NewPart(t.name, r.gameObject.layer, remains, t.position, t.rotation, t.lossyScale);
                            var s = copy.AddComponent<SpriteRenderer>();
                            s.sprite = sprite.sprite;
                            s.color = sprite.color;
                            s.flipX = sprite.flipX;
                            s.flipY = sprite.flipY;
                            s.drawMode = sprite.drawMode;
                            s.size = sprite.size;
                            s.sharedMaterial = sprite.sharedMaterial;
                            s.sortingLayerID = sprite.sortingLayerID;
                            s.sortingOrder = sprite.sortingOrder;
                            copied = true;
                            break;
                        }
                    }
                }
            }
            return copied;
        }

        static GameObject NewPart(string name, int layer, Transform parent, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var go = new GameObject(name) { layer = layer };
            var t = go.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(position, rotation);
            t.localScale = scale;
            return go;
        }

        void CopyLook(Renderer from, Renderer to)
        {
            var materials = from.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
                if (materials[i] != null) materials[i] = Own(new Material(materials[i]));
            to.sharedMaterials = materials;
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.renderingLayerMask = from.renderingLayerMask;
            if (!from.HasPropertyBlock()) return;
            block ??= new MaterialPropertyBlock();
            from.GetPropertyBlock(block);
            to.SetPropertyBlock(block);
            block.Clear();
        }

        T Own<T>(T asset) where T : Object
        {
            owned.Add(asset);
            return asset;
        }

        // Looping sounds below the owner go on in the remains from where they are and fade there.
        static bool CopyLoops(Transform owner, Transform remains)
        {
            var copied = false;
            using (ListPool<AudioSource>.Get(out var sources))
            {
                owner.GetComponentsInChildren(sources);
                foreach (var s in sources)
                {
                    if (!s.enabled || !s.isPlaying || !s.loop || s.clip == null) continue;
                    var copy = remains.gameObject.AddComponent<AudioSource>();
                    copy.playOnAwake = false;
                    copy.loop = true;
                    copy.clip = s.clip;
                    copy.volume = s.volume;
                    copy.pitch = s.pitch;
                    copy.spatialBlend = s.spatialBlend;
                    copy.rolloffMode = s.rolloffMode;
                    copy.minDistance = s.minDistance;
                    copy.maxDistance = s.maxDistance;
                    copy.dopplerLevel = s.dopplerLevel;
                    copy.spread = s.spread;
                    copy.priority = s.priority;
                    copy.outputAudioMixerGroup = s.outputAudioMixerGroup;
                    copy.time = s.time;
                    copy.Play();
                    copied = true;
                }
            }
            return copied;
        }

        void Setup()
        {
            started = true;
            linger = duration;
            using (ListPool<Collider>.Get(out var colliders))
            {
                GetComponentsInChildren(colliders);
                foreach (var c in colliders) c.enabled = false;
            }
            using (ListPool<Rigidbody>.Get(out var bodies))
            {
                GetComponentsInChildren(bodies);
                foreach (var b in bodies)
                {
                    b.isKinematic = true;
                    b.detectCollisions = false;
                }
            }
            GetComponentsInChildren(particles);
            foreach (var ps in particles) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            using (ListPool<TrailRenderer>.Get(out var trails))
            {
                GetComponentsInChildren(trails);
                foreach (var tr in trails)
                {
                    tr.emitting = false;
                    linger = Mathf.Max(linger, tr.time);
                }
            }
            using (ListPool<Renderer>.Get(out var renderers))
            {
                GetComponentsInChildren(renderers);
                foreach (var r in renderers) CollectRenderer(r);
            }
            using (ListPool<Light>.Get(out var found))
            {
                GetComponentsInChildren(found);
                foreach (var l in found) lights.Add(new FadedLight { light = l, intensity = l.intensity });
            }
            using (ListPool<AudioSource>.Get(out var found))
            {
                GetComponentsInChildren(found);
                foreach (var s in found) sounds.Add(new FadedSound { source = s, volume = s.volume });
            }
            GetComponentsInChildren(listeners);
        }

        void CollectRenderer(Renderer r)
        {
            switch (r)
            {
                case ParticleSystemRenderer:
                case TrailRenderer:
                    return;
                case SpriteRenderer sprite:
                    sprites.Add(new FadedSprite { renderer = sprite, colour = sprite.color });
                    return;
                case LineRenderer:
                    shrink = true;
                    return;
            }
            var materials = r.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                var m = materials[i];
                if (m == null) continue;
                if (TryFadable(m, out var property, out var fadeRgb))
                    colours.Add(new FadedColour
                    {
                        renderer = r, index = i, property = property, colour = m.GetColor(property), fadeRgb = fadeRgb,
                        owned = owned.Contains(m) ? m : null,
                    });
                else shrink = true;
            }
        }

        // A blended material with a colour property fades by its alpha (and its colour when it adds up). Opaque and
        // cut-out ones cannot, so the visual shrinks.
        static bool TryFadable(Material m, out int property, out bool fadeRgb)
        {
            property = m.HasProperty(MHKLitIds.BaseColor) ? MHKLitIds.BaseColor
                : m.HasProperty(MHKLitIds.Color) ? MHKLitIds.Color
                : m.HasProperty(TintColor) ? TintColor : -1;
            fadeRgb = false;
            if (property < 0) return false;
            if (m.HasProperty(MHKLitIds.SrcBlend) && m.HasProperty(MHKLitIds.DstBlend))
            {
                if ((BlendMode)(int)m.GetFloat(MHKLitIds.DstBlend) == BlendMode.Zero) return false;
                fadeRgb = (BlendMode)(int)m.GetFloat(MHKLitIds.SrcBlend) == BlendMode.One;
                return true;
            }
            return m.renderQueue > (int)RenderQueue.GeometryLast;
        }

        void Update()
        {
            if (!started)
            {
                delay -= Time.deltaTime;
                if (delay > 0f) return;
                Setup();
            }
            age += Time.deltaTime;
            if (!gone)
            {
                var visibility = 1f - Mathf.Clamp01(age / duration);
                Show(visibility);
                gone = visibility <= 0f;
            }
            if (age < duration || (age < MaxLinger && (age < linger || ParticlesAlive()))) return;
            Destroy(gameObject);
        }

        void Show(float visibility)
        {
            block ??= new MaterialPropertyBlock();
            foreach (var c in colours)
            {
                if (c.renderer == null) continue;
                var shown = c.colour;
                shown.a *= visibility;
                if (c.fadeRgb)
                {
                    shown.r *= visibility;
                    shown.g *= visibility;
                    shown.b *= visibility;
                }
                if (c.owned != null)
                {
                    c.owned.SetColor(c.property, shown);
                    continue;
                }
                c.renderer.GetPropertyBlock(block, c.index);
                block.SetColor(c.property, shown);
                c.renderer.SetPropertyBlock(block, c.index);
            }
            foreach (var s in sprites)
            {
                if (s.renderer == null) continue;
                var shown = s.colour;
                shown.a *= visibility;
                s.renderer.color = shown;
            }
            foreach (var l in lights)
                if (l.light != null) l.light.intensity = l.intensity * visibility;
            foreach (var s in sounds)
                if (s.source != null) s.source.volume = s.volume * visibility;
            foreach (var listener in listeners)
                if (listener is Object o && o != null) listener.OnFadeOut(visibility);
            if (shrink) transform.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, visibility);
        }

        bool ParticlesAlive()
        {
            foreach (var ps in particles)
                if (ps != null && ps.IsAlive(false)) return true;
            return false;
        }

        void OnDestroy()
        {
            foreach (var o in owned)
                if (o != null) Destroy(o);
        }
    }
}
