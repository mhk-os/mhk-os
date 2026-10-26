using System;
using System.Collections.Generic;
using System.IO;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// <see cref="IGhostStore"/> over files under <c>persistentDataPath/Ghosts</c>: one folder per track with best, last,
    /// saved and imported ghosts (<see cref="GhostPaths"/>) and the last computed track hash, plus Export and Import folders.
    /// Ghosts downloaded into <see cref="OnlineGhostCache"/> are found by id (<see cref="GhostKind.Online"/>) but not listed.
    /// </summary>
    public sealed class LocalGhostStore : IGhostStore
    {
        readonly string rootFolder;
        // Header id to the stored files with that id (a best kept aside as saved shares it) and file to header and write
        // time: read from every header once on first use, then kept current by this store's own changes and by List, which
        // reads a track's headers anyway. A race finds dozens of ghosts by id without inflating every stored file each time;
        // a file another game process rewrote since (best and last runs reuse their names) is read again.
        Dictionary<string, List<string>> pathsById;
        Dictionary<string, GhostHeader> headerByPath;
        Dictionary<string, DateTime> writtenByPath;

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
            if (!Directory.Exists(dir))
            {
                UnindexFolder(dir, null);
                return list;
            }
            var known = KnownTrackHash(trackId);
            var setupHashes = new Dictionary<(int, string, int), string>();
            var seen = pathsById != null ? new HashSet<string>(StringComparer.Ordinal) : null;
            foreach (var path in Directory.EnumerateFiles(dir, GhostPaths.SearchPattern))
            {
                var entry = ReadEntry(path, known, setupHashes);
                if (entry == null) continue;
                if (seen != null)
                {
                    seen.Add(path);
                    Reindex(path, entry.header);
                }
                if (entry.header.trackId == trackId) list.Add(entry);
            }
            UnindexFolder(dir, seen);
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
            EnsureIndex();
            string found = null;
            if (pathsById.TryGetValue(id, out var paths))
            {
                // Backwards: a file deleted behind the store's back leaves the list, one rewritten moves to its new id.
                for (var i = paths.Count - 1; i >= 0; i--)
                {
                    var path = paths[i];
                    if (!File.Exists(path))
                    {
                        Unindex(path);
                        continue;
                    }
                    if (File.GetLastWriteTimeUtc(path) != writtenByPath[path] && !Reread(path, id)) continue;
                    if (found == null || GhostPaths.KindOf(path) == GhostKind.Best) found = path;
                }
            }
            if (found == null) return FindOnline(id);
            var header = headerByPath[found];
            return NewEntry(found, header, GhostPaths.KindOf(found), KnownTrackHash(header.trackId));
        }

        // Indexes the current header of a file that changed since it was indexed; true when it still holds id.
        bool Reread(string path, string id)
        {
            var header = TryHeader(path);
            if (header == null)
            {
                Unindex(path);
                return false;
            }
            Reindex(path, header);
            return header.id == id;
        }

        GhostEntry FindOnline(string id)
        {
            if (!OnlineGhostCache.TryGet(id, out _)) return null;
            var path = OnlineGhostCache.PathFor(id);
            var header = TryHeader(path);
            if (header == null || !string.Equals(header.id, id, StringComparison.OrdinalIgnoreCase)) return null;
            return NewEntry(path, header, GhostKind.Online, KnownTrackHash(header.trackId));
        }

        /// <summary>Reads the file only (no Unity API, no index), so races decode their ghosts on worker threads.</summary>
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
            if (result.newRecord && best != null && !best.IsCurrent)
            {
                var savedPath = Path.Combine(dir, GhostPaths.Saved(best.Id));
                SafeFile.Replace(bestPath, savedPath);
                Unindex(bestPath);
                Reindex(savedPath, best.header);
            }
            var path = result.newRecord ? bestPath : lastPath;
            GhostFile.Save(path, run);
            if (result.newRecord && File.Exists(lastPath))
            {
                File.Delete(lastPath);
                Unindex(lastPath);
            }
            result.entry = ReadEntry(path, known);
            if (result.entry != null) Reindex(path, result.entry.header);
            else Unindex(path);
            return result;
        }

        public void Delete(GhostEntry entry)
        {
            if (entry == null) return;
            if (entry.kind == GhostKind.Online)
            {
                OnlineGhostCache.Remove(entry.Id);
                return;
            }
            if (string.IsNullOrEmpty(entry.location)) return;
            if (File.Exists(entry.location)) File.Delete(entry.location);
            Unindex(entry.location);
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
            EnsureIndex();
            int imported = 0, failed = 0;
            foreach (var path in pending)
            {
                try
                {
                    Import(path);
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

        void Import(string path)
        {
            var h = GhostFile.Load(path).header;
            if (string.IsNullOrEmpty(h.id) || string.IsNullOrEmpty(h.trackId)) throw new InvalidDataException("no id or track");
            if (!pathsById.ContainsKey(h.id))
            {
                var dir = TrackFolder(h.trackId);
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, GhostPaths.Imported(h.id));
                File.Copy(path, target, true);
                Reindex(target, h);
            }
            File.Delete(path);
        }

        public void RememberTrackHash(string trackId, string hash) =>
            SafeFile.WriteAllText(Path.Combine(TrackFolder(trackId), GhostPaths.TrackHashFile), $"{HashKey(trackId)}\n{hash}\n");

        public string KnownTrackHash(string trackId)
        {
            var path = Path.Combine(TrackFolder(trackId), GhostPaths.TrackHashFile);
            if (!File.Exists(path)) return null;
            try
            {
                var lines = File.ReadAllLines(path);
                return lines.Length >= 2 && lines[0] == HashKey(trackId) ? lines[1] : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        void EnsureIndex()
        {
            if (pathsById != null) return;
            pathsById = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            headerByPath = new Dictionary<string, GhostHeader>(StringComparer.Ordinal);
            writtenByPath = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            if (!Directory.Exists(rootFolder)) return;
            foreach (var dir in Directory.EnumerateDirectories(rootFolder))
            {
                if (GhostPaths.IsStoreFolder(Path.GetFileName(dir))) continue;
                foreach (var path in Directory.EnumerateFiles(dir, GhostPaths.SearchPattern))
                {
                    var header = TryHeader(path);
                    if (header != null) Reindex(path, header);
                }
            }
        }

        // Records that the file at path now holds header (no-op until the index is built).
        void Reindex(string path, GhostHeader header)
        {
            if (pathsById == null) return;
            Unindex(path);
            if (string.IsNullOrEmpty(header.id)) return;
            headerByPath[path] = header;
            writtenByPath[path] = File.GetLastWriteTimeUtc(path);
            if (!pathsById.TryGetValue(header.id, out var paths)) pathsById[header.id] = paths = new List<string>(1);
            paths.Add(path);
        }

        void Unindex(string path)
        {
            if (headerByPath == null || !headerByPath.Remove(path, out var header)) return;
            writtenByPath.Remove(path);
            if (!pathsById.TryGetValue(header.id, out var paths)) return;
            paths.Remove(path);
            if (paths.Count == 0) pathsById.Remove(header.id);
        }

        // Drops the indexed files of dir that are not in seen (null: all of them).
        void UnindexFolder(string dir, HashSet<string> seen)
        {
            if (headerByPath == null) return;
            List<string> gone = null;
            foreach (var path in headerByPath.Keys)
                if (Path.GetDirectoryName(path) == dir && (seen == null || !seen.Contains(path)))
                    (gone ??= new List<string>()).Add(path);
            if (gone == null) return;
            foreach (var path in gone) Unindex(path);
        }

        // A remembered track hash holds while the fingerprint scheme, the simulation version and the track's pack stay:
        // it outlives builds, so a menu knows it before the track is raced again (a race always computes it anew).
        static string HashKey(string trackId)
        {
            var track = GameRoot.Instance.Content.Get<TrackDefinition>(trackId);
            return $"{GhostFingerprint.Scheme} sim{SimulationVersion.Current} {(track != null ? track.OwnerPackId : "")}";
        }

        static GhostEntry ReadEntry(string path, string knownTrackHash, Dictionary<(int, string, int), string> setupHashes = null)
        {
            var header = TryHeader(path);
            return header != null ? NewEntry(path, header, GhostPaths.KindOf(path), knownTrackHash, setupHashes) : null;
        }

        static GhostEntry NewEntry(string path, GhostHeader header, GhostKind kind, string knownTrackHash,
            Dictionary<(int, string, int), string> setupHashes = null) => new()
        {
            header = header,
            kind = kind,
            location = path,
            compatibility = GhostCompatibilityRules.Evaluate(header, knownTrackHash, setupHashes),
        };

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
