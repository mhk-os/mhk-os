using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>One item box look: model, animation and placement (a <see cref="ContentPack.itemBoxLook"/> or a variant of <see cref="ItemBoxLook"/>).</summary>
    [Serializable]
    public class ItemBoxAppearance
    {
        [Tooltip("Model prefab: the box art, its glow and its light (centred on the box), placed like this game's track boxes relative to their root.")]
        public GameObject model;
        [Tooltip("Rotation around the world up axis (degrees per second). 0 = no animation.")]
        public float spinSpeed;
        [Tooltip("Height of the box roots above the ground on this game's tracks. Boxes with a known heightAboveGround are moved to match.")]
        public float trackHeight;
        [Tooltip("Size of the model (1 = as built). It scales around the ground below the box (trackHeight under the root), so the box stays clear of the ground and its gap to the ground scales with it.")]
        [Min(0.01f)] public float scale = 1f;
    }

    /// <summary>
    /// Shows an item box model: the look of the current track's game (<see cref="ContentPack.itemBoxLook"/> of the pack
    /// named by the track's source game), else one of the box's own <see cref="Variant"/>s. The model is a runtime child,
    /// and the editor preview is never saved.
    /// </summary>
    [ExecuteAlways]
    public partial class ItemBoxLook : MonoBehaviour
    {
        [Serializable]
        public class Variant : ItemBoxAppearance
        {
            [Tooltip("Track source games (pack ids, e.g. tincan) that use this look when their pack has no item box look.")]
            public string[] games = Array.Empty<string>();
        }

        [Tooltip("Use the item box look of the current track's game when its pack has one. Off = always the box's own looks (e.g. special item boxes).")]
        [SerializeField] bool usePackLook = true;
        [Tooltip("The box's own looks: the one listing the track's game, else the first one (the default look).")]
        [SerializeField] Variant[] variants = Array.Empty<Variant>();
        [Tooltip("Colour multiplied onto the model's materials (fake item boxes are slightly darker).")]
        [SerializeField] Color tint = Color.white;
        [Tooltip("Height of this object above the ground for boxes placed by code (fake item boxes), so they sit like the track's boxes. < 0 = track box, the model keeps its own height.")]
        [SerializeField] float heightAboveGround = -1f;

        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        // Shared tinted copies of the box materials: they keep the renderers SRP Batcher compatible (no property blocks).
        [NoAutoStaticsCleanup] static readonly Dictionary<(Material, Color), Material> tinted = new();

        GameObject instance;
        ItemBoxAppearance current;

        /// <summary>The spawned model (null when no look has one).</summary>
        public GameObject Model => instance;

        void OnEnable()
        {
            if (instance == null) Build();
        }

        void OnDisable()
        {
            // Collected boxes are hidden by deactivating this object: keep the model at runtime.
            if (!Application.isPlaying) Clear();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (Application.isPlaying) return;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled) return;
                Clear();
                Build();
            };
        }
