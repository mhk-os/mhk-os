using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Achievements
{
    /// <summary>
    /// The achievements of the loaded content (<see cref="AchievementDefinition"/>) and the player's progress
    /// (<see cref="AchievementStats"/>), kept in achievements.json: this device's totals, the progress no account has
    /// received yet and the packs of achievement parts seen. While an account is logged in and its totals are loaded
    /// (<see cref="Synced"/>), progress is the account's; otherwise this device's. Unlocks are never taken back: one
    /// unlocked here or by an account logged in here stays unlocked on this device. Main thread only.
    /// </summary>
    public static partial class Achievements
    {
        public const string File = "achievements.json";
        // Quiet time after a change before the file is written and the account synced (never during a race).
        const float CommitDelaySeconds = 3f;

        /// <summary>Progress, unlocks, the account sync or the player shares changed.</summary>
        public static event Action Changed;
        /// <summary>An achievement was just unlocked (main thread).</summary>
        public static event Action<AchievementView> Unlocked;

        [AutoStaticsCleanup] static StatBook local;
        [AutoStaticsCleanup] static StatBook pending;
        [AutoStaticsCleanup] static StatBook account;
        [AutoStaticsCleanup] static readonly Dictionary<string, HashSet<string>> partPacks = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly List<AchievementView> visible = new();
        [AutoStaticsCleanup] static ContentRegistry builtFor;
        [AutoStaticsCleanup] static int builtVersion;
        [AutoStaticsCleanup] static readonly Dictionary<string, uint> shareCounts = new(StringComparer.Ordinal);
        [AutoStaticsCleanup] static uint sharePlayers;
        [AutoStaticsCleanup] static bool dirty;
        [AutoStaticsCleanup] static bool committing;
        [AutoStaticsCleanup] static float commitAt;

        /// <summary>
        /// The achievements whose required packs (and the packs of their parts) are all loaded, by order then title.
        /// Secret ones are listed with their secret texts until unlocked.
        /// </summary>
        public static IReadOnlyList<AchievementView> Visible
        {
            get
            {
                var content = Content;
                if (content != builtFor || content != null && content.Version != builtVersion) Reevaluate();
                return visible;
            }
        }

        public static int UnlockedVisibleCount
        {
            get
            {
                var n = 0;
                foreach (var v in Visible)
                    if (v.Unlocked) n++;
                return n;
            }
        }

        /// <summary>An account is logged in and its totals are loaded: progress shown is the account's.</summary>
        public static bool Synced => account != null;

        /// <summary>Asks the server how many players unlocked the visible achievements; the views update (<see cref="Changed"/>).</summary>
        public static void RefreshShares()
        {
            var ids = new List<string>();
            foreach (var v in Visible) ids.Add(v.Id);
            // More than one request may name: ask for every achievement instead.
            if (ids.Count > AchievementSharesRequest.MaxIds) ids.Clear();
            OnlineAchievements.FetchShares(ids);
        }

        static ContentRegistry Content => GameRoot.Instance != null ? GameRoot.Instance.Content : null;

        // ---- Recording (AchievementStats) ----------------------------------------------------------------------------

        internal static void AddCounter(string stat, long delta)
        {
            EnsureLoaded();
            local.Add(stat, delta);
            pending.Add(stat, delta);
            StatsChanged();
        }

        internal static void RaiseMaximum(string stat, long value)
        {
            EnsureLoaded();
            var changed = local.Raise(stat, value);
            if (account == null || value > account.Maximum(stat)) changed |= pending.Raise(stat, value);
            if (changed) StatsChanged();
        }

        internal static void CollectItem(string stat, string item)
        {
            EnsureLoaded();
            var changed = local.Collect(stat, item);
            if (account == null || !account.Has(stat, item)) changed |= pending.Collect(stat, item);
            if (changed) StatsChanged();
        }

        static void StatsChanged()
        {
            dirty = true;
            Reevaluate();
            Changed?.Invoke();
            ScheduleCommit();
        }

        // ---- Account sync (OnlineAchievements) -----------------------------------------------------------------------

        internal static bool HasPending
        {
            get
            {
                EnsureLoaded();
                return !pending.IsEmpty;
            }
        }

        /// <summary>The progress no account has received yet, within the limits of one request.</summary>
        internal static SyncAchievementsRequest TakeBatch()
        {
            EnsureLoaded();
            var r = new SyncAchievementsRequest();
            foreach (var (stat, value) in pending.counters)
            {
                if (r.counters.Count == SyncAchievementsRequest.MaxStats) break;
                r.counters.Add((stat, (uint)Math.Min(value, uint.MaxValue)));
            }
            foreach (var (stat, value) in pending.maxima)
            {
                if (r.maxima.Count == SyncAchievementsRequest.MaxStats) break;
                r.maxima.Add((stat, (uint)Math.Min(value, uint.MaxValue)));
            }
            foreach (var (stat, set) in pending.sets)
            {
                if (r.sets.Count == SyncAchievementsRequest.MaxSets) break;
                var items = new List<string>(Math.Min(set.Count, SyncAchievementsRequest.MaxSetItems));
                foreach (var item in set)
                {
                    if (items.Count == SyncAchievementsRequest.MaxSetItems) break;
                    items.Add(item);
                }
                r.sets.Add((stat, items));
            }
            foreach (var (id, at) in pending.unlocked)
            {
                if (r.unlocked.Count == SyncAchievementsRequest.MaxUnlocked) break;
                r.unlocked.Add((id, DateTimeOffset.FromUnixTimeMilliseconds(at)));
            }
            return r;
        }

        /// <summary>
        /// The account has <paramref name="sent"/> (or refused it for good): it leaves the pending progress, what was
        /// recorded meanwhile stays.
        /// </summary>
        internal static void Delivered(SyncAchievementsRequest sent)
        {
            EnsureLoaded();
            foreach (var (stat, delta) in sent.counters)
            {
                var left = pending.Counter(stat) - delta;
                if (left > 0) pending.counters[stat] = left;
                else pending.counters.Remove(stat);
            }
            foreach (var (stat, value) in sent.maxima)
                if (pending.Maximum(stat) <= value) pending.maxima.Remove(stat);
            foreach (var (stat, items) in sent.sets)
            {
                if (!pending.sets.TryGetValue(stat, out var set)) continue;
                set.ExceptWith(items);
                if (set.Count == 0) pending.sets.Remove(stat);
            }
            foreach (var (id, at) in sent.unlocked)
                if (pending.unlocked.TryGetValue(id, out var t) && t >= at.ToUnixTimeMilliseconds()) pending.unlocked.Remove(id);
            Save();
        }

        /// <summary>
        /// The logged-in account's totals (null = logged out or another account): progress shows them from now on and
        /// their unlocks are kept on this device.
        /// </summary>
        internal static void SetAccountTotals(AchievementTotals totals)
        {
            EnsureLoaded();
            if (totals == null)
            {
                if (account == null) return;
                account = null;
            }
            else
            {
                account = new StatBook();
                foreach (var (stat, value) in totals.counters) account.counters[stat] = (long)Math.Min(value, long.MaxValue);
                foreach (var (stat, value) in totals.maxima) account.Raise(stat, value);
                foreach (var (stat, items) in totals.sets)
                foreach (var item in items)
                    account.Collect(stat, item);
                foreach (var (id, at) in totals.unlocked)
                {
                    var ms = at.ToUnixTimeMilliseconds();
                    account.Unlock(id, ms);
                    dirty |= local.Unlock(id, ms);
                }
            }
            Reevaluate();
            Changed?.Invoke();
            if (dirty) ScheduleCommit();
        }

        internal static void SetShares(AchievementShareCounts counts)
        {
            sharePlayers = counts.players;
            shareCounts.Clear();
            foreach (var (id, unlocked) in counts.shares) shareCounts[id] = unlocked;
            foreach (var v in visible) v.SetShare(Share(v.Id));
            Changed?.Invoke();
        }

        // Before the first answer, and without players, the share is unknown.
        static float? Share(string id)
        {
            if (sharePlayers == 0) return null;
            return shareCounts.TryGetValue(id, out var n) ? Math.Min(n, sharePlayers) / (float)sharePlayers : 0f;
        }

        // ---- Evaluation ----------------------------------------------------------------------------------------------

        static void Reevaluate()
        {
            EnsureLoaded();
            if (EnsureViews()) Refresh();
        }

        // Rebuilds the views when the content changed. False without content (no game running).
        static bool EnsureViews()
        {
            var content = Content;
            if (content == null)
            {
                builtFor = null;
                visible.Clear();
                return false;
            }
            if (content == builtFor && content.Version == builtVersion) return true;
            builtFor = content;
            builtVersion = content.Version;
            BuildViews(content);
            return true;
        }

        static void BuildViews(ContentRegistry content)
        {
            visible.Clear();
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in content.Packs) loaded.Add(p.packId);
            var parts = content.All<AchievementPartDefinition>();
            foreach (var part in parts) RememberPartPack(part);
            foreach (var def in content.All<AchievementDefinition>())
            {
                if (!AchievementStats.IsValidId(def.Id, AchievementStats.MaxItemLength))
                {
                    Debug.LogWarning($"[Achievements] '{def.Id}' of '{def.OwnerPackId}' is not shown: achievement ids are 1-{AchievementStats.MaxItemLength} characters of A-Z, a-z, 0-9, '_', '.', '-'.");
                    continue;
                }
                if (!AllLoaded(def.requiredPacks, loaded) || partPacks.TryGetValue(def.Id, out var partOwners) && !AllLoaded(partOwners, loaded)) continue;
                visible.Add(new AchievementView(def, def.kind == AchievementGoalKind.Collection ? Items(content, def, parts) : null));
            }
            visible.Sort(CompareViews);
        }

        static int CompareViews(AchievementView a, AchievementView b)
        {
            var byOrder = a.Definition.order.CompareTo(b.Definition.order);
            if (byOrder != 0) return byOrder;
            var byTitle = string.Compare(a.Definition.DisplayName, b.Definition.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            return byTitle != 0 ? byTitle : string.CompareOrdinal(a.Id, b.Id);
        }

        static bool AllLoaded(IEnumerable<string> packs, HashSet<string> loaded)
        {
            foreach (var p in packs)
                if (!string.IsNullOrEmpty(p) && !loaded.Contains(p)) return false;
            return true;
        }

        static void RememberPartPack(AchievementPartDefinition part)
        {
            var pack = part.OwnerPackId;
            if (string.IsNullOrEmpty(part.achievement) || string.IsNullOrEmpty(pack)) return;
            if (!partPacks.TryGetValue(part.achievement, out var packs)) partPacks[part.achievement] = packs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!packs.Add(pack)) return;
            dirty = true;
            ScheduleCommit();
        }

        // The required packs' content of the collected kind, the content of parts that collect their own pack's, and
        // the items parts name.
        static HashSet<string> Items(ContentRegistry content, AchievementDefinition def, IReadOnlyList<AchievementPartDefinition> parts)
        {
            var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in def.requiredPacks) AddOwned(content, def.collect, pack, items);
            foreach (var part in parts)
            {
                if (!string.Equals(part.achievement, def.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (part.collectOwnPack) AddOwned(content, def.collect, part.OwnerPackId, items);
                foreach (var id in part.items)
                    if (!string.IsNullOrEmpty(id)) items.Add(id);
            }
            return items;
        }

        static void AddOwned(ContentRegistry content, AchievementCollect collect, string pack, HashSet<string> items)
        {
            if (string.IsNullOrEmpty(pack)) return;
            if (collect == AchievementCollect.Cup)
            {
                foreach (var cup in content.All<CupDefinition>())
                    if (OwnedBy(cup, pack)) items.Add(cup.Id);
                return;
            }
            var arenas = collect == AchievementCollect.BattleArena;
            foreach (var track in content.All<TrackDefinition>())
                if ((track.layout == TrackLayout.Arena) == arenas && OwnedBy(track, pack)) items.Add(track.Id);
        }

        // A mod that replaces a pack's content keeps its id, so the content still counts for that pack.
        static bool OwnedBy(ContentDefinition def, string pack) =>
            string.Equals(def.OwnerPackId, pack, StringComparison.OrdinalIgnoreCase) || def.Id.StartsWith(pack + ".", StringComparison.OrdinalIgnoreCase);

        // Updates every view from the progress in effect and unlocks what is reached.
        static void Refresh()
        {
            List<AchievementView> unlockedNow = null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var view in visible)
            {
                var def = view.Definition;
                long current, target;
                switch (def.kind)
                {
                    case AchievementGoalKind.Collection:
                        current = 0;
                        if (!string.IsNullOrEmpty(def.stat))
                            foreach (var item in view.Items)
                                if (Collected(def.stat, item)) current++;
                        target = def.target > 0 ? Math.Min(def.target, view.Items.Count) : view.Items.Count;
                        break;
                    case AchievementGoalKind.Maximum:
                        current = 0;
                        foreach (var stat in def.stats)
                            if (!string.IsNullOrEmpty(stat)) current = Math.Max(current, MaximumOf(stat));
                        target = def.target;
                        break;
                    default:
                        current = 0;
                        foreach (var stat in def.stats)
                            if (!string.IsNullOrEmpty(stat)) current += CounterOf(stat);
                        target = def.target;
                        break;
                }
                if (!local.unlocked.TryGetValue(view.Id, out var at)) at = -1;
                if (at < 0 && target > 0 && current >= target)
                {
                    at = now;
                    local.Unlock(view.Id, at);
                    pending.Unlock(view.Id, at);
                    dirty = true;
                    (unlockedNow ??= new List<AchievementView>()).Add(view);
                }
                view.Set(current, target, at, Share(view.Id));
            }
            if (unlockedNow == null) return;
            ScheduleCommit();
            foreach (var view in unlockedNow)
            {
                Debug.Log($"[Achievements] Unlocked '{view.Id}'.");
                Unlocked?.Invoke(view);
            }
        }

        static long CounterOf(string stat) => account != null ? account.Counter(stat) + pending.Counter(stat) : local.Counter(stat);

        static long MaximumOf(string stat) => account != null ? Math.Max(account.Maximum(stat), pending.Maximum(stat)) : local.Maximum(stat);

        static bool Collected(string stat, string item) => account != null ? account.Has(stat, item) || pending.Has(stat, item) : local.Has(stat, item);

        // ---- Storage -------------------------------------------------------------------------------------------------

        static void EnsureLoaded()
        {
            if (local != null) return;
            var file = JsonStore.Load<AchievementFile>(File);
            local = StatBook.From(file.local);
            pending = StatBook.From(file.pending);
            partPacks.Clear();
            foreach (var entry in file.partPacks)
            {
                if (entry?.packs == null || string.IsNullOrEmpty(entry.achievement)) continue;
                if (!partPacks.TryGetValue(entry.achievement, out var packs)) partPacks[entry.achievement] = packs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in entry.packs)
                    if (!string.IsNullOrEmpty(p)) packs.Add(p);
            }
            Application.quitting -= SaveIfDirty;
            Application.quitting += SaveIfDirty;
        }

        static void Save()
        {
            dirty = false;
            var file = new AchievementFile { local = local.ToData(), pending = pending.ToData() };
            foreach (var (id, packs) in partPacks) file.partPacks.Add(new PartPacks { achievement = id, packs = new List<string>(packs) });
            JsonStore.Save(File, file);
        }

        static void SaveIfDirty()
        {
            if (dirty && local != null) Save();
        }

        // Writes the file and syncs the account once nothing changed for a moment and no race runs.
        static void ScheduleCommit()
        {
            commitAt = Time.realtimeSinceStartup + CommitDelaySeconds;
            if (!committing) CommitWhenQuiet().LogExceptionsAndForget();
        }

        static async Awaitable CommitWhenQuiet()
        {
            committing = true;
            try
            {
                while (Time.realtimeSinceStartup < commitAt || RaceRunning) await Awaitable.NextFrameAsync(Application.exitCancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Play mode ended or the game quits: Application.quitting saved.
                return;
            }
            finally
            {
                committing = false;
            }
            SaveIfDirty();
            if (!pending.IsEmpty) OnlineAchievements.Sync();
        }

        static bool RaceRunning
        {
            get
            {
                var race = RaceManager.Current;
                return race != null && !race.IsAttract && !race.LoadFailed && race.Phase != RacePhase.Results;
            }
        }
    }
}
