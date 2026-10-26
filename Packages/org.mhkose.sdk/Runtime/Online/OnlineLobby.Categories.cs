using System.Collections.Generic;

namespace MHK.Online
{
    /// <summary>A public lobby category: mode, packs and anticheat (docs/protocol.md, public lobby categories).</summary>
    public sealed class LobbyCategory
    {
        public readonly int id;
        public readonly RoomMode mode;
        public readonly string[] packs;
        public readonly bool anticheat;

        public LobbyCategory(int id, RoomMode mode, bool anticheat, params string[] packs)
        {
            this.id = id;
            this.mode = mode;
            this.anticheat = anticheat;
            this.packs = packs;
        }

        public bool Battle => mode == RoomMode.BattleSet;
    }

    /// <summary>The ten public lobby categories the server always offers; public lobbies race at 150cc with track laps and default item odds.</summary>
    public static class LobbyCategories
    {
        static readonly string[] Base = { "mhk2", "mhk3", "mhke", "mhkt" };
        static readonly string[] WithTincan = { "mhk2", "mhk3", "mhke", "mhkt", "tincan" };
        static readonly string[] Classic = { "mhk2" };

        /// <summary>In category order: races, battles, the classic races.</summary>
        public static readonly IReadOnlyList<LobbyCategory> All = new[]
        {
            new LobbyCategory(1, RoomMode.RaceSet, true, Base),
            new LobbyCategory(2, RoomMode.RaceSet, false, Base),
            new LobbyCategory(3, RoomMode.RaceSet, true, WithTincan),
            new LobbyCategory(4, RoomMode.RaceSet, false, WithTincan),
            new LobbyCategory(5, RoomMode.BattleSet, true, Base),
            new LobbyCategory(6, RoomMode.BattleSet, false, Base),
            new LobbyCategory(7, RoomMode.BattleSet, true, WithTincan),
            new LobbyCategory(8, RoomMode.BattleSet, false, WithTincan),
            new LobbyCategory(9, RoomMode.RaceSet, true, Classic),
            new LobbyCategory(10, RoomMode.RaceSet, false, Classic),
        };

        public static bool TryGet(int id, out LobbyCategory category)
        {
            category = id >= 1 && id <= All.Count ? All[id - 1] : null;
            return category != null;
        }
    }
}