#endif

        void Update()
        {
            if (instance != null && current.spinSpeed != 0f && Application.isPlaying)
                instance.transform.Rotate(0f, current.spinSpeed * Time.deltaTime, 0f, Space.World);
        }

        void Build()
        {
            current = Resolve();
            if (current == null || current.model == null) return;
            instance = Instantiate(current.model, transform, false);
            instance.name = current.model.name;
            Place(instance.transform);
            var preview = !Application.isPlaying;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = gameObject.layer;
                if (preview) t.gameObject.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            }
            ApplyTint();
        }

        void Clear()
        {
            if (instance == null) return;
            if (Application.isPlaying) Destroy(instance);
            else DestroyImmediate(instance);
            instance = null;
        }

        void Place(Transform model)
        {
            var lift = heightAboveGround >= 0f ? current.trackHeight - heightAboveGround : 0f;
            model.localPosition += Vector3.up * lift;
            if (current.scale == 1f) return;
            var ground = Vector3.up * (lift - current.trackHeight);
            model.localPosition = ground + (model.localPosition - ground) * current.scale;
            model.localScale *= current.scale;
        }

        void ApplyTint()
        {
            if (tint == Color.white) return;
            foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                    if (mats[i] != null) mats[i] = Tinted(mats[i], tint);
                r.sharedMaterials = mats;
            }
        }

        static Material Tinted(Material m, Color tint)
        {
            if (tinted.TryGetValue((m, tint), out var copy) && copy != null) return copy;
            var id = m.HasProperty(TintId) ? TintId : m.HasProperty(BaseColorId) ? BaseColorId : -1;
            if (id < 0) return m;
            copy = new Material(m) { name = m.name + " (tinted)", hideFlags = HideFlags.DontSave };
            copy.SetColor(id, m.GetColor(id) * tint);
            tinted[(m, tint)] = copy;
            return copy;
        }

        /// <summary>Destroys the shared tinted materials (content reload: no box uses them and their sources may be gone).</summary>
        public static void ReleaseTints()
        {
            foreach (var copy in tinted.Values)
                if (copy != null) Destroy(copy);
            tinted.Clear();
        }

        /// <summary>The look shown on the current track: its game's pack look, else the matching or first variant (null without any).</summary>
        public ItemBoxAppearance Resolve()
        {
            var game = TrackGame();
            if (usePackLook && !string.IsNullOrEmpty(game))
            {
                var pack = PackOf(game);
                if (pack != null && pack.itemBoxLook != null && pack.itemBoxLook.model != null) return pack.itemBoxLook;
            }
            if (variants == null || variants.Length == 0) return null;
            foreach (var v in variants)
                if (v != null && game != null && Array.IndexOf(v.games, game) >= 0) return v;
            return variants[0];
        }

        static ContentPack PackOf(string packId)
        {
            if (Application.isPlaying)
            {
                foreach (var p in GameRoot.Instance.Content.Packs)
                    if (p != null && string.Equals(p.packId, packId, StringComparison.OrdinalIgnoreCase)) return p;
                return null;
            }
#if UNITY_EDITOR
            return EditorPacks().TryGetValue(packId, out var pack) ? pack : null;
#else
            return null;
#endif
        }

        // Source game of the track being raced, or of the track whose scene holds this box.
        string TrackGame()
        {
            var race = Race.RaceManager.Current;
            if (race != null && race.Track != null) return race.Track.SourceGame;
            var path = gameObject.scene.path;
            if (string.IsNullOrEmpty(path)) return null;
            if (Application.isPlaying)
            {
                foreach (var t in GameRoot.Instance.Content.All<TrackDefinition>())
                    if (t.scene.Path == path) return t.SourceGame;
                return null;
            }
#if UNITY_EDITOR
            return EditorTrackGames().TryGetValue(path, out var game) ? game : null;
#else
            return null;
#endif
        }

#if UNITY_EDITOR
        [AutoStaticsCleanup] static Dictionary<string, string> editorTrackGames;
        [AutoStaticsCleanup] static Dictionary<string, ContentPack> editorPacks;

        [UnityEditor.InitializeOnLoadMethod]
        static void ForgetCachesOnProjectChange() => UnityEditor.EditorApplication.projectChanged += () =>
        {
            editorTrackGames = null;
            editorPacks = null;
        };

        static Dictionary<string, string> EditorTrackGames()
        {
            if (editorTrackGames != null) return editorTrackGames;
            editorTrackGames = new Dictionary<string, string>();
            foreach (var guid in UnityEditor.AssetDatabase.FindAssetGUIDs("t:" + nameof(TrackDefinition)))
            {
                var t = UnityEditor.AssetDatabase.LoadAssetByGUID<TrackDefinition>(guid);
                if (t != null && t.scene.IsSet) editorTrackGames[t.scene.Path] = t.SourceGame;
            }
            return editorTrackGames;
        }

        static Dictionary<string, ContentPack> EditorPacks()
        {
            if (editorPacks != null) return editorPacks;
            editorPacks = new Dictionary<string, ContentPack>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in UnityEditor.AssetDatabase.FindAssetGUIDs("t:" + nameof(ContentPack)))
            {
                var p = UnityEditor.AssetDatabase.LoadAssetByGUID<ContentPack>(guid);
                if (p != null && !string.IsNullOrEmpty(p.packId)) editorPacks.TryAdd(p.packId, p);
            }
            return editorPacks;
        }
#endif
    }
}
