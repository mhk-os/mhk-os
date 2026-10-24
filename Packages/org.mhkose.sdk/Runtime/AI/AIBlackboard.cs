using MHK.Karts;
using MHK.Race;

namespace MHK.AI
{
    /// <summary>
    /// Per-driver state shared between the driving (<see cref="AIKartDriver"/>) and item use (<see cref="AIItemBrain"/>).
    /// Each side writes its fields once per physics step and reads the other's from the previous step.
    /// </summary>
    internal sealed class AIBlackboard
    {
        // Written by the driving, read by the item brain.
        /// <summary>Alternative route the kart is currently following (null = main line).</summary>
        public TrackPath route;

        // Written by the item brain, read by the driving.
        /// <summary>The item brain wants the kart lined up for a straight shot: steer towards <see cref="aimLaneU"/>.</summary>
        public bool aimRequested;
        /// <summary>Road lane that puts the shot on its target.</summary>
        public float aimLaneU = AILanes.Centre;
        /// <summary>The kart the item brain wants to line up a shot at (null = none), chased in battles.</summary>
        public Kart aimTarget;
        /// <summary>The item brain wants the kart to brake and let a kart behind pass (missile yield).</summary>
        public bool yieldRequested;
        /// <summary>The kart behind that should pass while <see cref="yieldRequested"/> (null otherwise).</summary>
        public Kart yieldTo;
    }
}
