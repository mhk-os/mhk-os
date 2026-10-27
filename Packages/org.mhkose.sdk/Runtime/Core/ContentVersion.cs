namespace MHK.Core
{
    /// <summary>
    /// Version of everything that decides race results: physics code, track geometry and content rules. Bump
    /// <see cref="Current"/> when a change alters lap times for identical inputs: ghosts of other versions never count as
    /// records. Changes to one track's or kart's data are also caught by <see cref="Ghosts.GhostFingerprint"/>.
    /// </summary>
    public static class ContentVersion
    {
        public const int Current = 5;
    }
}
