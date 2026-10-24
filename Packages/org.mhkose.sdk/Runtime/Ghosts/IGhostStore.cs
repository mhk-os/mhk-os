using System.Collections.Generic;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;

namespace MHK.Ghosts
{
    /// <summary>
    /// Storage of time-trial ghosts. The game uses <see cref="GhostStores.Current"/> (a <see cref="LocalGhostStore"/>).
    /// An online store (ghost exchange) implements the same calls.
    /// </summary>
    public interface IGhostStore
    {
        /// <summary>All ghosts of <paramref name="trackId"/> (every engine class), with their compatibility.</summary>
        IReadOnlyList<GhostEntry> List(string trackId);
        /// <summary>The personal best of a track and engine class, also when it is from another version, null if none.</summary>
        GhostEntry Best(string trackId, Difficulty engineClass);
        /// <summary>The ghost with id <paramref name="id"/> (the best when several entries share it), null if none.</summary>
        GhostEntry Find(string id);
        /// <summary>The full ghost (frames and events), null when it can't be read.</summary>
        GhostData Load(GhostEntry entry);
        /// <summary>Stores a finished run: the new personal best when it beats the current one, else the last run.</summary>
        GhostSaveResult SaveRun(GhostData run);
        void Delete(GhostEntry entry);
        /// <summary>Copies the ghost to the export location, returns where it went (a path or address).</summary>
        string Export(GhostEntry entry);
        /// <summary>Takes in the ghosts waiting at the import location, returns how many were added and how many failed.</summary>
        (int imported, int failed) ImportPending();
        /// <summary>Records the track hash a race computed (<see cref="GhostFingerprint.TrackHash"/>) for menus to compare against.</summary>
        void RememberTrackHash(string trackId, string hash);
        /// <summary>Track hash last computed for <paramref name="trackId"/> with this simulation version and its pack (in any build), null when unknown.</summary>
        string KnownTrackHash(string trackId);
    }

    /// <summary>The store the game uses.</summary>
    public static partial class GhostStores
    {
        [AutoStaticsCleanup] static IGhostStore current;

        public static IGhostStore Current
        {
            get => current ??= new LocalGhostStore(DataPaths.Ghosts);
            set => current = value;
        }
    }
}
