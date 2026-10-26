namespace MHK.Ghosts
{
    /// <summary>Where a stored ghost came from.</summary>
    public enum GhostKind
    {
        /// <summary>The personal best of its track and engine class (kept automatically).</summary>
        Best,
        /// <summary>The latest finished run of its track and engine class that was not a new best.</summary>
        LastRun,
        /// <summary>Kept aside: a best of another simulation version or content that a run of this one replaced.</summary>
        Saved,
        /// <summary>Imported from a file.</summary>
        Imported,
        /// <summary>Downloaded from the server (<see cref="MHK.Online.OnlineGhostCache"/>): found by id, never listed with a track's ghosts.</summary>
        Online,
    }

    /// <summary>A ghost in a store: its header and where / how it is kept.</summary>
    public sealed class GhostEntry
    {
        public GhostHeader header;
        public GhostKind kind;
        public GhostCompatibility compatibility;
        /// <summary>Store-specific location (the local store: the file path).</summary>
        public string location;

        public string Id => header.id;
        public bool IsCurrent => compatibility == GhostCompatibility.Current;
    }

    /// <summary>Result of <see cref="IGhostStore.SaveRun"/>.</summary>
    public sealed class GhostSaveResult
    {
        /// <summary>The stored run (kind <see cref="GhostKind.Best"/> when it is the new record, else <see cref="GhostKind.LastRun"/>).</summary>
        public GhostEntry entry;
        public bool newRecord;
        /// <summary>Finish time of the record it beat or missed, negative when there was none.</summary>
        public float previousBest = -1f;
    }
}
