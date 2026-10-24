using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Runtime index of all registered content. Lookups by id are O(1), lists preserve registration order
    /// (pack load order, then order inside the pack). Later registrations override earlier ones with the same id.
    /// </summary>
    public class ContentRegistry
    {
        readonly Dictionary<string, ContentDefinition> byId = new(StringComparer.OrdinalIgnoreCase);
        readonly List<ContentDefinition> ordered = new();
        readonly List<ContentPack> packs = new();
        readonly HashSet<ContentPack> roots = new();
        readonly Dictionary<ContentPack, ContentPack> rootOf = new();
        readonly Dictionary<Type, object> allByType = new();

        /// <summary>Every registered pack, including packs bundled inside other packs.</summary>
        public IReadOnlyList<ContentPack> Packs => packs;

        /// <summary>Changes whenever registrations change (caches keyed by content compare it).</summary>
        public int Version { get; private set; }

        /// <summary>Registers a mod and everything it includes (included packs own their definitions).</summary>
        public void RegisterRoot(ContentPack root)
        {
            if (root == null || !roots.Add(root)) return;
            foreach (var p in root.SelfAndIncluded()) Register(p, root);
        }

        /// <summary>
        /// The top-level pack (mod) that registered <paramref name="pack"/>: the pack itself or the one that includes it
        /// (the core game for its original games). Null for an unregistered pack.
        /// </summary>
        public ContentPack RootOf(ContentPack pack) => pack != null && rootOf.TryGetValue(pack, out var root) ? root : null;

        void Register(ContentPack pack, ContentPack root)
        {
            if (packs.Contains(pack)) return;
            packs.Add(pack);
            rootOf[pack] = root;
            allByType.Clear();
            Version++;
            foreach (var def in pack.content)
            {
                if (def == null) continue;
                if (byId.TryGetValue(def.Id, out var existing))
                {
                    LogOverride(def.Id, existing.OwnerPack, pack, root);
                    ordered[ordered.IndexOf(existing)] = def;
                }
                else
                {
                    ordered.Add(def);
                }
                def.OwnerPack = pack;
                byId[def.Id] = def;
            }
        }

        // A higher load order overriding an id is a feature. The same id twice in one mod or at the same load order is not.
        void LogOverride(string id, ContentPack before, ContentPack pack, ContentPack root)
        {
            var beforeRoot = RootOf(before);
            var beforeName = before != null ? before.packId : "?";
            if (beforeRoot == root)
                Debug.LogWarning($"[Content] '{id}' is defined twice in mod '{root.packId}' ('{beforeName}' and '{pack.packId}'): '{pack.packId}' wins.");
            else if (beforeRoot != null && beforeRoot.loadOrder == root.loadOrder)
                Debug.LogWarning($"[Content] '{id}' is defined by '{beforeRoot.packId}' and '{root.packId}' at the same load order {root.loadOrder}: " +
                                 $"'{root.packId}' wins by file order. Give the overriding mod a higher loadOrder.");
            else
                Debug.Log($"[Content] Override: '{root.packId}' (load order {root.loadOrder}) replaces '{id}' of '{beforeName}'" +
                          (beforeRoot != null ? $" (mod '{beforeRoot.packId}', load order {beforeRoot.loadOrder})." : "."));
        }

        public void Clear()
        {
            byId.Clear();
            ordered.Clear();
            packs.Clear();
            roots.Clear();
            rootOf.Clear();
            allByType.Clear();
            Version++;
        }

        public T Get<T>(string id) where T : ContentDefinition =>
            id != null && byId.TryGetValue(id, out var d) ? d as T : null;

        /// <summary>The kart <paramref name="kartId"/>, else the default kart of <paramref name="character"/>, null if neither exists.</summary>
        public KartDefinition ResolveKart(string kartId, CharacterDefinition character)
        {
            var kart = Get<KartDefinition>(kartId);
            if (kart == null && character != null) kart = character.defaultKart;
            return kart;
        }

        /// <summary>
        /// All registered, non-hidden definitions of a type. The list is cached (per-frame callers such as the HUD
        /// roulette allocate nothing) and shared between callers: never cast it back and modify it.
        /// </summary>
        public IReadOnlyList<T> All<T>() where T : ContentDefinition
        {
            if (allByType.TryGetValue(typeof(T), out var cached)) return (List<T>)cached;
            var list = new List<T>();
            for (var i = 0; i < ordered.Count; i++)
                if (ordered[i] is T d && !d.Hidden) list.Add(d);
            allByType[typeof(T)] = list;
            return list;
        }
    }
}
