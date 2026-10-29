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
    }
}
