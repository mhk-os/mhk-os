using System;
using System.Collections.Generic;

namespace MHK.Modding
{
    /// <summary>
    /// Mod load order, first = highest priority (its definitions replace those with the same id of the mods below). A
    /// saved order (UserSettings.modOrder) keeps its mods' places, mods it does not list go by <see cref="ContentPack.loadOrder"/>.
    /// </summary>
    public static class ModOrder
    {
        /// <summary><paramref name="packs"/> in priority order following <paramref name="saved"/> (pack ids, first = highest).</summary>
        public static List<ContentPack> Sort(IReadOnlyList<ContentPack> packs, IReadOnlyList<string> saved)
        {
            var result = new List<ContentPack>(packs.Count);
            if (saved != null)
                foreach (var id in saved)
                {
                    var pack = Find(packs, id);
                    if (pack != null && !result.Contains(pack)) result.Add(pack);
                }
            var unlisted = new List<ContentPack>();
            foreach (var p in packs)
                if (!result.Contains(p)) unlisted.Add(p);
            unlisted.Sort(ByDefaultOrder);
            foreach (var p in unlisted) result.Insert(InsertIndex(result, p), p);
            return result;
        }

        /// <summary>
        /// The order to save after the player moved an entry: the shown ids <paramref name="shownIds"/> (already reordered),
        /// then the ids of <paramref name="saved"/> not shown now (mods or asset packs not installed keep a place for when
        /// they come back). Used for mods and asset packs alike.
        /// </summary>
        public static List<string> ToSave(IReadOnlyList<string> shownIds, IReadOnlyList<string> saved)
        {
            var ids = new List<string>(shownIds);
            if (saved != null)
                foreach (var id in saved)
                    if (!Contains(ids, id)) ids.Add(id);
            return ids;
        }

        static bool Contains(List<string> ids, string id)
        {
            foreach (var i in ids)
                if (string.Equals(i, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Higher loadOrder first, the pack id breaks ties so the order never depends on file names.
        static int ByDefaultOrder(ContentPack a, ContentPack b)
        {
            var c = b.loadOrder.CompareTo(a.loadOrder);
            return c != 0 ? c : string.CompareOrdinal(a.packId, b.packId);
        }

        static int InsertIndex(List<ContentPack> list, ContentPack pack)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].loadOrder < pack.loadOrder) return i;
            return list.Count;
        }

        static ContentPack Find(IReadOnlyList<ContentPack> packs, string id)
        {
            foreach (var p in packs)
                if (string.Equals(p.packId, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }
}
