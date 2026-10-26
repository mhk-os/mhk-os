using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;

namespace MHK.UI
{
    /// <summary>What the card pages pick for the online lobby (<see cref="LobbyScreen"/>), None in the offline modes.</summary>
    public enum LobbyPick
    {
        None,
        /// <summary>Driver, then kart: the player's loadout (lobby content only).</summary>
        Loadout,
        /// <summary>A track or arena of the lobby (or Random) for the running vote.</summary>
        Vote,
        /// <summary>The room owner's track for the next race.</summary>
        OwnerPick,
        /// <summary>A track (or Random) for a race against AI while the player is alone in the lobby.</summary>
        Solo,
    }

    /// <summary>The player's choices while walking through the menu, and the race session built from them.</summary>
    public sealed class MenuSelection
    {
        public GameMode mode;
        public CharacterDefinition character;
        public KartDefinition kart;
        /// <summary>Source-game filter of the card browsers (null = all).</summary>
        public string gameFilter;
        public Difficulty difficulty = Difficulty.Medium;
        public int opponents = 5;
        public int laps = 3;
        public bool useTrackLaps = true;
        /// <summary>Single race or battle: the picked track (its item family sets the default item odds).</summary>
        public TrackDefinition track;
        /// <summary>
        /// <see cref="track"/> was chosen before the driver (a time trial from the track's records page): the kart page goes
        /// straight on to what follows the track choice. Cleared when the player backs out of the driver page.
        /// </summary>
        public bool trackFixed;
        /// <summary>Time trial: race the personal best of the track and class (picked on the records page, or nothing picked).</summary>
        public bool raceOwnBest = true;
        /// <summary>Time trial: the other ghosts picked on the records page (stored or downloaded), in pick order.</summary>
        public readonly List<string> ghostIds = new();
        /// <summary>
        /// The card pages work for the online lobby: they list the lobby's content only and hand the choice to the lobby
        /// instead of starting a race. Set by the lobby screen, cleared by the offline modes.
        /// </summary>
        public LobbyPick lobbyPick;

        public string ModeName => mode switch
        {
            GameMode.Championship => "Championship",
            GameMode.TimeTrial => "Time Trial",
            GameMode.Battle => "Battle",
            _ => "Single Race",
        };

        /// <summary>
        /// <paramref name="c"/>'s default kart while a loaded pack registers it, else null (the base pack with the
        /// usual default karts may be disabled while a mod's drivers still point at them).
        /// </summary>
        public static KartDefinition LoadedDefaultKart(CharacterDefinition c)
        {
            if (c == null || c.defaultKart == null) return null;
            return GameRoot.Instance.Content.Get<KartDefinition>(c.defaultKart.Id) == c.defaultKart ? c.defaultKart : null;
        }

        /// <summary>
        /// Session for <paramref name="tracks"/>: the player plus <paramref name="opponentCount"/> computer drivers. Single
        /// races and battles get the player's item odds for the first track (<see cref="ItemOdds.PlayerOdds"/>).
        /// </summary>
        public GameSession BuildSession(IEnumerable<TrackDefinition> tracks, int opponentCount)
        {
            var session = new GameSession
            {
                mode = mode,
                difficulty = difficulty,
                laps = laps,
                lapsFromTrack = useTrackLaps || mode != GameMode.SingleRace,
            };
            session.trackIds.AddRange(tracks.Select(t => t.Id));
            if (mode == GameMode.SingleRace || mode == GameMode.Battle) ApplyItemOdds(session);
            session.racers.Add(PlayerSetup());
            AddOpponents(session, opponentCount);
            session.ResetStandings();
            return session;
        }

        /// <summary>A time trial on <paramref name="track"/> against the ghosts picked on its records page.</summary>
        public GameSession BuildTimeTrialSession(TrackDefinition track)
        {
            var session = BuildSession(new[] { track }, 0);
            session.powerupsEnabled = false;
            session.raceOwnBest = raceOwnBest;
            session.ghostIds.AddRange(ghostIds);
            return session;
        }

        // Only the player's own odds go into the session: without them the race uses the defaults of the track's family.
        static void ApplyItemOdds(GameSession session)
        {
            var root = GameRoot.Instance;
            var first = root.Content.Get<TrackDefinition>(session.CurrentTrackId);
            var own = ItemOdds.PlayerOdds(root.Content, root.Settings, first, session.powerupOdds);
            session.powerupsEnabled = ItemOdds.Total(session.powerupOdds) > 0f;
            if (own == null) session.powerupOdds.Clear();
        }

        RacerSetup PlayerSetup() => new()
        {
            characterId = character.Id,
            kartId = KartId(kart != null ? kart : LoadedDefaultKart(character)),
            isHuman = true,
            playerIndex = 0,
            displayName = character.DisplayName,
        };

        // Random drivers of every loaded pack: core games and mods mix on every track.
        void AddOpponents(GameSession session, int count)
        {
            var pool = GameRoot.Instance.Content.All<CharacterDefinition>().Where(c => c != character)
                .OrderBy(_ => UnityEngine.Random.value).ToList();
            for (var i = 0; i < count && pool.Count > 0; i++)
            {
                var c = pool[i % pool.Count];
                session.racers.Add(new RacerSetup
                {
                    characterId = c.Id,
                    kartId = KartId(LoadedDefaultKart(c)),
                    isHuman = false,
                    displayName = c.DisplayName,
                });
            }
        }

        static string KartId(KartDefinition k) => k != null ? k.Id : null;
    }
}
