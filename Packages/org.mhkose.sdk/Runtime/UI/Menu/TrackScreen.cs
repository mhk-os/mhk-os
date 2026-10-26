using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Track browser (Track.uxml): a card per track and the info panel of the hovered or focused one (laps or arena,
    /// records). Battles list arenas first, the other modes circuits only. Time trials open the ghost picker, the other
    /// modes the race settings. For the online lobby it lists the lobby's tracks (arenas in battle lobbies): a Random
    /// card first and the remaining time while voting (<see cref="LobbyPick.Vote"/>), the next race's track for the room
    /// owner (<see cref="LobbyPick.OwnerPick"/>).
    /// </summary>
    public class TrackScreen : CardGridScreen<TrackDefinition>
    {
        [SerializeField, Tooltip("Header title in battles (the UXML title is used for the other modes).")]
        string battleTitle = "Choose an arena";
        [SerializeField, Tooltip("Tooltip of the swords badge on arena cards in battles.")]
        string arenaTip = "Arena, built for battles.";
        [SerializeField, Tooltip("Appended to the game name when the track has a variant. {0} = variant.")]
        string variantFormat = " | {0}";
        [SerializeField, Tooltip("Length fact for one lap. {0} = laps.")]
        string lapFormat = "{0} lap";
        [SerializeField, Tooltip("Length fact for several laps. {0} = laps.")]
        string lapsFormat = "{0} laps";
        [SerializeField, Tooltip("A record. {0} = time, {1} = driver.")]
        string recordFormat = "{0} | {1}";
        [SerializeField, Tooltip("Shown instead of a record time when there is none.")]
        string noRecord = "—";
        [SerializeField, Tooltip("Header title while voting in an online lobby. {0} = seconds left.")]
        string voteTitleFormat = "Vote for the next track: {0}";
        [SerializeField, Tooltip("Header title when the room owner picks the next track.")]
        string ownerPickTitle = "Choose the next track";
        [SerializeField, Tooltip("Title of the Random card while voting.")]
        string randomTitle = "Random";
        [SerializeField, Tooltip("Tooltip of the Random card.")]
        string randomTip = "A random track of the lobby: whoever's vote is drawn decides.";

        List<TrackDefinition> tracks;
        bool battle;
        VisualElement arenaLine, lapsLine;
        Label lapsText, bestLap, bestRace;
        IVisualElementScheduledItem voteClock;

        void Reset()
        {
            showcaseVisible = false;
            cardStep = 0.014f;
        }

        protected override IEnumerable<TrackDefinition> Items => tracks;

        LobbyPick Lobby => Selection.lobbyPick is LobbyPick.Vote or LobbyPick.OwnerPick ? Selection.lobbyPick : LobbyPick.None;

        protected override void BindPage()
        {
            var lobby = Lobby;
            battle = lobby != LobbyPick.None ? LobbyScreen.IsBattle : Selection.mode == GameMode.Battle;
            if (lobby == LobbyPick.OwnerPick) Q<Label>("title").text = ownerPickTitle;
            else if (battle) Q<Label>("title").text = battleTitle;
            // Arenas only host battles, but battles may also be fought on any circuit (arenas listed first). Online
            // battle lobbies use arenas only, race lobbies circuits only.
            tracks = lobby != LobbyPick.None
                ? LobbyScreen.LobbyContent<TrackDefinition>().Where(t => t.layout == (battle ? TrackLayout.Arena : TrackLayout.Circuit)).ToList()
                : battle
                    ? Content.All<TrackDefinition>().OrderBy(t => t.layout == TrackLayout.Arena ? 0 : 1).ToList()
                    : Content.All<TrackDefinition>().Where(t => t.layout == TrackLayout.Circuit).ToList();
            arenaLine = Q<VisualElement>("info-arena");
            lapsLine = Q<VisualElement>("info-laps");
            lapsText = Q<Label>("info-laps-text");
            bestLap = Q<Label>("info-best-lap");
            bestRace = Q<Label>("info-best-race");
            if (lobby == LobbyPick.None) return;
            Online.OnlineLobby.Changed += OnLobbyChanged;
            if (lobby == LobbyPick.Vote) voteClock = Root.schedule.Execute(ShowVoteTime).Every(250);
            ShowVoteTime();
        }

        protected override void Unbind()
        {
            base.Unbind();
            Online.OnlineLobby.Changed -= OnLobbyChanged;
            voteClock?.Pause();
            voteClock = null;
        }

        void ShowVoteTime()
        {
            if (Lobby != LobbyPick.Vote || Root == null) return;
            Q<Label>("title").text = string.Format(voteTitleFormat, Mathf.CeilToInt(Online.OnlineLobby.PhaseSecondsLeft));
        }

        // Voting ended (or the lobby is gone): back to the lobby, which shows what was drawn.
        void OnLobbyChanged()
        {
            var lobby = Online.OnlineLobby.Current;
            var over = lobby == null || (Lobby == LobbyPick.Vote && lobby.Phase != Online.LobbyPhase.Voting)
                       || (Lobby == LobbyPick.OwnerPick && lobby.Phase != Online.LobbyPhase.Waiting);
            if (over && Flow.Current == this) LobbyScreen.Return(Flow);
        }

        // Voting: the Random card leads the grid.
        protected override void OnGridCleared()
        {
            if (Lobby != LobbyPick.Vote) return;
            var card = UiTree.Spawn<Button>(cardTemplate, Q<VisualElement>("card-grid"));
            if (card == null) return;
            card.SetText("card-title", randomTitle);
            card.Q<Label>("card-tag")?.SetVisible(false);
            card.tooltip = randomTip;
            card.Q("card-badge")?.SetVisible(false);
            var image = card.Q("card-image");
            image?.SetImage(null);
            if (image != null)
            {
                var icon = new VisualElement();
                icon.AddToClassList("icon");
                icon.AddToClassList("i-shuffle");
                icon.AddToClassList("random-card-icon");
                image.Add(icon);
            }
            card.clicked += () =>
            {
                Click();
                LobbyScreen.Vote(Flow, "");
            };
        }

        protected override string CardTag(TrackDefinition t) => UiText.GameName(t.SourceGame);

        protected override Texture2D CardImage(TrackDefinition t) => t.Picture;

        protected override string InfoGame(TrackDefinition t) => UiText.GameWithVariant(t, variantFormat);

        // Arenas get the swords badge (the gold won-cup badge of cup cards) with a hover tooltip.
        protected override void OnCardSpawned(Button card, TrackDefinition t)
        {
            var badge = card.Q("card-badge");
            if (badge == null) return;
            var arena = battle && t.layout == TrackLayout.Arena;
            badge.SetVisible(arena);
            if (!arena) return;
            badge.pickingMode = PickingMode.Position;
            badge.tooltip = arenaTip;
            var icon = badge.Q(className: "icon");
            icon?.RemoveFromClassList("i-emoji_events");
            icon?.AddToClassList("i-swords");
        }

        protected override void Describe(TrackDefinition t)
        {
            var arena = t.layout == TrackLayout.Arena;
            arenaLine.SetVisible(arena);
            lapsLine.SetVisible(!arena);
            if (!arena) lapsText.text = string.Format(t.defaultLaps == 1 ? lapFormat : lapsFormat, t.defaultLaps);
            var rec = GameRoot.Instance.Save.records.Find(r => r.trackId == t.Id);
            bestLap.text = rec != null ? Record(rec.bestLap, rec.bestLapCharacter) : noRecord;
            bestRace.text = rec != null ? Record(rec.bestRace, rec.bestRaceCharacter) : noRecord;
        }

        string Record(float time, string characterId) =>
            time > 0 ? string.Format(recordFormat, UiText.Clock(time), DriverName(characterId)) : noRecord;

        static string DriverName(string characterId)
        {
            var character = Content.Get<CharacterDefinition>(characterId);
            return character != null ? character.DisplayName : characterId;
        }

        protected override void Pick(TrackDefinition track)
        {
            switch (Lobby)
            {
                case LobbyPick.Vote:
                    LobbyScreen.Vote(Flow, track.Id);
                    return;
                case LobbyPick.OwnerPick:
                    LobbyScreen.StartOn(Flow, track.Id);
                    return;
                default:
                    Continue(track);
                    return;
            }
        }

        /// <summary>
        /// What follows the track choice for <paramref name="track"/>: the ghost picker in time trials, else the race
        /// settings. The kart page calls it directly when the track was fixed beforehand (<see cref="MenuSelection.trackFixed"/>).
        /// </summary>
        public void Continue(TrackDefinition track)
        {
            var selection = Selection;
            selection.track = track;
            if (selection.mode == GameMode.TimeTrial)
            {
                Flow.Get<GhostScreen>().Open(track);
                return;
            }
            Flow.Get<RaceSettingsScreen>().Open(() => SceneFlow.StartSession(selection.BuildSession(new[] { track }, selection.opponents)),
                track.DisplayName, track.Picture);
        }
    }
}
