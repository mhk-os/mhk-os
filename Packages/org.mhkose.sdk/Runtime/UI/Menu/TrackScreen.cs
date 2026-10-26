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
    /// records). Battles list arenas first, the other modes circuits only. A time trial (track fixed on its records page)
    /// starts at once, the other modes open the race settings. For the online lobby it lists the lobby's tracks (arenas
    /// in battle lobbies): a Random card first and the remaining time while voting (<see cref="LobbyPick.Vote"/>), the
    /// next race's track for the room owner (<see cref="LobbyPick.OwnerPick"/>).
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
        [SerializeField, Tooltip("Header title while voting in an online lobby (the countdown is a badge beside it).")]
        string voteTitle = "Vote for the next track";
        [SerializeField, Tooltip("Header title when the room owner picks the next track.")]
        string ownerPickTitle = "Choose the next track";
        [SerializeField, Tooltip("Header title when the player picks the track of a race against AI.")]
        string soloTitle = "Race AI: choose a track";
        [SerializeField, Tooltip("Title of the Random card in the lobby modes.")]
        string randomTitle = "Random";
        [SerializeField, Tooltip("Tooltip of the Random card while voting.")]
        string randomTip = "A random track of the lobby: whoever's vote is drawn decides.";
        [SerializeField, Tooltip("Tooltip of the Random card outside voting.")]
        string randomPickTip = "A random track of the lobby";

        List<TrackDefinition> tracks;
        bool battle;
        VisualElement arenaLine, lapsLine;
        Label lapsText, bestLap, bestRace;
        IVisualElementScheduledItem voteClock;
        Label voteTimer;
        // While voting: the vote badge of each card by track id ("" = Random).
        readonly Dictionary<string, VisualElement> voteBadges = new();

        void Reset()
        {
            showcaseVisible = false;
            cardStep = 0.014f;
        }

        protected override IEnumerable<TrackDefinition> Items => tracks;

        LobbyPick Lobby => Selection.lobbyPick is LobbyPick.Vote or LobbyPick.OwnerPick or LobbyPick.Solo ? Selection.lobbyPick : LobbyPick.None;

        protected override void BindPage()
        {
            var lobby = Lobby;
            battle = lobby != LobbyPick.None ? LobbyScreen.IsBattle : Selection.mode == GameMode.Battle;
            if (lobby == LobbyPick.Vote) AddVoteTimer();
            else if (lobby == LobbyPick.OwnerPick) Q<Label>("title").text = ownerPickTitle;
            else if (lobby == LobbyPick.Solo) Q<Label>("title").text = soloTitle;
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
            // The cards (and their vote badges) are made after BindPage.
            Root.schedule.Execute(ShowVotes);
        }

        protected override void Unbind()
        {
            base.Unbind();
            Online.OnlineLobby.Changed -= OnLobbyChanged;
            voteClock?.Pause();
            voteClock = null;
            voteTimer = null;
            voteBadges.Clear();
        }

        // The title and, beside it, a timer badge with the seconds left (the header becomes a row).
        void AddVoteTimer()
        {
            var title = Q<Label>("title");
            title.text = voteTitle;
            var header = title.parent;
            header.AddToClassList("vote-header");
            var badge = new VisualElement { tooltip = "Voting ends" };
            badge.AddToClassList("vote-timer");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("icon");
            icon.AddToClassList("i-timer");
            badge.Add(icon);
            voteTimer = new Label { pickingMode = PickingMode.Ignore };
            voteTimer.AddToClassList("vote-timer-label");
            badge.Add(voteTimer);
            header.Add(badge);
        }

        void ShowVoteTime()
        {
            if (Lobby != LobbyPick.Vote || voteTimer == null) return;
            voteTimer.text = Mathf.CeilToInt(Online.OnlineLobby.PhaseSecondsLeft).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Voting ended (or the lobby is gone): back to the lobby, which shows what was drawn. New votes update the cards.
        void OnLobbyChanged()
        {
            var lobby = Online.OnlineLobby.Current;
            var over = lobby == null || (Lobby == LobbyPick.Vote && lobby.Phase != Online.LobbyPhase.Voting)
                       || (Lobby is LobbyPick.OwnerPick or LobbyPick.Solo && lobby.Phase != Online.LobbyPhase.Waiting)
                       || (Lobby == LobbyPick.Solo && !LobbyScreen.CanRaceAlone);
            if (over && Flow.Current == this)
            {
                LobbyScreen.Return(Flow);
                return;
            }
            ShowVotes();
        }

        // Votes per card: their count, voters' names as tooltip.
        void ShowVotes()
        {
            var lobby = Online.OnlineLobby.Current;
            if (Lobby != LobbyPick.Vote || lobby == null) return;
            foreach (var pair in voteBadges)
            {
                var names = new List<string>();
                foreach (var v in lobby.votes)
                {
                    if (!string.Equals(v.trackId, pair.Key, System.StringComparison.OrdinalIgnoreCase)) continue;
                    var m = lobby.Member(v.accountId);
                    names.Add(m != null ? m.username : "?");
                }
                pair.Value.Clear();
                pair.Value.SetVisible(names.Count > 0);
                if (names.Count > 0)
                    InfoChips.Add(pair.Value, "how_to_vote", names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), string.Join("\n", names), InfoChipState.On)
                        .AddToClassList("big");
            }
        }

        VisualElement AddVoteBadge(Button card, string trackId)
        {
            var badge = new VisualElement { pickingMode = PickingMode.Position };
            badge.AddToClassList("info-chips");
            badge.AddToClassList("vote-badge");
            badge.SetVisible(false);
            card.Add(badge);
            voteBadges[trackId] = badge;
            return badge;
        }

        // In the lobby modes the Random card leads the grid (the room owner of a championship picks the cup's order: none).
        protected override void OnGridCleared()
        {
            voteBadges.Clear();
            var lobby = Lobby;
            if (lobby is not (LobbyPick.Vote or LobbyPick.Solo or LobbyPick.OwnerPick)) return;
            var card = UiTree.Spawn<Button>(cardTemplate, Q<VisualElement>("card-grid"));
            if (card == null) return;
            card.SetText("card-title", randomTitle);
            card.Q<Label>("card-tag")?.SetVisible(false);
            card.tooltip = lobby == LobbyPick.Vote ? randomTip : randomPickTip;
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
            if (lobby == LobbyPick.Vote) AddVoteBadge(card, "");
            card.clicked += () =>
            {
                Click();
                Pick("");
            };
        }

        protected override string GameName(string game) => Lobby != LobbyPick.None ? OnlineUi.PackName(game) : UiText.GameName(game);

        protected override string CardTag(TrackDefinition t) => GameName(t.SourceGame);

        protected override Texture2D CardImage(TrackDefinition t) => t.Picture;

        protected override string InfoGame(TrackDefinition t) =>
            string.IsNullOrEmpty(t.variant) ? GameName(t.SourceGame) : GameName(t.SourceGame) + string.Format(variantFormat, t.variant);

        // Arenas get the swords badge (the gold won-cup badge of cup cards) with a hover tooltip.
        protected override void OnCardSpawned(Button card, TrackDefinition t)
        {
            if (Lobby == LobbyPick.Vote) AddVoteBadge(card, t.Id);
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
            if (Lobby == LobbyPick.None) Continue(track);
            else Pick(track.Id);
        }

        // The lobby modes: a vote, the owner's next track or a race against AI ("" = random).
        void Pick(string trackId)
        {
            switch (Lobby)
            {
                case LobbyPick.Vote:
                    LobbyScreen.Vote(Flow, trackId);
                    return;
                case LobbyPick.OwnerPick:
                case LobbyPick.Solo:
                    LobbyScreen.StartOn(Flow, trackId);
                    return;
            }
        }

        /// <summary>
        /// What follows the track choice for <paramref name="track"/>: a time trial starts at once against the ghosts
        /// picked on the track's records page, the other modes open the race settings. The kart page calls it directly when
        /// the track was fixed beforehand (<see cref="MenuSelection.trackFixed"/>).
        /// </summary>
        public void Continue(TrackDefinition track)
        {
            var selection = Selection;
            selection.track = track;
            if (selection.mode == GameMode.TimeTrial)
            {
                if (SceneFlow.Busy) return;
                selection.trackFixed = false;
                SceneFlow.StartSession(selection.BuildTimeTrialSession(track));
                return;
            }
            Flow.Get<RaceSettingsScreen>().Open(() => SceneFlow.StartSession(selection.BuildSession(new[] { track }, selection.opponents)),
                track.DisplayName, track.Picture);
        }
    }
}
