using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    public enum SurfaceKind { Road, Grass, Sand, Dirt, Ice, Water, Mud, Snow, Wall, Deadly, Drop, Boost }

    /// <summary>Kind groups shared by physics, AI, items and respawn.</summary>
    public static class SurfaceKindExtensions
    {
        public static bool IsWall(this SurfaceKind kind) => kind == SurfaceKind.Wall;

        /// <summary>Drop or Deadly: karts fall or die there.</summary>
        public static bool IsFall(this SurfaceKind kind) => kind is SurfaceKind.Drop or SurfaceKind.Deadly;

        /// <summary>Wall, Drop or Deadly: never a place to put a kart or an item.</summary>
        public static bool IsUnsafeGround(this SurfaceKind kind) => kind is SurfaceKind.Wall or SurfaceKind.Drop or SurfaceKind.Deadly;
    }

    /// <summary>
    /// Driving surface of a collider with MHK2 material semantics (grip, skid, dust, sound).
    /// Colliders without one behave like MHK2 road.
    /// </summary>
    public partial class TrackSurface : MonoBehaviour
    {
        [Tooltip("Surface class: walls, fall zones and water change respawn, AI and item behaviour.")]
        public SurfaceKind kind = SurfaceKind.Road;
        [Tooltip("'lat dir friction': multiplies the wheels' lateral grip.")]
        public float lateralFriction = 1f;
        [Tooltip("'roll dir friction': multiplies brake friction and drive traction.")]
        public float rollFriction = 1f;
        [Tooltip("Offroad ground beside the road (grass, deep sand, mud, snow fields): slows karts by " +
                 "GameConfig.offroadResistance, the same in every game, and without its own surfaceSound plays the kart " +
                 "pack's offroad loop. Ground the road itself is made of (MHK2 Winter snow, Moorhuhn X gravel, a sand " +
                 "arena) is not offroad, whatever its grip.")]
        public bool offroad;
        [Tooltip("Tint of the dust thrown by tyres and digging items.")]
        public Color dustColor = new(0.6f, 0.5f, 0.35f, 0.6f);

        [Header("Skid marks / dust (cosmetic)")]
        [Tooltip("'skidmark color' (rgb + opacity).")]
        public Color skidColor = new(0f, 0f, 0f, 0.5f);
        [Tooltip("'min. skid energy': summed lateral slip speed of the wheels (m/s, original speed units) where skid marks " +
                 "and the kart's skid sound start.")]
        public float minSkidEnergy = 10f;
        [Tooltip("'max. skid energy': summed lateral slip speed at full skid mark opacity / skid sound volume.")]
        public float maxSkidEnergy = 20f;
        [Tooltip("Tyres throw dust/smoke particles on this surface (MHK2 'particleEffect' 1).")]
        public bool dust;
        [Tooltip("'particleBaseEmitterFreq': particles/s per rear wheel while rolling.")]
        public float dustBaseRate;
        [Tooltip("'particleSpeedEmitterFreq': extra particles/s at 'particleMaxSpeed'.")]
        public float dustSpeedRate = 20f;
        [Tooltip("'particleMinSpeed' / 'particleMaxSpeed' (m/s, original speed units).")]
        public Vector2 dustSpeedRange = new(5f, 40f);
        [Tooltip("'particleSkidEmitterFreq': extra particles/s at full skid energy.")]
        public float dustSkidRate = 35f;
        [Tooltip("'particleMinSkidEnergy' / 'particleMaxSkidEnergy'.")]
        public Vector2 dustSkidRange = new(10f, 20f);
        [Tooltip("'particleCarVelocityFactor': share of the kart velocity a particle starts with.")]
        public float dustVelocityFactor = 0.7f;

        [Header("Surface sound")]
        [Tooltip("Loop played while driving on it, louder with speed (MHK2: the material's 'sound effect' UG sample, " +
                 "volume = speed / 30 m/s in original units).")]
        public AudioClip surfaceSound;
        [Tooltip("Volume of surfaceSound (MHK2 AudioEvent volume).")]
        [Range(0f, 1f)] public float surfaceSoundVolume = 1f;

        [AutoStaticsCleanup] static readonly Dictionary<Collider, TrackSurface> ByCollider = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;

        /// <summary>
        /// Surface of <paramref name="collider"/> or its parents, null for plain road. Cached per collider while a
        /// track is loaded.
        /// </summary>
        public static TrackSurface Of(Collider collider)
        {
            if (collider == null) return null;
            var track = TrackRuntime.Current;
            if (track == null) return collider.GetComponentInParent<TrackSurface>();
            if (cacheTrack != track)
            {
                ByCollider.Clear();
                cacheTrack = track;
            }
            if (!ByCollider.TryGetValue(collider, out var surface))
            {
                surface = collider.GetComponentInParent<TrackSurface>();
                ByCollider[collider] = surface;
            }
            return surface;
        }

        /// <summary>
        /// Tyre slip angle scale from the lateral friction: (lat / 0.5)^−1.2 within [0.35, 2.5]. The less grip, the more
        /// the kart drifts: hard ground (1.0) ≈ 0.44 (almost no drift, sharp turns, no skidding), gravel 0.5 = 1,
        /// sand 0.4 ≈ 1.3, snow 0.3 ≈ 1.85.
        /// </summary>
        public static float SlipAngleScaleFor(float lateralFriction) =>
            Mathf.Clamp(Mathf.Pow(Mathf.Max(lateralFriction, 0.01f) / 0.5f, -1.2f), 0.35f, 2.5f);

        /// <summary>
        /// Editor helper: MHK2-like friction, dust switch and offroad flag for <see cref="kind"/> (dust rates keep the
        /// MHK2 sand/gravel field defaults). Snow is the most slippery driving surface (MHK2 winter 'snow' 0.3), ice a
        /// bit less (MHK3 ice keeps traction: 'slide ×3' only). Untick <see cref="offroad"/> again when the road itself is
        /// made of this ground.
        /// </summary>
        [ContextMenu("Apply defaults for kind")]
        public void ApplyDefaults()
        {
            (lateralFriction, rollFriction) = kind switch
            {
                SurfaceKind.Grass => (0.2f, 0.2f),   // MHK2 "Gras"
                SurfaceKind.Sand => (0.5f, 0.5f),
                SurfaceKind.Dirt => (0.5f, 0.5f),    // MHK2 "Schotter"
                SurfaceKind.Mud => (0.3f, 0.3f),
                SurfaceKind.Snow => (0.3f, 0.35f),   // MHK2 "snow" + MHK3 slow floor
                SurfaceKind.Ice => (0.35f, 0.5f),
                SurfaceKind.Water => (0.3f, 0.3f),
                _ => (1f, 1f),
            };
            dust = kind is SurfaceKind.Sand or SurfaceKind.Dirt or SurfaceKind.Snow;
            offroad = kind is SurfaceKind.Grass or SurfaceKind.Sand or SurfaceKind.Dirt or SurfaceKind.Mud or SurfaceKind.Snow
                or SurfaceKind.Water;
        }
    }
}
