using System.Collections.Generic;

namespace MHK.Online
{
    /// <summary>A public lobby category: mode, packs, anticheat and AI (docs/protocol.md, public lobby categories).</summary>
    public sealed class LobbyCategory
    {
        public readonly int id;
        public readonly RoomMode mode;
        public readonly string[] packs;
        public readonly bool anticheat;
        /// <summary>Computer drivers race in every place no human takes.</summary>
        public readonly bool ai;

        public LobbyCategory(int id, RoomMode mode, bool anticheat, bool ai, params string[] packs)
        {
            this.id = id;
            this.mode = mode;
            this.anticheat = anticheat;
            this.ai = ai;
            this.packs = packs;
        }

        public bool Battle => mode == RoomMode.BattleSet;
    }

    /// <summary>
    /// The twenty public lobby categories the server always offers: ten without AI and their AI versions (category + 10).
    /// Public lobbies race at 150cc with track laps and default item odds.
    /// </summary>
    public static class LobbyCategories
    {
        /// <summary>Category number of a category's AI version: its own number plus this.</summary>
        public const int AiOffset = 10;

        static readonly string[] Base = { "mhk2", "mhk3", "mhke", "mhkt" };
        static readonly string[] WithTincan = { "mhk2", "mhk3", "mhke", "mhkt", "tincan" };
        static readonly string[] Classic = { "mhk2" };

        static readonly (RoomMode mode, bool anticheat, string[] packs)[] Kinds =
        {
            (RoomMode.RaceSet, true, Base),
            (RoomMode.RaceSet, false, Base),
            (RoomMode.RaceSet, true, WithTincan),
            (RoomMode.RaceSet, false, WithTincan),
            (RoomMode.BattleSet, true, Base),
            (RoomMode.BattleSet, false, Base),
            (RoomMode.BattleSet, true, WithTincan),
            (RoomMode.BattleSet, false, WithTincan),
            (RoomMode.RaceSet, true, Classic),
            (RoomMode.RaceSet, false, Classic),
        };

        /// <summary>In category order: races, battles, the classic races; then the same with AI.</summary>
        public static readonly IReadOnlyList<LobbyCategory> All = Build();

        static LobbyCategory[] Build()
        {
            var all = new LobbyCategory[Kinds.Length * 2];
            for (var i = 0; i < Kinds.Length; i++)
            {
                var (mode, anticheat, packs) = Kinds[i];
                all[i] = new LobbyCategory(i + 1, mode, anticheat, false, packs);
                all[i + AiOffset] = new LobbyCategory(i + 1 + AiOffset, mode, anticheat, true, packs);
            }
            return all;
        }

        public static bool TryGet(int id, out LobbyCategory category)
        {
            category = id >= 1 && id <= All.Count ? All[id - 1] : null;
            return category != null;
        }
    }
}
