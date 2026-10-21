using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A track: its scene, layout, laps, music, AI tuning and physics overrides.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Track", fileName = "Track")]
    public class TrackDefinition : ContentDefinition
    {
        [Tooltip("Track scene: one TrackRuntime (racing line, grid) plus the track's geometry, props, lights and lighting settings.")]
        public SceneReference scene = new();
        public TrackLayout layout = TrackLayout.Circuit;
        [Min(1)] public int defaultLaps = 3;
        public AudioClip music;
        [Range(0, 1)] public float musicVolume = 0.7f;
        public Texture2D previewImage;
        [Tooltip("Gameplay variant label shown in menus (e.g. 'Reverse').")]
        public string variant;
        [Tooltip("Physics gravity override (0 = game default, MHK2 19.62).")]
        public float gravity;
        [Tooltip("AI tuning per engine class (Easy/50cc, Medium/100cc, Hard/150cc). Empty = GameConfig.defaultTrackAi.")]
        public TrackAiSettings[] aiSettings = Array.Empty<TrackAiSettings>();
        [Tooltip("Kart physics overrides on this track (slope gravity).")]
        public TrackPhysicsSettings physics = new();

        /// <summary>The picture menus show for this track: the preview, else the icon.</summary>
        public Texture2D Picture => previewImage != null ? previewImage : Icon;

        /// <summary>AI tuning for an engine class: the track's own, else <paramref name="defaults"/> (GameConfig.defaultTrackAi).</summary>
        public TrackAiSettings GetAi(int engineClass, IReadOnlyList<TrackAiSettings> defaults)
        {
            var table = aiSettings != null && aiSettings.Length > 0 ? aiSettings : defaults;
            return table != null && table.Count > 0 ? table[Mathf.Clamp(engineClass, 0, table.Count - 1)] : new TrackAiSettings();
        }

#if UNITY_EDITOR
        // Project assets only: a definition loaded from a mod bundle in the editor has no scene asset (the field is
        // editor-only), so syncing would clear the scene path it was built with.
        void OnValidate()
        {
            if (!string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(this))) scene.Sync();
        }
#endif
    }
}
