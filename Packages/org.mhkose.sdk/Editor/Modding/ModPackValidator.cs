using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Pre-build checks of a mod pack: pack id set and free, definition ids namespaced, track scenes present. The game's
    /// and the base content's ids come from the project and from the SDK's <see cref="CoreIndex"/> (mod projects).
    /// </summary>
    public static class ModPackValidator
    {
        /// <summary>Track scenes of a pack and the packs it includes.</summary>
        public static string[] Scenes(ContentPack p) =>
            p.SelfAndIncluded().SelectMany(x => x.GetAll<TrackDefinition>())
                .Where(t => t.scene.IsSet).Select(t => t.scene.Path).Distinct().ToArray();

        /// <summary>Logs the errors that break the mod (then returns false) and warnings.</summary>
        public static bool Check(ContentPack root)
        {
            if (string.IsNullOrWhiteSpace(root.packId))
            {
                Debug.LogError($"[Mods] {AssetDatabase.GetAssetPath(root)}: the pack has no packId.", root);
                return false;
            }
            var builtin = ContentPackAssets.BuiltinPacks();
            if (builtin.Contains(root))
            {
                Debug.LogError($"[Mods] '{root.packId}' is compiled into the game, it is not a mod.", root);
                return false;
            }
            var others = builtin.Concat(ContentPackAssets.ShippedPacks().Where(p => p != root).SelectMany(p => p.SelfAndIncluded())).ToList();
            var index = CoreIndex.Load();
            var otherIds = new HashSet<string>(others.SelectMany(p => p.content).Where(d => d != null).Select(d => d.Id).Concat(index.Ids(root.packId)));
            var otherPackIds = new HashSet<string>(others.Select(p => p.packId).Concat(index.PackIds(root.packId)));
            var ok = true;
            foreach (var pack in root.SelfAndIncluded())
            {
                if (otherPackIds.Contains(pack.packId))
                {
                    Debug.LogError($"[Mods] Pack id '{pack.packId}' is taken by the game or a shipped mod.", pack);
                    ok = false;
                }
                foreach (var def in pack.content) ok &= CheckDefinition(pack, def, otherIds);
            }
            return ok;
        }

        static bool CheckDefinition(ContentPack pack, ContentDefinition def, HashSet<string> otherIds)
        {
            if (def == null)
            {
                Debug.LogError($"[Mods] '{pack.packId}': empty entry in the content list. Remove it: the game does not load a mod with an empty " +
                               "entry (it looks like content whose script is missing).", pack);
                return false;
            }
            if (!def.Id.StartsWith(pack.packId + ".") && !otherIds.Contains(def.Id))
                Debug.LogWarning($"[Mods] '{def.Id}' ({AssetDatabase.GetAssetPath(def)}) is not namespaced '{pack.packId}.<name>': ids are global, " +
                                 "so it may collide with other mods. Reuse a base content id only to override that content on purpose.", def);
            else if (otherIds.Contains(def.Id))
                Debug.Log($"[Mods] '{pack.packId}' replaces '{def.Id}' of the game or the base content while it is higher in the load order " +
                          "(a higher loadOrder places it higher by default).", def);
            if (def is not TrackDefinition t || t.scene.IsSet) return true;
            Debug.LogError($"[Mods] Track '{t.Id}' has no scene.", t);
            return false;
        }
    }
}
