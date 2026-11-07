using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Editor.Tracks;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Pre-build checks of a mod pack: pack id set and free, definition ids namespaced, track scenes present, track overlays
    /// naming a known track and option with an overlay scene rooted in a TrackOverlay. The game's and the base content's ids
    /// come from the project and from the SDK's <see cref="CoreIndex"/> (mod projects).
    /// </summary>
    public static class ModPackValidator
    {
        /// <summary>Track scenes and track overlay scenes of a pack and the packs it includes (the pack's scene bundle).</summary>
        public static string[] Scenes(ContentPack p)
        {
            var packs = p.SelfAndIncluded().ToList();
            return packs.SelectMany(x => x.GetAll<TrackDefinition>()).Select(t => t.scene)
                .Concat(packs.SelectMany(x => x.GetAll<TrackOverlayDefinition>()).Select(o => o.scene))
                .Where(s => s.IsSet).Select(s => s.Path).Distinct().ToArray();
        }

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
            var known = new KnownIds(index);
            var ok = true;
            foreach (var pack in root.SelfAndIncluded())
            {
                if (otherPackIds.Contains(pack.packId))
                {
                    Debug.LogError($"[Mods] Pack id '{pack.packId}' is taken by the game or a shipped mod.", pack);
                    ok = false;
                }
                foreach (var def in pack.content) ok &= CheckDefinition(pack, def, otherIds, known);
            }
            return ok;
        }

        static bool CheckDefinition(ContentPack pack, ContentDefinition def, HashSet<string> otherIds, KnownIds known)
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
            return def switch
            {
                TrackDefinition t => CheckTrack(t),
                TrackOverlayDefinition o => CheckOverlay(o, known),
                _ => true,
            };
        }

        static bool CheckTrack(TrackDefinition t)
        {
            if (t.scene.IsSet) return true;
            Debug.LogError($"[Mods] Track '{t.Id}' has no scene.", t);
            return false;
        }

        static bool CheckOverlay(TrackOverlayDefinition o, KnownIds known)
        {
            var ok = true;
            if (!known.Has<TrackDefinition>(o.trackId))
            {
                Debug.LogError($"[Mods] Track overlay '{o.Id}': no track '{o.trackId}' in the project or the base content.", o);
                ok = false;
            }
            if (!known.Has<TrackOptionDefinition>(o.optionId))
            {
                Debug.LogError($"[Mods] Track overlay '{o.Id}': no track option '{o.optionId}' in the project or the base content.", o);
                ok = false;
            }
            if (!o.scene.IsSet) return ok;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(o.scene.Path) == null)
            {
                Debug.LogError($"[Mods] Track overlay '{o.Id}': its scene {o.scene.Path} is missing.", o);
                return false;
            }
            var rooted = TrackScenes.WithAdditive(o.scene.Path, scene => TrackSceneLayout.FindOverlay(scene) != null, SceneSave.Never);
            if (rooted) return ok;
            Debug.LogError($"[Mods] Track overlay '{o.Id}': the root of its scene {o.scene.Path} has no TrackOverlay.", o);
            return false;
        }

        // Ids of the project's definition assets and of the base content's definitions, by definition type.
        sealed class KnownIds
        {
            readonly Dictionary<Type, HashSet<string>> byType = new();
            readonly CoreIndex index;

            public KnownIds(CoreIndex index) => this.index = index;

            public bool Has<T>(string id) where T : ContentDefinition
            {
                if (string.IsNullOrEmpty(id)) return false;
                if (!byType.TryGetValue(typeof(T), out var ids))
                    byType[typeof(T)] = ids = new HashSet<string>(EditorAssets.FindAll<T>().Select(d => d.Id)
                        .Concat(index.definitions.Where(d => d.type == typeof(T).FullName).Select(d => d.id)), StringComparer.OrdinalIgnoreCase);
                return ids.Contains(id);
            }
        }
    }
}
