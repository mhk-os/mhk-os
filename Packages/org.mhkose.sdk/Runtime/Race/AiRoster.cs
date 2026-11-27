using System.Collections.Generic;
using MHK.Modding;
using MHK.Online;

namespace MHK.Race
{
    /// <summary>
    /// Loadouts of computer drivers: any driver on any kart of any loaded pack (online: of the lobby's packs). Lists keep
    /// the registry's order, so clients with the same content draw the same loadouts from the same seed.
    /// </summary>
    public static class AiRoster
    {
        /// <summary>Every listed kart, or only the lobby content of <paramref name="lobbyPacks"/> when it is given.</summary>
        public static List<KartDefinition> Karts(ContentRegistry content, IReadOnlyList<string> lobbyPacks = null) =>
            lobbyPacks != null ? ContentManifests.LobbyContent<KartDefinition>(content, lobbyPacks) : new List<KartDefinition>(content.All<KartDefinition>());

        /// <summary>Every listed driver, or only the lobby content of <paramref name="lobbyPacks"/> when it is given.</summary>
        public static List<CharacterDefinition> Characters(ContentRegistry content, IReadOnlyList<string> lobbyPacks = null) =>
            lobbyPacks != null ? ContentManifests.LobbyContent<CharacterDefinition>(content, lobbyPacks) : new List<CharacterDefinition>(content.All<CharacterDefinition>());

        /// <summary>A random kart of <paramref name="karts"/> drawn from <paramref name="rng"/>, null when there is none.</summary>
        public static KartDefinition RandomKart(IReadOnlyList<KartDefinition> karts, System.Random rng) =>
            karts == null || karts.Count == 0 ? null : karts[rng.Next(karts.Count)];

        /// <summary>A random kart of <paramref name="karts"/> drawn from Unity's random state, null when there is none.</summary>
        public static KartDefinition RandomKart(IReadOnlyList<KartDefinition> karts) =>
            karts == null || karts.Count == 0 ? null : karts[UnityEngine.Random.Range(0, karts.Count)];

        /// <summary>
        /// The kart of computer driver <paramref name="c"/>. A driver that races without a kart keeps its default (the No
        /// kart), the others get a random kart of <paramref name="karts"/> drawn from <paramref name="rng"/>.
        /// </summary>
        public static KartDefinition KartFor(ContentRegistry content, CharacterDefinition c, IReadOnlyList<KartDefinition> karts, System.Random rng) =>
            c != null && c.racesWithoutKart ? content.DefaultKart(c) : RandomKart(karts, rng);

        /// <summary><see cref="KartFor(ContentRegistry, CharacterDefinition, IReadOnlyList{KartDefinition}, System.Random)"/> drawing from Unity's random state.</summary>
        public static KartDefinition KartFor(ContentRegistry content, CharacterDefinition c, IReadOnlyList<KartDefinition> karts) =>
            c != null && c.racesWithoutKart ? content.DefaultKart(c) : RandomKart(karts);
    }
}
