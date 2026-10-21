using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Renders menu art for content without any: character portraits, kart and powerup icons. Track previews are
    /// taken in game by <see cref="TrackPreviewCapture"/> (queued from here). Output PNGs live next to the definitions.
    /// </summary>
    public static class PreviewRenderer
    {
        readonly struct StudioShot
        {
            readonly Vector3 euler;
            readonly float distanceFactor, minExtent;
            public readonly float Fov;
            public readonly int Size;

            public StudioShot(Vector3 euler, float distanceFactor, float minExtent, float fov, int size)
            {
                this.euler = euler;
                this.distanceFactor = distanceFactor;
                this.minExtent = minExtent;
                Fov = fov;
                Size = size;
            }

            public Vector3 CameraPosition(Vector3 focus, Bounds b) =>
                focus + Quaternion.Euler(euler) * Vector3.forward * (Mathf.Max(minExtent, b.extents.magnitude) * distanceFactor);
        }

        static readonly Color StudioAmbient = new(0.9f, 0.9f, 0.9f);
        static readonly Vector3 StudioSunDir = new(0.3f, -0.7f, 0.6f);
        const float StudioSun = 1.3f;
        const float StudioNear = 0.1f, StudioFar = 50f;
        const int PortraitSize = 256;
        const float PortraitFocusLift = 0.35f;

        static readonly StudioShot PortraitShot = new(new Vector3(8f, 28f, 0f), 2.2f, 0f, 28f, PortraitSize * 2);
        static readonly StudioShot KartIcon = new(new Vector3(-22f, 35f, 0f), 3.2f, 0.3f, 32f, 256);
        static readonly StudioShot PowerupIcon = new(new Vector3(25f, 145f, 0f), 3f, 0.3f, 30f, 128);

        [MenuItem("MHK/Content/Render Missing Previews, Portraits && Icons")]
        public static void RenderMissing() => RenderAll(false);

        [MenuItem("MHK/Content/Re-render All Previews")]
        public static void RenderAllForced() => RenderAll(true);

        /// <summary>
        /// Renders the missing portraits and icons (all generated ones when <paramref name="force"/>) of the built-in
        /// and project mod packs, then queues the tracks without a preview (all when forced) for the in-game capture.
        /// </summary>
        public static void RenderAll(bool force)
        {
            var tracks = new List<string>();
            foreach (var pack in Modding.ContentPackAssets.ShippedPacks().Concat(Modding.ProjectModPacks.All()).SelectMany(p => p.SelfAndIncluded()))
            {
                foreach (var c in pack.GetAll<CharacterDefinition>())
                    if (force || c.portrait == null) RenderCharacter(c);
                // Built-in powerup icons are curated slot pictures (gen_powerup_icons.py): renders only fill in icons
                // of mod powerups that ship without one, even when forced.
                foreach (var p in pack.GetAll<PowerupDefinition>())
                    if (p.Icon == null) RenderPowerup(p);
                foreach (var k in pack.GetAll<KartDefinition>())
                    if (force || k.Icon == null) RenderKart(k);
                tracks.AddRange(pack.GetAll<TrackDefinition>().Where(t => force || t.previewImage == null).Select(t => t.Id));
            }
            AssetDatabase.SaveAssets();
            if (tracks.Count > 0) TrackPreviewCapture.Capture(tracks.Distinct().ToArray());
        }

        internal static string OutputPath(ContentDefinition def, string suffix) =>
            $"{AssetPaths.DirectoryOf(def)}/{AssetPaths.SafeFileName(def.Id)}_{suffix}.png";

        /// <summary>
        /// Renders a front three-quarter driver bust as the character's portrait on a transparent background.
        /// A portrait that is not one of these renders (hand-made art) is kept.
        /// </summary>
        public static void RenderCharacter(CharacterDefinition c)
        {
            if (c.driverPrefab == null) return;
            var path = OutputPath(c, "portrait");
            if (c.portrait != null && AssetDatabase.GetAssetPath(c.portrait) != path) return;
            WithInstance(c.driverPrefab, go => go.transform.localScale *= c.modelScale, c.animations, (go, b) =>
            {
                var focus = b.center + Vector3.up * (b.extents.y * PortraitFocusLift);
                var bust = RenderStudio(go, focus, b, PortraitShot);
                var portrait = PortraitCompositor.Compose(bust, PortraitSize);
                Object.DestroyImmediate(bust);
                c.portrait = Save(portrait, path);
                if (c.Icon == null) c.EditorSetIcon(c.portrait);
                EditorUtility.SetDirty(c);
            });
        }

        public static void RenderKart(KartDefinition k) =>
            RenderIcon(k, k.kartPrefab, go => go.transform.localScale = Vector3.one * k.modelScale, k.animations, KartIcon);

        public static void RenderPowerup(PowerupDefinition p) => RenderIcon(p, p.usePrefab, null, null, PowerupIcon);

        static void RenderIcon(ContentDefinition def, GameObject prefab, Action<GameObject> prepare,
                               IEnumerable<CharacterAnimBinding> anims, StudioShot shot)
        {
            if (prefab == null) return;
            WithInstance(prefab, prepare, anims, (go, b) =>
            {
                def.EditorSetIcon(Save(RenderStudio(go, b.center, b, shot), OutputPath(def, "icon")));
                EditorUtility.SetDirty(def);
            });
        }

        static void WithInstance(GameObject prefab, Action<GameObject> prepare, IEnumerable<CharacterAnimBinding> anims,
                                 Action<GameObject, Bounds> shoot)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                prepare?.Invoke(go);
                Pose(go, anims);
                if (VisibleBounds(go, out var b)) shoot(go, b);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        static Texture2D Save(Texture2D tex, string path)
        {
            TextureCapture.WritePng(tex, path);
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.mipmapEnabled = false;
                ti.alphaIsTransparency = true;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Texture2D RenderStudio(GameObject subject, Vector3 focus, Bounds bounds, StudioShot shot)
        {
            var pru = new PreviewRenderUtility();
            try
            {
                ConfigureStudioCamera(pru.camera, shot.CameraPosition(focus, bounds), focus, shot.Fov);
                ConfigureStudioLights(pru.lights);
                pru.AddSingleGO(subject);
                return RenderWithStudioAmbient(pru.camera, shot.Size);
            }
            finally
            {
                pru.Cleanup();
            }
        }

        static void ConfigureStudioCamera(Camera cam, Vector3 position, Vector3 lookAt, float fov)
        {
            cam.fieldOfView = fov;
            cam.nearClipPlane = StudioNear;
            cam.farClipPlane = StudioFar;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
            cam.transform.position = position;
            cam.transform.LookAt(lookAt);
        }

        static void ConfigureStudioLights(Light[] lights)
        {
            lights[0].transform.rotation = Quaternion.LookRotation(StudioSunDir);
            lights[0].color = Color.white;
            lights[0].intensity = StudioSun;
            lights[1].intensity = 0f;
        }

        static Texture2D RenderWithStudioAmbient(Camera cam, int size)
        {
            // Direct camera renders bypass the utility's lighting override: without one the shaders' ambient (SH)
            // would come from whichever scene is open (e.g. the warm main menu).
            Unsupported.SetOverrideLightingSettings(cam.scene);
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = StudioAmbient;
                var sh = new SphericalHarmonicsL2();
                sh.AddAmbientLight(StudioAmbient);
                RenderSettings.ambientProbe = sh;
                return TextureCapture.Render(cam, size, size);
            }
            finally
            {
                Unsupported.RestoreOverrideLightingSettings();
            }
        }

        static void Pose(GameObject instance, IEnumerable<CharacterAnimBinding> anims)
        {
            if (instance == null || anims == null) return;
            var list = anims.ToList();
            var clip = FirstClip(list, CharacterAnimRole.Drive);
            if (clip == null) clip = FirstClip(list, CharacterAnimRole.Idle);
            if (clip == null) return;
            var animator = instance.GetComponentInChildren<Animator>(true);
            clip.SampleAnimation(animator != null ? animator.gameObject : instance, 0f);
        }

        static AnimationClip FirstClip(List<CharacterAnimBinding> anims, CharacterAnimRole role) =>
            anims.FirstOrDefault(a => a.role == role && a.clip != null).clip;

        static bool VisibleBounds(GameObject go, out Bounds b)
        {
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) ps.gameObject.SetActive(false);
            var renderers = go.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            b = renderers.Length > 0 ? renderers[0].bounds : default;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return renderers.Length > 0;
        }
    }
}
