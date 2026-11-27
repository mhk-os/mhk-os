using UnityEngine;

namespace MHK.Modding
{
    /// <summary>Kind of track: a lap circuit or a battle arena.</summary>
    public enum TrackLayout
    {
        [Tooltip("Closed racing circuit with a start/finish line.")]
        Circuit,
        [Tooltip("Battle arena (Thunder Last Man Standing). Offered for battles, and for races too when an overlay gives it a " +
                 "circuit course (core.circuit, TrackOptionRules.circuit).")]
        Arena,
    }
}
