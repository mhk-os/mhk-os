using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A track: its scene, layout, laps, music and physics overrides.</summary>
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
        [Tooltip("Kart physics overrides on this track (slope gravity).")]
        public TrackPhysicsSettings physics = new();

        /// <summary>The picture menus show for this track: the preview, else the icon.</summary>
        public Texture2D Picture => previewImage != null ? previewImage : Icon;
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
