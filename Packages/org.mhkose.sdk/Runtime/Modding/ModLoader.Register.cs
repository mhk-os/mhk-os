using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>Why an installed mod of <see cref="ModLoader.AvailablePacks"/> is not loaded this session.</summary>
    public enum ModProblem
    {
        /// <summary>Loaded, or disabled by the player.</summary>
        None,
        /// <summary>Built for an older mod API level (<see cref="ModSdk.ApiLevel"/>): its author has to release an update.</summary>
        NeedsModUpdate,
        /// <summary>Built for a newer mod API level: it needs a newer game.</summary>
        NeedsNewerGame,
        /// <summary>Its files or content failed to load or register (<see cref="ModLoader.ProblemDetail"/> tells why).</summary>
        LoadFailed,
    }

    public static partial class ModLoader
    {
        const string UnreadableFile = "Its file is damaged or made for another platform.";
        const string UnreadableSceneFile = "Its scene file is damaged or made for another platform.";
        const string MissingScripts = "Its content needs scripts this game does not have.";
        const int MaxDetailLength = 120;
        const string BundleSignature = "UnityFS";

        [AutoStaticsCleanup] static readonly Dictionary<ContentPack, AssetBundle> sceneBundleOf = new();
        [AutoStaticsCleanup] static readonly Dictionary<string, (ModProblem problem, string detail)> problems = new(StringComparer.OrdinalIgnoreCase);

        sealed class ModLoadException : Exception
        {
            public ModLoadException(string message) : base(message) { }
        }

        /// <summary>Why the mod <paramref name="pack"/> of <see cref="AvailablePacks"/> is not loaded, <see cref="ModProblem.None"/> when it is or the player disabled it.</summary>
        public static ModProblem ProblemOf(ContentPack pack) =>
            pack != null && pack.packId != null && problems.TryGetValue(pack.packId, out var p) ? p.problem : ModProblem.None;

        /// <summary>One-line reason of a <see cref="ModProblem.LoadFailed"/> mod, null otherwise.</summary>
        public static string ProblemDetail(ContentPack pack) =>
            pack != null && pack.packId != null && problems.TryGetValue(pack.packId, out var p) ? p.detail : null;

        // Only the API level decides (an unstamped bundle has the first level). Definitions whose script this game lacks
        // load as empty entries.
        static bool CheckBuild(ContentPack pack, string file)
        {
            if (!ModSdk.IsCompatible(pack.sdkApi))
            {
                var level = ModSdk.Level(pack.sdkApi);
                var newer = level > ModSdk.ApiLevel;
                problems[pack.packId] = (newer ? ModProblem.NeedsNewerGame : ModProblem.NeedsModUpdate, null);
                var built = pack.sdkApi > 0 ? $"built for mod API level {level}" : $"built before the mod API level existed (level {level})";
                Debug.LogWarning($"[Mods] '{pack.packId}' ({file}) is not loaded: {built}, this game has level {ModSdk.ApiLevel}. " +
                                 (newer ? "It needs a newer game." : "Its author has to release a build for this game."));
                return false;
            }
            var missing = pack.SelfAndIncluded().Sum(p => p.content.Count(d => d == null));
            if (missing == 0) return true;
            Fail(pack.packId, file, MissingScripts, $"{missing} definitions need scripts this game does not have (built with a newer SDK?).");
            return false;
        }

        static void Fail(string packId, string where, Exception e)
        {
            if (e is ModLoadException) Fail(packId, where, e.Message, e.Message);
            else Fail(packId, where, OneLine(e), e.ToString());
        }

        static void Fail(string packId, string where, string detail, string log)
        {
            problems[packId] = (ModProblem.LoadFailed, detail);
            Debug.LogWarning($"[Mods] '{packId}' ({where}) is not loaded: {log}");
        }

        static string OneLine(Exception e)
        {
            var message = e.Message ?? "";
            var newline = message.IndexOf('\n');
            var line = $"{e.GetType().Name}: {(newline >= 0 ? message.Substring(0, newline) : message).Trim()}";
            return line.Length > MaxDetailLength ? line.Substring(0, MaxDetailLength) : line;
        }

        // A mod that throws while it registers or resolves is dropped and the others register again from the start, so
        // nothing keeps pointing at its content. Logged once, after the last pass.
        static void RegisterMods(ContentRegistry registry, ICollection<string> disabledPackIds)
        {
            var failed = new List<ContentPack>();
            var resolved = new List<(string packId, int remapped, List<string> dropped)>();
            while (!TryRegister(registry, disabledPackIds, failed, resolved))
            {
                foreach (var def in failed.SelectMany(p => p.SelfAndIncluded()).SelectMany(p => p.content ?? Enumerable.Empty<ContentDefinition>()))
                    if (def != null) def.OwnerPack = null;
                registry.Clear();
                ContentReferenceResolver.RestoreRecorded();
                RegisterBuiltin(registry, false);
            }
            foreach (var pack in failed) Drop(pack);
            LogRegistered(disabledPackIds);
            foreach (var (packId, remapped, dropped) in resolved)
            {
                if (remapped > dropped.Count)
                    Debug.Log($"[Mods] '{packId}': {remapped - dropped.Count} references to other packs' content resolved by id.");
                if (dropped.Count > 0)
                    Debug.LogWarning($"[Mods] '{packId}': {dropped.Count} references to content of mods that are not loaded removed " +
                                     $"({string.Join(", ", dropped.Distinct())}).");
            }
        }

        static bool TryRegister(ContentRegistry registry, ICollection<string> disabledPackIds, List<ContentPack> failed,
                                List<(string packId, int remapped, List<string> dropped)> resolved)
        {
            for (var i = available.Count - 1; i >= 0; i--)
            {
                var pack = available[i];
                if (!IsEnabled(pack, disabledPackIds) || problems.ContainsKey(pack.packId)) continue;
                try
                {
                    registry.RegisterRoot(pack);
                }
                catch (Exception e)
                {
                    return Failed(pack, e, failed);
                }
            }
            resolved.Clear();
            foreach (var p in registry.Packs.ToList())
            {
                var root = registry.RootOf(p);
                var dropped = new List<string>();
                try
                {
                    // Bundle content is resolved in place (it goes with its bundle). The game's own and project packs are
                    // shared assets: their changes are recorded and undone before the next load and when play mode ends.
                    resolved.Add((p.packId, ContentReferenceResolver.Resolve(registry, p, !folderOf.ContainsKey(root), dropped), dropped));
                }
                catch (Exception e)
                {
                    if (available.Contains(root)) return Failed(root, e, failed);
                    Debug.LogWarning($"[Mods] References of the game pack '{p.packId}' could not be resolved: {e}");
                }
            }
            return true;
        }

        static bool Failed(ContentPack pack, Exception e, List<ContentPack> failed)
        {
            Fail(pack.packId, FolderOf(pack) ?? "project", e);
            failed.Add(pack);
            return false;
        }

        static void LogRegistered(ICollection<string> disabledPackIds)
        {
            for (var i = available.Count - 1; i >= 0; i--)
            {
                var pack = available[i];
                if (problems.ContainsKey(pack.packId)) continue;
                if (!IsEnabled(pack, disabledPackIds))
                {
                    Debug.Log($"[Mods] Pack '{pack.packId}' disabled by settings.");
                    continue;
                }
                var commit = string.IsNullOrEmpty(pack.version) ? "" : $" (commit {pack.version})";
                Debug.Log($"[Mods] Loaded pack '{pack.packId}'{commit}, load order {i + 1} of {available.Count} ({pack.TotalContentCount} definitions" +
                          (pack.includes.Count > 0 ? $" in {pack.SelfAndIncluded().Count()} packs" : "") + ").");
            }
        }

        // A failed bundle mod becomes a stand-in and its bundles unload. A project pack (editor) stays listed as it is.
        static void Drop(ContentPack pack)
        {
            if (!bundleOf.Remove(pack, out var bundle)) return;
            var index = available.IndexOf(pack);
            if (index >= 0) available[index] = SafeStandIn(pack);
            if (!bundleOf.ContainsValue(bundle)) Unload(bundle);
            if (sceneBundleOf.Remove(pack, out var scenes))
            {
                Unload(scenes);
                bundleScenes.Clear();
                foreach (var b in sceneBundleOf.Values) bundleScenes.UnionWith(b.GetAllScenePaths());
            }
            folderOf.Remove(pack);
        }

        static ContentPack SafeStandIn(ContentPack pack)
        {
            try
            {
                return StandIn(pack, FolderOf(pack));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mods] '{pack.packId}': its logo could not be kept for the Mods screen ({e.Message}).");
                return StandIn(pack.packId, pack.title, pack.loadOrder);
            }
        }

        // Unity logs errors for a file that is no AssetBundle at all, such a file fails here without them. A file inside
        // the Android APK cannot be read here, only Unity can tell.
        static bool LooksLikeBundle(string file)
        {
            if (!File.Exists(file)) return true;
            var head = new byte[BundleSignature.Length];
            using var stream = File.OpenRead(file);
            return stream.Read(head, 0, head.Length) == head.Length && Encoding.ASCII.GetString(head) == BundleSignature;
        }

        static void Unload(AssetBundle bundle)
        {
            loadedBundles.Remove(bundle);
            bundle.Unload(true);
        }
    }
}
