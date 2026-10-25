using System;
using System.IO;
using System.Threading;
using MHK.Assets;
using UnityEngine;

namespace MHK.Modding
{
    public static partial class ModSources
    {
        /// <summary>
        /// Metadata of an asset pack: in the root folder of its zip (next to icon.png and Assets/), and optionally as a
        /// release asset of the same name so the game knows the pack before the download. An asset pack release has
        /// exactly one zip asset (for every platform) and no mod.json. Its identity is the release tag plus the pack.json
        /// commit when there is one.
        /// </summary>
        public const string PackFile = "pack.json";

        const string ZipExtension = ".zip";

        // A release with mod.json is a mod. One with pack.json or a single zip is an asset pack (old mod releases have one zip per platform).
        static Kind Detect(GitHubRelease[] releases)
        {
            foreach (var rel in releases)
            {
                if (rel == null || rel.draft || rel.prerelease) continue;
                if (rel.Asset(ManifestFile) != null) return Kind.Mod;
                if (rel.Asset(PackFile) != null || PackZip(rel) != null) return Kind.AssetPack;
            }
            return Kind.Unknown;
        }

        static GitHubAsset PackZip(GitHubRelease rel)
        {
            GitHubAsset zip = null;
            if (rel.assets == null) return null;
            foreach (var a in rel.assets)
            {
                if (a == null || a.name == null || !a.name.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase)) continue;
                if (zip != null) return null;
                zip = a;
            }
            return zip != null && !string.IsNullOrEmpty(zip.browser_download_url) ? zip : null;
        }

        // Asset packs do not depend on the game build: the newest release with its zip (and a readable pack.json asset when it has one) is the one.
        static async Awaitable ChoosePackAsync(Source s, GitHubRelease[] releases, RepoCache cache, CancellationToken ct)
        {
            foreach (var rel in releases)
            {
                if (rel == null || rel.draft || rel.prerelease) continue;
                var zip = PackZip(rel);
                if (zip == null) continue;
                var asset = rel.Asset(PackFile);
                string json = null;
                PackManifest pack = null;
                if (asset != null)
                {
                    json = await ManifestJsonAsync(asset, cache, ct);
                    pack = PackManifest.Parse(json);
                    if (pack == null) continue;
                    Describe(s, pack.id, pack.title, pack.description);
                }
                s.latest = new Release
                {
                    kind = Kind.AssetPack, tag = rel.tag_name, commit = pack?.commit ?? rel.tag_name, pack = pack, manifestJson = json,
                    zipUrl = zip.browser_download_url, zipName = zip.name, zipId = zip.id, zipSize = zip.size,
                };
                cache.manifests.RemoveAll(m => asset == null || m.assetId != asset.id);
                return;
            }
            cache.manifests.Clear();
        }

        // Before an asset pack without a pack.json asset is downloaded its id is unknown: the repository names its files until then.
        static string StagingId(Source s, Release release) =>
            release.kind == Kind.Mod ? release.manifest.packId : release.pack?.id ?? s.repo.Replace('/', '_');

        static void RefreshInstalledPack(Source s)
        {
            foreach (var folder in new[] { HiddenFolder(PacksFolder, s.packId, PendingSuffix), InstalledFolder(PacksFolder, s.packId) })
            {
                var record = ReadJson<ModSourceRecord>(Path.Combine(folder, SourceFile));
                if (record == null || !SameRepo(record.repo, s.repo)) continue;
                var pack = PackManifest.Parse(ReadText(Path.Combine(folder, PackFile)));
                s.installed = s.managed = true;
                s.installedTag = record.tag;
                s.installedCommit = pack?.commit ?? record.tag;
                if (pack != null && s.description == null) Describe(s, null, pack.title, pack.description);
                return;
            }
            var installedFolder = InstalledFolder(PacksFolder, s.packId);
            var listed = false;
            foreach (var info in AssetPacks.Available)
                if (string.Equals(info.id, s.packId, StringComparison.OrdinalIgnoreCase))
                {
                    installedFolder = info.folder;
                    listed = true;
                }
            var manual = PackManifest.Parse(ReadText(Path.Combine(installedFolder, PackFile)));
            s.installed = listed || manual != null;
            s.installedCommit = manual?.commit;
        }

        // The pack folder is the zip's root or its single top folder, the one with pack.json.
        static (string, string) StagePack(Release release, string staging)
        {
            var folder = File.Exists(Path.Combine(staging, PackFile)) ? staging : null;
            if (folder == null)
                foreach (var dir in Directory.GetDirectories(staging))
                    if (File.Exists(Path.Combine(dir, PackFile)))
                    {
                        if (folder != null) throw new SourceException(State.Error, "The download holds several asset packs.");
                        folder = dir;
                    }
            if (folder == null) throw new SourceException(State.Error, $"The download holds no {PackFile}.");
            var pack = PackManifest.Parse(ReadText(Path.Combine(folder, PackFile)));
            if (pack == null) throw new SourceException(State.Error, $"The {PackFile} of the download is unreadable.");
            if (release.pack != null && !string.Equals(pack.id, release.pack.id, StringComparison.OrdinalIgnoreCase))
                throw new SourceException(State.Error, $"The download is asset pack '{pack.id}', its release says '{release.pack.id}'.");
            return (folder, pack.id);
        }
    }
}
