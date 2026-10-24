using System;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Kart physics overrides of a track. MHK3 tracks cancel the slope gravity and press the karts into the ground: no
    /// speed change on slopes, contact on any floor slope (work/mhk3/mechanics.md).
    /// </summary>
    [Serializable]
    public class TrackPhysicsSettings
    {
        [Tooltip("Use slopeGravityFactor / gravityIntoGround below instead of the kart profiles' slopeGravityFactor.")]
        public bool overrideSlopeGravity;
        [Tooltip("Share of the gravity along the ground acting on grounded karts: 0 = slopes neither slow down nor speed " +
                 "up the kart, 1 = full gravity (MHK2).")]
        [Range(0f, 1f)] public float slopeGravityFactor = 1f;
        [Tooltip("While grounded, the full gravity pulls the kart into the ground (along the ground normal), so it keeps " +
                 "contact and grip on steep slopes.")]
        public bool gravityIntoGround;
    }
}
