namespace MHK.AI
{
    /// <summary>Road lanes (0 = left edge, 0.5 = centre, 1 = right edge) and heights shared by the computer driver's parts.</summary>
    internal static class AILanes
    {
        public const float Centre = 0.5f;
        /// <summary>Band the driving keeps to and the feelers scan.</summary>
        public const float DriveMin = 0.1f, DriveMax = 0.9f;
        /// <summary>Item boxes and speed pads outside this band are not driven for.</summary>
        public const float TargetMin = 0.05f, TargetMax = 0.95f;
        /// <summary>A trap is passed on the far side when the near side leaves this band.</summary>
        public const float DodgeMin = 0.08f, DodgeMax = 0.92f;
        /// <summary>Karts more than this (m) above or below are on another level of the track.</summary>
        public const float SameLevelHeight = 3f;
    }
}
