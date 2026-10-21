using System;
using MHK.Core;
using UnityEngine;

namespace MHK.Attract
{
    /// <summary>Camera shot kinds of the menu's background race (<see cref="AttractDirector"/>).</summary>
    public enum AttractShotKind
    {
        [Tooltip("The race camera behind a kart, in one of its driving views (ChaseCamera presets).")]
        Chase,
        [Tooltip("Circles a kart while looking at it.")]
        Orbit,
        [Tooltip("High above a kart looking steeply down at it and the karts around it.")]
        Overhead,
        [Tooltip("Stands beside the racing line ahead of a kart and turns to watch it pass.")]
        Trackside,
        [Tooltip("Flies along the racing line beside and above a kart, overtaking it while keeping it in view.")]
        Flyby,
    }

    /// <summary>One shot type of the attract director: what it shows, how long, how it starts and its placement values.</summary>
    [Serializable]
    public class AttractShot
    {
        public AttractShotKind kind;
        [Tooltip("Relative chance to be picked next (0 = never).")]
        [Min(0f)] public float weight = 1f;
        [Tooltip("Length of the shot in seconds, picked at random between x and y.")]
        public Vector2 seconds = new(5f, 8f);
        [Tooltip("Seconds of the blend from the previous shot's camera. 0 = a hard cut.")]
        [Min(0f)] public float blendSeconds;
        [Tooltip("Chase: the race camera's driving view (Close or Far, Pov hides the kart).")]
        public CameraMode view = CameraMode.Close;
        [Tooltip("Orbit: radius (m). Overhead: horizontal distance behind the kart (m). Trackside: distance beside the " +
                 "road edge (m). Flyby: sideways offset from the racing line (m).")]
        public float distance = 6f;
        [Tooltip("Height (m) over the kart (Orbit, Overhead) or over the ground (Trackside, Flyby).")]
        public float height = 2f;
        [Tooltip("Orbit: degrees per second around the kart. Flyby: metres per second faster than the kart, so it is overtaken.")]
        public float speed = 20f;
        [Tooltip("Trackside: seconds until the kart reaches the camera. Flyby: seconds behind the kart where the flight starts.")]
        [Min(0f)] public float lead = 2.5f;
        [Tooltip("Horizontal field of view in degrees. 0 = the settings' default (Chase uses the race camera's own).")]
        [Min(0f)] public float fov;
    }
}
