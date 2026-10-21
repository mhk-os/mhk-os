namespace MHK.Karts
{
    /// <summary>Per-tick control state of a kart, produced by a driver (human, AI, replay, network).</summary>
    public struct KartInput
    {
        /// <summary>-1 (left) .. 1 (right).</summary>
        public float steer;
        /// <summary>0 .. 1.</summary>
        public float throttle;
        /// <summary>0 .. 1. Braking while (nearly) stopped engages reverse.</summary>
        public float brake;
        /// <summary>Use held powerup forwards (edge-triggered by the inventory).</summary>
        public bool fire;
        /// <summary>Aim backwards / look back while held.</summary>
        public bool lookBack;
        /// <summary>Sound the horn (the character's horn voice line, with a cooldown).</summary>
        public bool horn;
        /// <summary>Ask to be put back on the track.</summary>
        public bool respawn;
        /// <summary>
        /// Held by the race, not the driver (countdown, frozen): full brake, no throttle, never engages reverse.
        /// MHK2 writes these outputs directly instead of going through the controller logic.
        /// </summary>
        public bool parked;
    }
}
