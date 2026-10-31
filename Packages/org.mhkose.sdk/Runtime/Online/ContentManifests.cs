using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Pack manifests of the loaded content (docs/protocol.md, content agreement): per registered pack (the built-in
    /// <c>game</c> pack and every pack a mod includes) the effective definitions it owns after load-order overrides,
    /// as (kind, id, <see cref="ContentFingerprints"/>) items, and the canonical manifest hash the server recomputes.
    /// Also answers which definitions are content of a lobby and which lobby ids another mod overrides here.
    /// </summary>
    public static partial class ContentManifests
    {
        /// <summary>Pack id of the game's own definitions: its items are content of every lobby.</summary>
        public const string GamePackId = "game";

        // Built for one registry version: content reloads change it.
        [AutoStaticsCleanup] static ContentRegistry builtFor;
        [AutoStaticsCleanup] static int builtVersion = -1;
        [AutoStaticsCleanup] static List<PackManifest> built = new();

        /// <summary>
        /// The manifests of every registered pack of <paramref name="registry"/> (cached until the content changes), in
        /// registration order. Packs with an invalid id and items with an invalid id are left out (logged): they cannot be
        /// announced and their content is never lobby content.
        /// </summary>
        public static IReadOnlyList<PackManifest> Build(ContentRegistry registry)
        {
            if (registry == builtFor && registry.Version == builtVersion) return built;
            var byPack = new Dictionary<string, PackManifest>(StringComparer.OrdinalIgnoreCase);
            var list = new List<PackManifest>();
            foreach (var pack in registry.Packs)
            {
                if (pack == null) continue;
                if (!IsPackId(pack.packId))
                {
                    Debug.LogWarning($"[Online] Pack id '{pack.packId}' is not 1-64 letters, digits, _ or -: it is not announced online.");
                    continue;
                }
                if (!byPack.TryGetValue(pack.packId, out var manifest))
                {
                    manifest = new PackManifest { packId = pack.packId, commit = Commit(pack), contentVersion = ContentVersion.Current };
                    byPack[pack.packId] = manifest;
                    list.Add(manifest);
                }
                AddItems(registry, pack, manifest);
            }
            foreach (var m in list)
            {
                if (m.items.Count > LobbyLimits.ManifestItems) m.items.RemoveRange(LobbyLimits.ManifestItems, m.items.Count - LobbyLimits.ManifestItems);
                m.manifestHash = Hash(m.items);
            }
            if (list.Count > LobbyLimits.AnnouncedPacks)
            {
                Debug.LogWarning($"[Online] {list.Count} packs are loaded, only the first {LobbyLimits.AnnouncedPacks} are announced online.");
                list.RemoveRange(LobbyLimits.AnnouncedPacks, list.Count - LobbyLimits.AnnouncedPacks);
            }
            built = list;
            builtFor = registry;
            builtVersion = registry.Version;
            return built;
        }

        static void AddItems(ContentRegistry registry, ContentPack pack, PackManifest manifest)
        {
            foreach (var def in pack.content)
            {
                if (def == null || registry.Get<ContentDefinition>(def.Id) != def) continue;
                var kind = ContentFingerprints.KindOf(def);
                if (kind == null) continue;
                if (!IsContentId(def.Id))
                {
                    Debug.LogWarning($"[Online] Content id '{def.Id}' of '{pack.packId}' is not 1-96 letters, digits, _ . or -: it cannot be used online.");
                    continue;
                }
                manifest.items.Add(new ManifestItem(kind.Value, def.Id, ContentFingerprints.Of(def)));
            }
        }

        // The commit is printable text of at most 40 bytes on the wire.
        static string Commit(ContentPack pack)
        {
            var v = pack.version ?? "";
            var sb = new StringBuilder(v.Length);
            foreach (var c in v)
                if (c >= 0x20 && c < 0x7f) sb.Append(c);
            return sb.Length > LobbyLimits.Commit ? sb.ToString(0, LobbyLimits.Commit) : sb.ToString();
        }

        /// <summary>
        /// The canonical manifest hash: items sorted by kind, then by the lowercased id (ordinal), each as the UTF-8 text
        /// "kind\nid lowercase\nfingerprint\n"; SHA-256 over all of them, the first 16 bytes as lowercase hex.
        /// </summary>
        public static string Hash(IReadOnlyList<ManifestItem> items)
        {
            var sorted = new List<(int kind, string id, string fingerprint)>(items.Count);
            foreach (var item in items) sorted.Add(((int)item.kind, item.id.ToLowerInvariant(), item.fingerprint ?? ""));
            sorted.Sort((a, b) => a.kind != b.kind ? a.kind.CompareTo(b.kind) : string.CompareOrdinal(a.id, b.id));
            using var sha = SHA256.Create();
            var sb = new StringBuilder();
            foreach (var (kind, id, fingerprint) in sorted) sb.Append(kind).Append('\n').Append(id).Append('\n').Append(fingerprint).Append('\n');
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(32);
            for (var i = 0; i < 16; i++) hex.Append(digest[i].ToString("x2"));
            return hex.ToString();
        }

        /// <summary>A content id the server accepts: 1-96 characters of A-Z a-z 0-9 _ . - (lowercasing stays ASCII).</summary>
        public static bool IsContentId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > LobbyLimits.ContentId) return false;
            foreach (var c in id)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-')) return false;
            return true;
        }

        /// <summary>A pack id the server accepts: a content id without dots of at most 64 characters.</summary>
        public static bool IsPackId(string id) => IsContentId(id) && id.Length <= LobbyLimits.PackId && id.IndexOf('.') < 0;

        /// <summary>The pack an item id names: the text before its first dot ("" when there is none).</summary>
        public static string PackOf(string id)
        {
            var dot = id?.IndexOf('.') ?? -1;
            return dot > 0 ? id.Substring(0, dot) : "";
        }

        /// <summary>
        /// Whether <paramref name="d"/> is content of a lobby with <paramref name="lobbyPacks"/>: its id names a lobby pack
        /// (or the game's own pack), that pack registered it itself (not a mod's replacement) and it is the effective definition.
        /// </summary>
        public static bool IsLobbyContent(ContentRegistry registry, ContentDefinition d, IReadOnlyList<string> lobbyPacks)
        {
            if (d == null || registry.Get<ContentDefinition>(d.Id) != d || !IsContentId(d.Id)) return false;
            var pack = PackOf(d.Id);
            if (pack.Length == 0 || !string.Equals(pack, d.OwnerPackId, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(pack, GamePackId, StringComparison.OrdinalIgnoreCase) || Contains(lobbyPacks, pack);
        }

        /// <summary>Lobby content of type <typeparamref name="T"/> in menu order (hidden definitions left out).</summary>
        public static List<T> LobbyContent<T>(ContentRegistry registry, IReadOnlyList<string> lobbyPacks) where T : ContentDefinition
        {
            var list = new List<T>();
            foreach (var d in registry.All<T>())
                if (IsLobbyContent(registry, d, lobbyPacks)) list.Add(d);
            return list;
        }

        /// <summary>Packs of <paramref name="lobbyPacks"/> that are not loaded here.</summary>
        public static List<string> MissingPacks(ContentRegistry registry, IReadOnlyList<string> lobbyPacks)
        {
            var missing = new List<string>();
            if (lobbyPacks == null) return missing;
            foreach (var id in lobbyPacks)
            {
                var loaded = false;
                foreach (var p in registry.Packs)
                    if (p != null && string.Equals(p.packId, id, StringComparison.OrdinalIgnoreCase)) loaded = true;
                if (!loaded) missing.Add(id);
            }
            return missing;
        }

        /// <summary>
        /// Definitions of the lobby packs <paramref name="lobbyPacks"/> (and the game's own pack) that a mod replaces here:
        /// the id of each with the pack that replaces it. Such a mod must be disabled to play in the lobby.
        /// </summary>
        public static List<(string id, string by)> Overrides(ContentRegistry registry, IReadOnlyList<string> lobbyPacks)
        {
            var list = new List<(string, string)>();
            foreach (var pack in registry.Packs)
            {
                if (pack == null) continue;
                var lobbyPack = string.Equals(pack.packId, GamePackId, StringComparison.OrdinalIgnoreCase) || Contains(lobbyPacks, pack.packId);
                if (!lobbyPack) continue;
                foreach (var def in pack.content)
                {
                    if (def == null) continue;
                    var effective = registry.Get<ContentDefinition>(def.Id);
                    if (effective == null || effective == def || string.Equals(effective.OwnerPackId, pack.packId, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add((def.Id, RootTitle(registry, effective.OwnerPack)));
                }
            }
            return list;
        }

        /// <summary>
        /// Id of the pack whose definition of <paramref name="id"/> is the effective one here when that is not the pack the
        /// id names (a mod replaces it through the load order), else null. The server derives the same from the manifests.
        /// </summary>
        public static string OverriddenBy(ContentRegistry registry, string id)
        {
            var effective = string.IsNullOrEmpty(id) ? null : registry.Get<ContentDefinition>(id);
            if (effective == null || effective.OwnerPackId.Length == 0
                || string.Equals(effective.OwnerPackId, PackOf(id), StringComparison.OrdinalIgnoreCase)) return null;
            return effective.OwnerPackId;
        }

        // The mod the player knows (Mods screen): the top-level pack of the replacing pack.
        static string RootTitle(ContentRegistry registry, ContentPack pack)
        {
            var root = registry.RootOf(pack) ?? pack;
            if (root == null) return "?";
            return string.IsNullOrEmpty(root.title) ? root.packId : root.title;
        }

        static bool Contains(IReadOnlyList<string> list, string id)
        {
            if (list == null) return false;
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
