using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using MHK.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace MHK.Modding
{
    public static partial class ModSources
    {
        const float StallSeconds = 30f;
        const string StagingSuffix = ".staging", OldSuffix = ".old", PendingSuffix = ".pending", RemoveSuffix = ".remove";

        static string ModsFolder => ModLoader.UserFolder;
        static string PacksFolder => DataPaths.AssetPacks;
        static string DownloadFolder => Path.Combine(DataPaths.Root, "Cache", "ModDownloads");
        static string RootOf(Kind kind) => kind == Kind.AssetPack ? PacksFolder : ModsFolder;
        static string InstalledFolder(string root, string packId) => Path.Combine(root, packId);
        static string HiddenFolder(string root, string packId, string suffix) => Path.Combine(root, "." + packId + suffix);

        static async Awaitable<bool> InstallLatestAsync(Source s, CancellationToken ct)
        {
            var release = s.latest;
            Debug.Log($"[ModSources] Installing {s.repo} {release.tag} ({(s.managed ? "update of an install this game made" : s.installed ? "replaces a manual install" : "new")}).");
            var root = RootOf(release.kind);
            var stagingId = StagingId(s, release);
            s.busy = true;
            string zip = null;
            try
            {
                zip = await DownloadAsync(s, release, stagingId, ct);
                SetState(s, State.Installing, null);
                var (folder, id) = await StageAsync(s.repo, release, zip, HiddenFolder(root, stagingId, StagingSuffix), ct);
                var pending = Replace(root, id, folder);
                Debug.Log($"[ModSources] Installed {s.repo} {release.tag} ({release.commit}) " +
                          (pending ? $"as {HiddenFolder(root, id, PendingSuffix)}, it replaces the loaded copy at the next content reload." : $"into {InstalledFolder(root, id)}."));
                Installed(s, id);
                return true;
            }
            catch (SourceException e)
            {
                Debug.LogWarning($"[ModSources] Install of {s.repo} {release.tag} failed: {e.Message}");
                SetState(s, e.state, e.Message);
                return false;
            }
            catch (OperationCanceledException)
            {
                SetEvaluated(s);
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ModSources] Install of {s.repo} {release.tag} failed: {e}");
                SetState(s, State.Error, OneLine(e.Message));
                return false;
            }
            finally
            {
                s.busy = false;
                s.downloadedBytes = 0;
                DeleteQuietly(zip);
                DeleteFolderQuietly(HiddenFolder(root, stagingId, StagingSuffix));
            }
        }

        static void Installed(Source s, string id)
        {
            if (s.packId != id)
            {
                s.packId = id;
                var cache = RepoCache.Load(s.repo);
                cache.Remember(s);
                cache.Save(s.repo);
            }
            RefreshInstalled(s);
            SetEvaluated(s);
        }

        // No overall timeout (mods are hundreds of MB): a download that receives nothing for StallSeconds is given up.
        static async Awaitable<string> DownloadAsync(Source s, Release release, string stagingId, CancellationToken ct)
        {
            Directory.CreateDirectory(DownloadFolder);
            var path = Path.Combine(DownloadFolder, stagingId + "-" + release.zipId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".zip");
            s.downloadBytes = release.zipSize;
            s.downloadedBytes = 0;
            SetState(s, State.Downloading, null);
            using var r = Request(release.zipUrl, false);
            r.timeout = 0;
            r.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
            var lastBytes = 0UL;
            var lastProgress = Time.realtimeSinceStartup;
            var stalled = false;
            await SendAsync(r, () =>
            {
                if (r.downloadedBytes != lastBytes)
                {
                    lastBytes = r.downloadedBytes;
                    lastProgress = Time.realtimeSinceStartup;
                    s.downloadedBytes = (long)lastBytes;
                    RaiseChanged(true);
                }
                else if (!stalled && Time.realtimeSinceStartup - lastProgress > StallSeconds)
                {
                    stalled = true;
                    r.Abort();
                }
            }, ct);
            if (stalled) throw new SourceException(State.Offline, "The download stopped.");
            ThrowOnFailure(r);
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (release.zipSize > 0 && size != release.zipSize) throw new SourceException(State.Error, "The download is incomplete.");
            s.downloadedBytes = size;
            RaiseChanged();
            return path;
        }

        // Extraction runs on a worker thread. The result is the folder that becomes <root>/<id> and that id.
        static async Awaitable<(string folder, string id)> StageAsync(string repo, Release release, string zip, string staging, CancellationToken ct)
        {
            (string, string) result = default;
            Exception error = null;
            await Awaitable.BackgroundThreadAsync();
            try
            {
                DeleteFolder(staging);
                ExtractChecked(zip, staging, ct);
                result = release.kind == Kind.AssetPack ? StagePack(release, staging) : StageMod(release, staging);
                var record = new ModSourceRecord { repo = repo, tag = release.tag, installed = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) };
                SafeFile.WriteAllText(Path.Combine(result.Item1, SourceFile), JsonUtility.ToJson(record, true));
            }
            catch (Exception e)
            {
                error = e;
            }
            await Awaitable.MainThreadAsync();
            ct.ThrowIfCancellationRequested();
            switch (error)
            {
                case null: return result;
                case SourceException or OperationCanceledException: throw error;
                case InvalidDataException: throw new SourceException(State.Error, "The download is not a valid zip.");
                default: throw new SourceException(State.Error, OneLine(error.Message));
            }
        }

        static (string, string) StageMod(Release release, string staging)
        {
            var packId = release.manifest.packId;
            var folder = Path.Combine(staging, packId);
            if (!File.Exists(Path.Combine(folder, packId + ModLoader.ModFileExtension))) folder = staging;
            if (!File.Exists(Path.Combine(folder, packId + ModLoader.ModFileExtension)))
                throw new SourceException(State.Error, $"The download holds no {packId}{ModLoader.ModFileExtension}.");
            SafeFile.WriteAllText(Path.Combine(folder, ManifestFile), release.manifestJson);
            return (folder, packId);
        }

        // Every entry is checked before anything is written: one entry outside the folder rejects the whole zip.
        static void ExtractChecked(string zip, string folder, CancellationToken ct)
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
                if (!IsInside(root, e.FullName))
                    throw new SourceException(State.Error, "The download has files outside its folder and was rejected.");
            Directory.CreateDirectory(folder);
            foreach (var e in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = e.FullName.Replace('\\', '/');
                if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || e.Name.StartsWith("._", StringComparison.Ordinal)) continue;
                var target = Path.GetFullPath(Path.Combine(root, name));
                if (name.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                e.ExtractToFile(target, false);
            }
        }

        static bool IsInside(string root, string entry)
        {
            var name = entry.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith("/", StringComparison.Ordinal) || name.Contains(":")) return false;
            foreach (var part in name.Split('/'))
                if (part == "..") return false;
            return Path.GetFullPath(Path.Combine(root, name)).StartsWith(root, StringComparison.Ordinal);
        }

        // <root>/<id> moves aside, the new folder takes its place, the old one is deleted. When loaded files lock the
        // folder (Windows), the new folder waits as .<id>.pending for ApplyPending. True = pending.
        static bool Replace(string root, string packId, string folder)
        {
            var target = InstalledFolder(root, packId);
            var old = HiddenFolder(root, packId, OldSuffix);
            var pending = HiddenFolder(root, packId, PendingSuffix);
            DeleteFolder(old);
            if (!SamePath(folder, pending)) DeleteFolder(pending);
            if (Directory.Exists(target))
            {
                try
                {
                    Directory.Move(target, old);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    if (!SamePath(folder, pending)) Directory.Move(folder, pending);
                    return true;
                }
            }
            try
            {
                Directory.Move(folder, target);
            }
            catch
            {
                if (Directory.Exists(old) && !Directory.Exists(target)) Directory.Move(old, target);
                throw;
            }
            DeleteFolderQuietly(old);
            return false;
        }

        /// <summary>
        /// Finishes what had to wait until no mod or asset pack was loaded and repairs an interrupted replace, in the Mods
        /// and the asset pack folder: restores a lone <c>.&lt;id&gt;.old</c>, moves <c>.&lt;id&gt;.pending</c> into place,
        /// deletes folders marked for removal and leftover staging folders. <see cref="GameRoot"/> calls it before content loads.
        /// </summary>
        internal static void ApplyPending()
        {
            foreach (var root in new[] { ModsFolder, PacksFolder })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var suffix in new[] { OldSuffix, StagingSuffix, PendingSuffix })
                        foreach (var dir in Directory.GetDirectories(root, ".*" + suffix))
                            ApplyHidden(root, PackIdOf(dir, suffix), suffix, dir);
                    foreach (var marker in Directory.GetFiles(root, ".*" + RemoveSuffix))
                    {
                        DeleteFolder(InstalledFolder(root, PackIdOf(marker, RemoveSuffix)));
                        File.Delete(marker);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"[ModSources] Cannot finish pending installs in {root}: {e.Message}");
                }
            }
        }

        /// <summary>Mods and asset packs were (re)loaded (called by <see cref="GameRoot"/>): installed copies and the states that depend on them are read again.</summary>
        internal static void ContentLoaded()
        {
            if (sources == null) return;
            foreach (var s in sources)
            {
                if (s.busy) continue;
                RefreshInstalled(s);
                if (s.state is State.NotInstalled or State.UpToDate or State.UpdateAvailable or State.NeedsNewerGame or State.NeedsModUpdate) SetEvaluated(s);
            }
            RaiseChanged();
        }

        static void ApplyHidden(string root, string packId, string suffix, string dir)
        {
            if (string.IsNullOrEmpty(packId)) return;
            if (suffix == StagingSuffix) DeleteFolder(dir);
            else if (suffix == PendingSuffix)
            {
                if (!Replace(root, packId, dir)) Debug.Log($"[ModSources] Pending install of '{packId}' applied.");
            }
            else if (Directory.Exists(InstalledFolder(root, packId))) DeleteFolder(dir);
            else
            {
                Directory.Move(dir, InstalledFolder(root, packId));
                Debug.LogWarning($"[ModSources] An interrupted update of '{packId}' was undone: the previous copy is back.");
            }
        }

        static string PackIdOf(string path, string suffix)
        {
            var name = Path.GetFileName(path);
            return name.Length > suffix.Length + 1 ? name.Substring(1, name.Length - suffix.Length - 1) : null;
        }

        // Deleting a loaded folder can fail (Windows): a marker lets ApplyPending delete it at the next reload.
        static void DeleteInstalled(Source s)
        {
            if (string.IsNullOrEmpty(s.packId) || s.kind == Kind.Unknown) return;
            var root = RootOf(s.kind);
            DeleteFolderQuietly(HiddenFolder(root, s.packId, PendingSuffix));
            var folder = InstalledFolder(root, s.packId);
            try
            {
                DeleteFolder(folder);
                Debug.Log($"[ModSources] Deleted {folder}.");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                File.WriteAllText(HiddenFolder(root, s.packId, RemoveSuffix), s.repo);
                Debug.Log($"[ModSources] {folder} is in use: it is deleted at the next content reload.");
            }
            RefreshInstalled(s);
        }

        static void ClearDownloads() => DeleteFolderQuietly(DownloadFolder);

        static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

        static void DeleteFolder(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        static void DeleteFolderQuietly(string dir)
        {
            try
            {
                DeleteFolder(dir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[ModSources] Cannot delete {dir}: {e.Message}");
            }
        }

        static void DeleteQuietly(string file)
        {
            try
            {
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) File.Delete(file);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[ModSources] Cannot delete {file}: {e.Message}");
            }
        }
    }
}
