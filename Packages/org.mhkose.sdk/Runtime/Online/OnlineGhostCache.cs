using System;
using System.Collections.Generic;
using System.IO;
using MHK.Core;
using MHK.Ghosts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>What the server said about a downloaded ghost (its leaderboard row when it was downloaded).</summary>
    [Serializable]
    public sealed class OnlineGhostInfo
    {
        /// <summary>The ghost file's header id (<see cref="GhostHeader.id"/>), the key of the cache.</summary>
        public string ghostId;
        public long runId;
        public long boardId;
        /// <summary>Account name of the uploader: the name the ghost shows (the file's own player name is not trusted).</summary>
        public string username;
        public string country;
        public string trackId;
        public int finishMs;
        /// <summary>Distinct accounts that reported the run when it was downloaded.</summary>
        public int flags;
        /// <summary>When it was stored (UTC ticks), for evicting the oldest downloads.</summary>
        public long storedTicks;
    }

    /// <summary>
    /// Ghosts downloaded from the server: <c>Ghosts/Online/&lt;ghostId&gt;.mhkghost</c> plus <c>index.json</c> with
    /// their <see cref="OnlineGhostInfo"/>. Not the player's own records: the local store finds them by id for races and
    /// replays but does not list them with a track's ghosts. Main thread only.
    /// </summary>
    public static partial class OnlineGhostCache
    {
        /// <summary>Sub-folder of <see cref="DataPaths.Ghosts"/>; on players' disks, never rename.</summary>
        public const string FolderName = "Online";
        const string IndexFile = "index.json";
        /// <summary>Downloads kept; the oldest go first beyond this.</summary>
        public const int MaxEntries = 1000;

        [Serializable]
        sealed class Index
        {
            public List<OnlineGhostInfo> ghosts = new();
        }

        [AutoStaticsCleanup] static Dictionary<string, OnlineGhostInfo> byId;

        public static string Folder => Path.Combine(DataPaths.Ghosts, FolderName);

        /// <summary>The cached file of <paramref name="ghostId"/> (it may not exist).</summary>
        public static string PathFor(string ghostId) => Path.Combine(Folder, SafeId(ghostId) + GhostFile.Extension);

        /// <summary>The cached ghost <paramref name="ghostId"/>: true when both its file and its info are present.</summary>
        public static bool TryGet(string ghostId, out OnlineGhostInfo info)
        {
            info = null;
            if (string.IsNullOrEmpty(ghostId)) return false;
            return Entries.TryGetValue(ghostId, out info) && File.Exists(PathFor(ghostId));
        }

        /// <summary>Every cached ghost (also those whose file went missing).</summary>
        public static IEnumerable<OnlineGhostInfo> All => Entries.Values;

        /// <summary>Stores a downloaded ghost file and its info, replacing an older copy, then evicts beyond <see cref="MaxEntries"/>.</summary>
        public static void Store(OnlineGhostInfo info, byte[] file)
        {
            if (info == null || string.IsNullOrEmpty(info.ghostId)) throw new ArgumentException("ghost id missing", nameof(info));
            SafeFile.Write(PathFor(info.ghostId), s => s.Write(file, 0, file.Length));
            info.storedTicks = DateTime.UtcNow.Ticks;
            Entries[info.ghostId] = info;
            Evict();
            Save();
        }

        /// <summary>Updates the info of a cached ghost (e.g. a newer flag count) without touching its file.</summary>
        public static void Update(OnlineGhostInfo info)
        {
            if (info == null || !Entries.ContainsKey(info.ghostId)) return;
            Entries[info.ghostId] = info;
            Save();
        }

        public static void Remove(string ghostId)
        {
            if (string.IsNullOrEmpty(ghostId)) return;
            Entries.Remove(ghostId);
            TryDelete(PathFor(ghostId));
            Save();
        }

        static Dictionary<string, OnlineGhostInfo> Entries => byId ??= Load();

        static Dictionary<string, OnlineGhostInfo> Load()
        {
            var map = new Dictionary<string, OnlineGhostInfo>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(Folder, IndexFile);
            if (!File.Exists(path)) return map;
            try
            {
                var index = JsonUtility.FromJson<Index>(File.ReadAllText(path));
                if (index?.ghosts != null)
                    foreach (var g in index.ghosts)
                        if (g != null && !string.IsNullOrEmpty(g.ghostId)) map[g.ghostId] = g;
            }
            catch (Exception e)
            {
                var kept = SafeFile.Quarantine(path);
                Debug.LogWarning($"[Online] Ghost cache index unreadable ({e.Message}), starting empty" +
                                 (kept != null ? $"; kept as {kept}." : "."));
            }
            return map;
        }

        static void Save()
        {
            var index = new Index();
            index.ghosts.AddRange(Entries.Values);
            try
            {
                SafeFile.WriteAllText(Path.Combine(Folder, IndexFile), JsonUtility.ToJson(index));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Online] Could not write the ghost cache index: {e.Message}");
            }
        }

        static void Evict()
        {
            if (Entries.Count <= MaxEntries) return;
            var oldest = new List<OnlineGhostInfo>(Entries.Values);
            oldest.Sort((a, b) => a.storedTicks.CompareTo(b.storedTicks));
            for (var i = 0; i < oldest.Count - MaxEntries; i++)
            {
                Entries.Remove(oldest[i].ghostId);
                TryDelete(PathFor(oldest[i].ghostId));
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[Online] Could not delete {path}: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                Debug.LogWarning($"[Online] Could not delete {path}: {e.Message}");
            }
        }

        // Header ids are 32 hex characters; anything else (a hostile server) must not escape the folder.
        static string SafeId(string id)
        {
            var chars = id.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars);
        }
    }
}
