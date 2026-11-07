using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;

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
        /// <summary>The room owner's track for the next race, or between sets of an owner-pick race room a cup (the Cup page) as the next set.</summary>
        OwnerPick,
        /// <summary>A track (or Random) for a race against AI while the player is alone in the lobby.</summary>
        Solo,
    }

    /// <summary>The player's choices while walking through the menu, and the race session built from them.</summary>
    public sealed class MenuSelection
    {
        /// <summary>Engine class time trials start in while no personal best picks one (the hub, a track's page): 150cc.</summary>
        public const Difficulty TimeTrialClass = Difficulty.Hard;

        public GameMode mode;
        public CharacterDefinition character;
        public KartDefinition kart;
        /// <summary>Source-game filter of the card browsers (null = all).</summary>
        public string gameFilter;
        public Difficulty difficulty = Difficulty.Medium;
        /// <summary>Computer opponents of a single race or battle.</summary>
        public int opponents = GameSession.DefaultOpponents;
        /// <summary>Computer opponents of a championship, below 0 = <see cref="DefaultChampionshipOpponents"/>.</summary>
        public int championshipOpponents = -1;
        public int laps = 3;
        public bool useTrackLaps = true;
        /// <summary>Single race, battle or time trial: the picked track. Picking another one resets <see cref="variantKey"/>.</summary>
        public TrackDefinition track
        {
            get => pickedTrack;
            set
            {
                if (value != pickedTrack) variantKey = "";
                pickedTrack = value;
            }
        }
        TrackDefinition pickedTrack;
        /// <summary>
        /// Single race, battle and time trial: key of the picked variant of <see cref="track"/>
        /// (<see cref="TrackVariant.Key"/>, "" = the track's default). Championships pick theirs per race (<see cref="BuildSession"/>).
        /// </summary>
        public string variantKey = "";
        /// <summary>Time trial: laps to race, 1 for a single lap, 0 for the track's default (<see cref="TrackDefinition.defaultLaps"/>).</summary>
        public int timeTrialLaps;
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

        /// <summary>Computer opponents a local race has at most (<see cref="GameConfig.maxRacers"/> minus the player).</summary>
        public static int MaxOpponents => Math.Max(0, GameRoot.Instance.Config.maxRacers - 1);

        /// <summary>Championship opponents until the player sets a number: every loaded driver but the player's, at most <see cref="MaxOpponents"/>.</summary>
        public int DefaultChampionshipOpponents =>
            Math.Min(MaxOpponents, GameRoot.Instance.Content.All<CharacterDefinition>().Count(c => c != character));

        /// <summary>The championship's opponents: the player's number, else <see cref="DefaultChampionshipOpponents"/>.</summary>
        public int ChampionshipOpponents => championshipOpponents >= 0 ? Math.Min(championshipOpponents, MaxOpponents) : DefaultChampionshipOpponents;

        /// <summary>
        /// <paramref name="c"/>'s default kart while a loaded pack registers it, else null (the base pack with the
        /// usual default karts may be disabled while a mod's drivers still point at them).
        /// </summary>
        public static KartDefinition LoadedDefaultKart(CharacterDefinition c)
        {
            if (c == null || c.defaultKart == null) return null;
            return GameRoot.Instance.Content.Get<KartDefinition>(c.defaultKart.Id) == c.defaultKart ? c.defaultKart : null;
        }

        /// <summary>The picked variant of <paramref name="t"/>: <see cref="variantKey"/> when the track offers it, else its default.</summary>
        public TrackVariant VariantOf(TrackDefinition t)
        {
            var set = TrackVariants.Of(t);
            return set?.Resolve(variantKey) ?? set?.Default;
        }

        /// <summary>Laps of a time trial on <paramref name="t"/>: 1 when <see cref="timeTrialLaps"/> asks for a single lap, else the track's default.</summary>
        public int TimeTrialLapsOn(TrackDefinition t) => timeTrialLaps == 1 ? 1 : t.defaultLaps;

        /// <summary>
        /// Session for <paramref name="tracks"/>: the player plus <paramref name="opponentCount"/> computer drivers. Single
        /// races and battles get the player's item odds (<see cref="ItemOdds.PlayerOdds"/>) and the picked variant
        /// (<see cref="VariantOf"/>). Championships race a variant of every track rolled now (one random generator per
        /// championship): without <paramref name="setups"/> (cups) a random one, with them (a custom championship, same order
        /// as the tracks) each race's fixed options and laps (<see cref="CustomRaceSetup"/>, a missing setup = all random).
        /// </summary>
        public GameSession BuildSession(IEnumerable<TrackDefinition> tracks, int opponentCount, IReadOnlyList<CustomRaceSetup> setups = null)
        {
            var list = tracks.ToList();
            var session = new GameSession
            {
                mode = mode,
                difficulty = difficulty,
                laps = laps,
                lapsFromTrack = useTrackLaps || mode != GameMode.SingleRace,
            };
            session.trackIds.AddRange(list.Select(t => t.Id));
            if (mode == GameMode.SingleRace || mode == GameMode.Battle)
            {
                ApplyItemOdds(session);
                session.variants.AddRange(list.Select(t => VariantOf(t)?.Key ?? ""));
            }
            else if (mode == GameMode.Championship) RollChampionship(session, list, setups);
            session.racers.Add(PlayerSetup());
            AddOpponents(session, opponentCount);
            session.ResetStandings();
            return session;
        }

        // Each race's variant (and with setups its laps), rolled with one generator for the whole championship.
        static void RollChampionship(GameSession session, List<TrackDefinition> tracks, IReadOnlyList<CustomRaceSetup> setups)
        {
            var rng = new System.Random();
            for (var i = 0; i < tracks.Count; i++)
            {
                var setup = setups != null && i < setups.Count ? setups[i] : null;
                var variant = setup != null ? setup.Roll(tracks[i], rng) : TrackVariants.Of(tracks[i])?.Random(rng);
                session.variants.Add(variant?.Key ?? "");
                if (setups != null) session.raceLaps.Add(setup != null ? Math.Clamp(setup.laps, 0, CustomRaceSetup.MaxLaps) : 0);
            }
        }

        /// <summary>
        /// A time trial on <paramref name="track"/> (the picked variant and laps, <see cref="TimeTrialLapsOn"/>) against the
        /// ghosts picked on its records page.
        /// </summary>
        public GameSession BuildTimeTrialSession(TrackDefinition track)
        {
            var session = BuildSession(new[] { track }, 0);
            session.powerupsEnabled = false;
            session.variants.Add(VariantOf(track)?.Key ?? "");
            var laps = TimeTrialLapsOn(track);
            session.lapsFromTrack = laps == track.defaultLaps;
            session.laps = laps;
            session.raceOwnBest = raceOwnBest;
            session.ghostIds.AddRange(ghostIds);
            return session;
        }

        // Only the player's own odds go into the session: without them the race uses the default odds.
        static void ApplyItemOdds(GameSession session)
        {
            var root = GameRoot.Instance;
            var own = ItemOdds.PlayerOdds(root.Content, root.Settings, session.powerupOdds);
            session.powerupsEnabled = ItemOdds.Total(session.powerupOdds) > 0f;
            if (!own) session.powerupOdds.Clear();
        }

        RacerSetup PlayerSetup() => new()
        {
            characterId = character.Id,
            kartId = KartId(kart != null ? kart : LoadedDefaultKart(character)),
            isHuman = true,
            playerIndex = 0,
            displayName = character.DisplayName,
        };

        // Random drivers of every loaded pack, each once per round of a new random order (the player's driver only when
        // there is no other), on random karts of every pack: core games and mods mix on every track.
        void AddOpponents(GameSession session, int count)
        {
            var content = GameRoot.Instance.Content;
            var drivers = content.All<CharacterDefinition>().Where(c => c != character).ToList();
            if (drivers.Count == 0) drivers.AddRange(content.All<CharacterDefinition>());
            var karts = AiRoster.Karts(content);
            var round = new List<CharacterDefinition>(drivers.Count);
            for (var i = 0; i < count && drivers.Count > 0; i++)
            {
                if (i % drivers.Count == 0)
                {
                    round.Clear();
                    round.AddRange(drivers.OrderBy(_ => UnityEngine.Random.value));
                }
                var c = round[i % drivers.Count];
                session.racers.Add(new RacerSetup
                {
                    characterId = c.Id,
                    kartId = KartId(AiRoster.RandomKart(karts)),
                    isHuman = false,
                    displayName = c.DisplayName,
                });
            }
        }

        static string KartId(KartDefinition k) => k != null ? k.Id : null;
    }
}
