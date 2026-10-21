namespace MHK.Core
{
    /// <summary>
    /// Version of the race simulation. Bump <see cref="Current"/> when a code change alters lap times for identical inputs:
    /// ghosts of other versions never count as records. Content changes are caught by <see cref="Ghosts.GhostFingerprint"/>.
    /// </summary>
    public static class SimulationVersion
    {
        public const int Current = 2;
    }
}
