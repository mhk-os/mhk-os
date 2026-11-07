namespace MHK.Core
{
    /// <summary>
    /// Version of the race rules every track and kart shares: physics code, the race rules of <see cref="GameConfig"/> and
    /// the kart root's chassis. Bump <see cref="Current"/> when such a change alters lap times for identical inputs: ghosts
    /// of other versions never count as records. A change to one track, overlay or kart bumps that content's own
    /// <c>contentVersion</c> instead (<see cref="Modding.TrackDefinition.contentVersion"/>,
    /// <see cref="Modding.KartDefinition.contentVersion"/>, <see cref="Modding.TrackOverlayDefinition.contentVersion"/>).
    /// </summary>
    public static class RulesVersion
    {
        public const int Current = 1;
    }
}
