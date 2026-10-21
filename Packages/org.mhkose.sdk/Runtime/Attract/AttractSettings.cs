using System;
using System.Collections.Generic;
using MHK.Core;
using UnityEngine;

namespace MHK.Attract
{
    /// <summary>How the main menu shows its background race.</summary>
    public enum AttractMode
    {
        [Tooltip("The neutral background only, no race is loaded.")]
        Off,
        [Tooltip("One picture of the race, then the race unloads (nothing runs while the menu is open).")]
        Still,
        [Tooltip("The race runs behind the menu and the director cuts between shots.")]
        Live,
    }

    /// <summary>Background race setup of one graphics preset.</summary>
    [Serializable]
    public struct AttractProfile
    {
        public AttractMode mode;
        [Tooltip("Karts in the race (at most GameConfig.maxRacers).")]
        [Range(1, 12)] public int karts;
    }

    /// <summary>Background race setups of the Low, Medium and High presets (Custom uses the tier of its shadow quality).</summary>
    [Serializable]
    public struct AttractTiers
    {
        public AttractProfile low, medium, high;

        public readonly AttractProfile For(GraphicsPreset tier) => tier switch
        {
            GraphicsPreset.Low => low,
            GraphicsPreset.Medium => medium,
            _ => high,
        };
    }

    /// <summary>
    /// The main menu's background race: shots of the camera director, track rotation, race setup and the reduced setups
    /// per platform and graphics preset.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Attract Settings", fileName = "AttractSettings")]
    public class AttractSettings : ScriptableObject
    {
        [Header("Director")]
        [Tooltip("Shot while the karts wait on the grid (until the countdown ends).")]
        public AttractShot openingShot = new() { kind = AttractShotKind.Orbit, distance = 7f, height = 2.2f, speed = 18f };
        [Tooltip("Shots of the running race, picked at random by weight (never the same kind twice in a row when another fits).")]
        public List<AttractShot> shots = new();
        [Tooltip("Horizontal field of view (degrees) of shots without their own.")]
        [Range(30f, 120f)] public float defaultFov = 70f;
        [Tooltip("How fast the shots' cameras follow their kart (1/s): higher = tighter, lower = calmer.")]
        [Min(0.1f)] public float followRate = 4f;
        [Tooltip("Gap (m) kept between the camera and a wall or the ground it would be behind.")]
        [Min(0f)] public float wallSkin = 0.3f;
        [Tooltip("A shot cuts early when its kart stays hidden behind the track for this long (s). Chase shots never do.")]
        [Min(0f)] public float lostSightSeconds = 0.8f;

        [Header("Race")]
        [Tooltip("Engine class and computer skill of the background race.")]
        public Difficulty difficulty = Difficulty.Hard;
        [Tooltip("Item boxes and powerups in the background race.")]
        public bool items = true;
        [Tooltip("Minutes on one track before the menu fades to another. 0 = one track per menu visit.")]
        [Min(0f)] public float minutesPerTrack = 5f;
        [Tooltip("Seconds of the fade between the neutral background and the race.")]
        [Min(0f)] public float fadeSeconds = 0.8f;
        [Tooltip("Still mode: seconds of racing before the picture is taken.")]
        [Min(0f)] public float stillAfterSeconds = 4f;
        [Tooltip("Still mode: the shot the picture is taken with.")]
        public AttractShot stillShot = new() { kind = AttractShotKind.Chase, view = CameraMode.Far };

        [Header("Platforms")]
        [Tooltip("Desktop setups per graphics preset.")]
        public AttractTiers desktop = new()
        {
            low = new AttractProfile { mode = AttractMode.Live, karts = 4 },
            medium = new AttractProfile { mode = AttractMode.Live, karts = 6 },
            high = new AttractProfile { mode = AttractMode.Live, karts = 8 },
        };
        [Tooltip("Phone and tablet setups per graphics preset.")]
        public AttractTiers mobile = new()
        {
            low = new AttractProfile { mode = AttractMode.Still, karts = 4 },
            medium = new AttractProfile { mode = AttractMode.Still, karts = 4 },
            high = new AttractProfile { mode = AttractMode.Live, karts = 4 },
        };
    }
}
