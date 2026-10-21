using UnityEngine;

namespace MHK.Modding
{
    /// <summary>Kind of track: a lap circuit or a battle arena.</summary>
    public enum TrackLayout
    {
        [Tooltip("Closed racing circuit with a start/finish line.")]
        Circuit,
        [Tooltip("Battle arena without a lap circuit (Thunder Last Man Standing). Only offered for battles.")]
        Arena,
    }
}
