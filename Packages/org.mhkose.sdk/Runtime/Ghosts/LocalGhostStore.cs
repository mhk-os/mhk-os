using System;
using System.Collections.Generic;
using System.IO;
using MHK.Core;
using MHK.Modding;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// <see cref="IGhostStore"/> over files under <c>persistentDataPath/Ghosts</c>: one folder per track with best, last,
    /// saved and imported ghosts (<see cref="GhostPaths"/>) and the last computed track hash, plus Export and Import folders.
    /// </summary>
    public sealed class LocalGhostStore : IGhostStore
    {
        readonly string rootFolder;

        public string ExportFolder => Path.Combine(rootFolder, GhostPaths.ExportFolderName);
        public string ImportFolder => Path.Combine(rootFolder, GhostPaths.ImportFolderName);

        public LocalGhostStore(string root)
        {
            rootFolder = root;
        }

        string TrackFolder(string trackId) => Path.Combine(rootFolder, GhostPaths.Safe(trackId));

        public IReadOnlyList<GhostEntry> List(string trackId)
        {
            var list = new List<GhostEntry>();
            var dir = TrackFolder(trackId);
            if (!Directory.Exists(dir)) return list;
            var known = KnownTrackHash(trackId);
            var setupHashes = new Dictionary<(int, string, int), string>();
            foreach (var path in Directory.EnumerateFiles(dir, GhostPaths.SearchPattern))
            {
                var entry = ReadEntry(path, known, setupHashes);
                if (entry != null && entry.header.trackId == trackId) list.Add(entry);
            }
            list.Sort((a, b) => a.header.finishTime.CompareTo(b.header.finishTime));
            return list;
        }

        public GhostEntry Best(string trackId, Difficulty engineClass)
        {
            var path = Path.Combine(TrackFolder(trackId), GhostPaths.Best((int)engineClass));
            return File.Exists(path) ? ReadEntry(path, KnownTrackHash(trackId)) : null;
        }

        public GhostEntry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GhostEntry found = null;
            var setupHashes = new Dictionary<(int, string, int), string>();
            foreach (var path in StoredFiles())
            {
                var header = TryHeader(path);
                if (header == null || header.id != id) continue;
                var entry = ReadEntry(path, KnownTrackHash(header.trackId), setupHashes);
                if (entry == null) continue;
                if (found == null || entry.kind == GhostKind.Best) found = entry;
            }
            return found;
        }

        public GhostData Load(GhostEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.location)) return null;
            try
            {
                return GhostFile.Load(entry.location);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ghosts] Could not read {entry.location}: {e.Message}");
                return null;
            }
        }

        public GhostSaveResult SaveRun(GhostData run)
        {
            var h = run.header;
            var dir = TrackFolder(h.trackId);
            var bestPath = Path.Combine(dir, GhostPaths.Best(h.engineClass));
            var lastPath = Path.Combine(dir, GhostPaths.Last(h.engineClass));
            var known = KnownTrackHash(h.trackId);
            var best = File.Exists(bestPath) ? ReadEntry(bestPath, known) : null;
            var result = new GhostSaveResult { newRecord = best == null || !best.IsCurrent || h.finishTime < best.header.finishTime };
            if (best != null && best.IsCurrent) result.previousBest = best.header.finishTime;
            // A record needs a current best to compare with: a best of another simulation version or content is kept aside.
            if (result.newRecord && best != null && !best.IsCurrent) SafeFile.Replace(bestPath, Path.Combine(dir, GhostPaths.Saved(best.Id)));
            var path = result.newRecord ? bestPath : lastPath;
            GhostFile.Save(path, run);
            if (result.newRecord && File.Exists(lastPath)) File.Delete(lastPath);
            result.entry = ReadEntry(path, known);
            return result;
        }

        public void Delete(GhostEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.location) || !File.Exists(entry.location)) return;
            File.Delete(entry.location);
        }

        public string Export(GhostEntry entry)
        {
            Directory.CreateDirectory(ExportFolder);
            var target = Path.Combine(ExportFolder, GhostPaths.ExportName(entry.header));
            File.Copy(entry.location, target, true);
            return target;
        }

        public (int imported, int failed) ImportPending()
        {
            Directory.CreateDirectory(ImportFolder);
            var pending = Directory.GetFiles(ImportFolder, GhostPaths.SearchPattern);
            if (pending.Length == 0) return (0, 0);
            var storedIds = StoredIds();
            int imported = 0, failed = 0;
            foreach (var path in pending)
            {
                try
                {
                    Import(path, storedIds);
                    imported++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Ghosts] Could not import {path}: {e.Message}");
                    failed++;
                }
            }
            return (imported, failed);
        }

        void Import(string path, HashSet<string> storedIds)
        {
            var h = GhostFile.Load(path).header;
            if (string.IsNullOrEmpty(h.id) || string.IsNullOrEmpty(h.trackId)) throw new InvalidDataException("no id or track");
            if (storedIds.Add(h.id))
            {
                var dir = TrackFolder(h.trackId);
                Directory.CreateDirectory(dir);
                File.Copy(path, Path.Combine(dir, GhostPaths.Imported(h.id)), true);
            }
            File.Delete(path);
        }

        public void RememberTrackHash(string trackId, string hash) =>
            SafeFile.WriteAllText(Path.Combine(TrackFolder(trackId), GhostPaths.TrackHashFile), $"{BuildInfo.Id}\n{PackVersion(trackId)}\n{hash}\n");

        public string KnownTrackHash(string trackId)
        {
            var path = Path.Combine(TrackFolder(trackId), GhostPaths.TrackHashFile);
            if (!File.Exists(path)) return null;
            try
            {
                var lines = File.ReadAllLines(path);
                return lines.Length >= 3 && lines[0] == BuildInfo.Id && lines[1] == PackVersion(trackId) ? lines[2] : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        IEnumerable<string> StoredFiles()
        {
            if (!Directory.Exists(rootFolder)) yield break;
            foreach (var dir in Directory.EnumerateDirectories(rootFolder))
            {
                if (GhostPaths.IsStoreFolder(Path.GetFileName(dir))) continue;
                foreach (var path in Directory.EnumerateFiles(dir, GhostPaths.SearchPattern)) yield return path;
            }
        }

        HashSet<string> StoredIds()
        {
            var ids = new HashSet<string>();
            foreach (var path in StoredFiles())
            {
                var header = TryHeader(path);
                if (header != null && !string.IsNullOrEmpty(header.id)) ids.Add(header.id);
            }
            return ids;
        }

        static string PackVersion(string trackId)
        {
            var track = GameRoot.Instance.Content.Get<TrackDefinition>(trackId);
            return track != null && track.OwnerPack != null ? track.OwnerPackId + " " + track.OwnerPackVersion : "";
        }

        static GhostEntry ReadEntry(string path, string knownTrackHash, Dictionary<(int, string, int), string> setupHashes = null)
        {
            var header = TryHeader(path);
            if (header == null) return null;
            return new GhostEntry
            {
                header = header,
                kind = GhostPaths.KindOf(path),
                location = path,
                compatibility = GhostCompatibilityRules.Evaluate(header, knownTrackHash, setupHashes),
            };
        }

        static GhostHeader TryHeader(string path)
        {
            try
            {
                return GhostFile.LoadHeader(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ghosts] Skipping {path}: {e.Message}");
                return null;
            }
        }
    }
}
