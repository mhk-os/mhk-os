using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>Status effects by id for ghost playback: the content's, the race config's, else any loaded definition.</summary>
    static partial class GhostEffectLookup
    {
        [AutoStaticsCleanup] static readonly Dictionary<string, StatusEffectDefinition> Cache = new();
        [AutoStaticsCleanup] static ContentRegistry cachedContent;
        [AutoStaticsCleanup] static int cachedVersion;

        public static StatusEffectDefinition Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var root = GameRoot.Instance;
            ForgetOnContentChange(root.Content);
            if (Cache.TryGetValue(id, out var def)) return def;
            def = root.Content.Get<StatusEffectDefinition>(id);
            if (def == null)
                foreach (var d in root.Config.RaceEffects)
                    if (d != null && d.Id == id) def = d;
            if (def == null)
                foreach (var d in Resources.FindObjectsOfTypeAll<StatusEffectDefinition>())
                    if (d.Id == id) { def = d; break; }
            Cache[id] = def;
            return def;
        }

        // Mods enabled or disabled in the menu change the registry: cached hits and misses no longer apply.
        static void ForgetOnContentChange(ContentRegistry content)
        {
            if (content == cachedContent && content.Version == cachedVersion) return;
            Cache.Clear();
            cachedContent = content;
            cachedVersion = content.Version;
        }
    }
}
