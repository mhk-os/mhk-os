using UnityEngine;

namespace MHK.Race
{
    /// <summary>Raw downward ground rays (triggers ignored, every collider in the mask counts).</summary>
    public static class GroundProbe
    {
        /// <summary>Ray from <paramref name="above"/> metres over <paramref name="point"/> down to <paramref name="depth"/> metres below it.</summary>
        public static bool Down(Vector3 point, float above, float depth, int mask, out RaycastHit hit) =>
            Physics.Raycast(point + Vector3.up * above, Vector3.down, out hit, above + depth, mask, QueryTriggerInteraction.Ignore);

        /// <summary>Ray from <paramref name="origin"/> straight down over <paramref name="distance"/> metres.</summary>
        public static bool Down(Vector3 origin, float distance, int mask, out RaycastHit hit) =>
            Physics.Raycast(origin, Vector3.down, out hit, distance, mask, QueryTriggerInteraction.Ignore);

        /// <summary>True when anything in <paramref name="mask"/> lies within <paramref name="distance"/> metres below <paramref name="origin"/>.</summary>
        public static bool Any(Vector3 origin, float distance, int mask) =>
            Physics.Raycast(origin, Vector3.down, distance, mask, QueryTriggerInteraction.Ignore);
    }
}
