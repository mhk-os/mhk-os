using System.Collections.Generic;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Ghosts;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.UI
{
    /// <summary>
    /// World records of the current content version per track and engine class for the Time Trials hub: one
    /// ListBoards request for every track, kept for <see cref="MaxAgeSeconds"/> so visiting the hub does not ask again.
    /// A track with several boards of this version shows the one of this device's track data, else the most played.
    /// </summary>
    public static partial class WorldRecords
    {
        const float MaxAgeSeconds = 60f;

        [AutoStaticsCleanup] static Dictionary<(string, int), BoardInfo> boards;
        [AutoStaticsCleanup] static float fetchedAt = -1f;
        // Not generated: the cleanup would Dispose() the task, which throws while it still runs. ForgetRequests drops it.
        [NoAutoStaticsCleanup] static Task<bool> pending;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ForgetRequests() => pending = null;

        /// <summary>Whether the last request got an answer (false before any and when the server could not be reached).</summary>
        public static bool Online { get; private set; }

        /// <summary>The reason the last request failed, empty after an answer.</summary>
        public static string LastError { get; private set; } = "";

        /// <summary>The board holding the world record of <paramref name="trackId"/> in <paramref name="engineClass"/>, false when none is known.</summary>
        public static bool TryGet(string trackId, int engineClass, out BoardInfo board)
        {
            board = null;
            return boards != null && !string.IsNullOrEmpty(trackId) && boards.TryGetValue((trackId.ToLowerInvariant(), engineClass), out board);
        }

        /// <summary>
        /// The records, asked for when older than <see cref="MaxAgeSeconds"/> (one request at a time); true when they are
        /// current. Never throws: an unreachable server leaves the last known records and sets <see cref="Online"/> false.
        /// </summary>
        public static Task<bool> Load()
        {
            if (pending != null && !pending.IsCompleted) return pending;
            if (boards != null && fetchedAt >= 0f && Time.realtimeSinceStartup - fetchedAt < MaxAgeSeconds) return Task.FromResult(true);
            return pending = FetchAsync();
        }

        static async Task<bool> FetchAsync()
        {
            var r = await OnlineTimeTrials.ListBoards("", ListBoardsRequest.AnyEngineClass, ContentVersion.Current);
            if (!r.Ok)
            {
                Online = false;
                LastError = r.result.message ?? "";
                return false;
            }
            var map = new Dictionary<(string, int), BoardInfo>();
            foreach (var b in r.value)
            {
                if (b.bestMs <= 0 || string.IsNullOrEmpty(b.trackId)) continue;
                var key = (b.trackId.ToLowerInvariant(), b.engineClass);
                if (!map.TryGetValue(key, out var kept) || Better(b, kept)) map[key] = b;
            }
            boards = map;
            fetchedAt = Time.realtimeSinceStartup;
            Online = true;
            LastError = "";
            return true;
        }

        // This device's track data first, then the board with more players.
        static bool Better(BoardInfo candidate, BoardInfo kept)
        {
            var known = GhostStores.Current.KnownTrackHash(candidate.trackId);
            if (!string.IsNullOrEmpty(known))
            {
                var candidateKnown = string.Equals(candidate.trackHash, known, System.StringComparison.OrdinalIgnoreCase);
                var keptKnown = string.Equals(kept.trackHash, known, System.StringComparison.OrdinalIgnoreCase);
                if (candidateKnown != keptKnown) return candidateKnown;
            }
            return candidate.players > kept.players;
        }
    }
}
