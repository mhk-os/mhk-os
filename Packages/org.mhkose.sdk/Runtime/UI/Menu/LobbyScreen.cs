using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The online lobby the player is in (Lobby.uxml): name, room code with Copy code / Copy link, the phase with its
    /// countdown, the members (set standings: country, name, driver and kart, ping, points, owner / moderator / ready /
    /// waiting / voted, kick for the room owner and moderators), the player's loadout (the Character and Kart pages limited
    /// to the lobby's content), Ready in private rooms, the vote (the Track page with the lobby's tracks and Random, opened
    /// by itself when voting starts), the owner's Start and Settings, the last race's results and Leave. Races start from
    /// RaceSetup (OnlineRace) and come back here with <see cref="OnlineLobby.ReturnToLobby"/>.
    /// </summary>
    public class LobbyScreen : MenuScreen
    {
        [SerializeField, Tooltip("Member row (Templates/LobbyMemberRow.uxml).")]
        VisualTreeAsset memberTemplate;

        [Header("Texts")]
        [SerializeField] string waitingOwnerText = "Waiting for you to start the next race";
        [SerializeField] string waitingOthersText = "Waiting for the room owner to start";
        [SerializeField] string waitingPublicText = "Waiting for players";
        [SerializeField] string votingText = "Vote for the next track";
        [SerializeField, Tooltip("{0} = track.")] string loadingFormat = "Loading {0}";
        [SerializeField] string countdownText = "Get ready!";
        [SerializeField] string racingText = "Race running";
        [SerializeField] string sittingOutText = "Race running: you race in the next one";
        [SerializeField] string resultsText = "Results";
        [SerializeField] string setResultsText = "Set results";
        [SerializeField, Tooltip("{0} = race in the set, {1} = races in the set.")] string setRaceFormat = "Race {0} of {1}";
        [SerializeField] string anticheatOnText = "Anticheat on", anticheatOffText = "Anticheat off";
        [SerializeField] string privateRoomText = "Private room";
        [SerializeField, Tooltip("{0} = instance number.")] string publicLobbyFormat = "Public lobby #{0}";
        [SerializeField] string noLoadoutText = "No driver chosen";
        [SerializeField] string readyText = "Ready", notReadyText = "Not ready";
        [SerializeField] string voteText = "Vote", changeVoteText = "Change vote";
        [SerializeField] string startVoteText = "Start voting", pickTrackText = "Choose track";
        [SerializeField, Tooltip("Championship start. {0} = next track of the cup.")] string startCupFormat = "Start: {0}";
        [SerializeField] string lastRaceTitle = "LAST RACE", setTitle = "SET RESULTS", noResultsTitle = "";
        [SerializeField] string leaveQuestion = "Leave this lobby?", leaveLabel = "Leave";
        [SerializeField, Tooltip("{0} = player.")] string kickQuestionFormat = "Remove {0} from the lobby? They cannot come back for 10 minutes. Reason (shown to them):";
        [SerializeField] string kickLabel = "Kick";
        [SerializeField, Tooltip("{0} = player.")] string kickedFormat = "{0} was removed.";
        [SerializeField] string codeCopiedText = "Room code copied.", linkCopiedText = "Room link copied: send it to your friends.";
        [SerializeField] string pingUnknown = "-";
        [SerializeField] string dnfText = "DNF";

        const int CountdownMs = 250;

        Label countdown, status;
        VisualElement members;
        IVisualElementScheduledItem tick;
        // Voting round (its end time) the vote page already opened for by itself.
        long autoVoteFor;

        static List<string> NoPacks { get; } = new();

        /// <summary>The current lobby's content of type <typeparamref name="T"/> (empty outside a lobby).</summary>
        public static List<T> LobbyContent<T>() where T : ContentDefinition =>
            ContentManifests.LobbyContent<T>(GameRoot.Instance.Content, OnlineLobby.Current?.settings.packs ?? NoPacks);

        /// <summary>The current lobby fights battles (arenas).</summary>
        public static bool IsBattle => OnlineLobby.Current != null && OnlineLobby.Current.settings.mode == RoomMode.BattleSet;

        /// <summary>The menu as title → Online → lobby, the lobby on top (after a card page, a race or a link).</summary>
        public static void Return(MenuFlow flow)
        {
            flow.Selection.lobbyPick = LobbyPick.None;
            flow.ShowTitle();
            flow.Show<OnlineScreen>();
            if (OnlineLobby.InLobby) flow.Show<LobbyScreen>();
        }

        /// <summary>Kart page in loadout mode: sends the loadout and returns to the lobby.</summary>
        public static void ChooseLoadout(MenuFlow flow, CharacterDefinition character, KartDefinition kart)
        {
            if (character != null && kart != null) Report(flow, OnlineLobby.SetLoadout(character.Id, kart.Id));
            Return(flow);
        }

        /// <summary>Track page while voting: votes (<c>""</c> = Random) and returns to the lobby.</summary>
        public static void Vote(MenuFlow flow, string trackId)
        {
            Report(flow, OnlineLobby.Vote(trackId));
            Return(flow);
        }

        /// <summary>Track page of the room owner: starts the next race there and returns to the lobby.</summary>
        public static void StartOn(MenuFlow flow, string trackId)
        {
            Report(flow, OnlineLobby.StartRace(trackId));
            Return(flow);
        }

        // A refused request shows on the lobby page once it answers.
        static async void Report(MenuFlow flow, Task<OnlineResult> request)
        {
            var r = await request;
            if (r.Ok || flow == null) return;
            var lobby = flow.Get<LobbyScreen>();
            if (lobby != null) lobby.SetStatus(r.message, false);
        }

        protected override void Bind()
        {
            Selection.lobbyPick = LobbyPick.None;
            countdown = Q<Label>("countdown");
            status = Q<Label>("lob-status-label");
            members = Q<VisualElement>("member-list");
            LeaderRows.KeepFocusedRowInView(Q<ScrollView>("member-scroll"), members);
            OnClick("copy-code", CopyCode);
            OnClick("copy-link", CopyLink);
            OnClick("pick-driver", () => OpenLoadout(true));
            OnClick("pick-kart", () => OpenLoadout(false));
            OnClick("ready", ToggleReady);
            OnClick("vote", OpenVote);
            OnClick("start", StartNext);
            OnClick("room-settings", () => Flow.Get<RoomSettingsScreen>().OpenEdit());
            SetStatus(null, false);
            OnlineLobby.Changed += Refresh;
            OnlineLobby.Left += OnLeft;
            tick = Root.schedule.Execute(Tick).Every(CountdownMs);
            Refresh();
            FocusTarget = Q<Button>(OnlineLobby.Me?.HasLoadout ?? false ? "vote" : "pick-driver");
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineLobby.Changed -= Refresh;
            OnlineLobby.Left -= OnLeft;
            tick?.Pause();
            tick = null;
            countdown = status = null;
            members = null;
        }

        protected override void GoBack() =>
            ConfirmPrompt.Show(Q<Button>("back"), leaveQuestion, leaveLabel, () =>
            {
                _ = OnlineLobby.Leave();
                Flow.ShowTitle();
                Flow.Show<OnlineScreen>();
            });

        void OnLeft(LobbyLeftPush left)
        {
            if (Flow.Current != this) return;
            Flow.ShowTitle();
            Flow.Show<OnlineScreen>();
        }

        void SetStatus(string text, bool good)
        {
            if (status == null) return;
            status.text = text ?? "";
            var box = Q<VisualElement>("lob-status");
            box.SetVisible(!string.IsNullOrEmpty(text));
            box.EnableInClassList("good", good);
            Q<VisualElement>("lob-status").Q(className: "icon")?.SetIcon(good ? "check" : "error");
        }

        void Tick()
        {
            var lobby = OnlineLobby.Current;
            if (lobby == null || countdown == null) return;
            var seconds = OnlineLobby.PhaseSecondsLeft;
            countdown.text = lobby.phaseEnds > 0 ? Mathf.CeilToInt(seconds).ToString(CultureInfo.InvariantCulture) : "";
        }

        void Refresh()
        {
            if (members == null) return;
            var lobby = OnlineLobby.Current;
            if (lobby == null) return;
            var me = OnlineLobby.Me;
            ShowHeader(lobby);
            ShowBanner(lobby, me);
            ShowMembers(lobby, me);
            ShowSide(lobby, me);
            ShowResults(lobby);
            Tick();
            AutoOpenVote(lobby, me);
        }

        void ShowHeader(LobbyState lobby)
        {
            Q<Label>("title").text = lobby.summary.name;
            var hasCode = !string.IsNullOrEmpty(lobby.code);
            SetVisible("code-box", hasCode);
            if (hasCode) Q<Label>("code").text = lobby.code;
            Q<Label>("footer-note").text = lobby.IsPrivate ? privateRoomText : string.Format(publicLobbyFormat, lobby.summary.instance);
        }

        void ShowBanner(LobbyState lobby, LobbyMember me)
        {
            var (text, icon) = lobby.Phase switch
            {
                LobbyPhase.Waiting => (lobby.IsPrivate ? OnlineLobby.IsOwner ? waitingOwnerText : waitingOthersText : waitingPublicText, "timer"),
                LobbyPhase.Voting => (votingText, "map"),
                LobbyPhase.Loading => (string.Format(loadingFormat, ContentDisplay.TrackName(lobby.trackId)), "sync"),
                LobbyPhase.Countdown => (countdownText, "counter_3"),
                LobbyPhase.Racing => (me != null && !me.inRace ? sittingOutText : racingText, "sports_score"),
                LobbyPhase.Results => (resultsText, "emoji_events"),
                _ => (setResultsText, "emoji_events"),
            };
            Q<Label>("phase").text = text;
            Q<VisualElement>("phase-icon").SetIcon(icon);
            var parts = new List<string>();
            if (lobby.setRace > 0)
                parts.Add(string.Format(setRaceFormat, lobby.setRace, lobby.setSize));
            if (!string.IsNullOrEmpty(lobby.trackId) && lobby.Phase != LobbyPhase.Loading) parts.Add(ContentDisplay.TrackName(lobby.trackId));
            parts.Add(OnlineScreen.ModeName(lobby.settings));
            parts.Add(EngineClassName(lobby.settings.engineClass));
            parts.Add(lobby.summary.anticheat ? anticheatOnText : anticheatOffText);
            parts.Add(OnlineScreen.PackNames(lobby.settings.packs));
            Q<Label>("phase-sub").text = string.Join(UiText.Separator, parts);
        }

        static string EngineClassName(int engineClass)
        {
            var classes = GameRoot.Instance.Config.difficulties;
            return engineClass >= 0 && engineClass < classes.Length ? classes[engineClass].label : "";
        }

        void ShowMembers(LobbyState lobby, LobbyMember me)
        {
            var focused = members.panel?.focusController?.focusedElement as VisualElement;
            long focusedId = 0;
            if (focused != null)
                foreach (var row in members.Children())
                    if (row.Contains(focused) && row.userData is long id) focusedId = id;
            members.Clear();
            var list = new List<LobbyMember>(lobby.members);
            // Standings: points first, then who joined first.
            list.Sort((a, b) => a.setPoints != b.setPoints ? b.setPoints.CompareTo(a.setPoints) : a.joined.CompareTo(b.joined));
            var i = 0;
            VisualElement refocus = null;
            foreach (var m in list)
            {
                var row = SpawnMember(lobby, m, me, i++ % 2 == 1);
                if (row == null) break;
                if (m.accountId == focusedId) refocus = row.Q<Button>("lb-main");
            }
            FocusLater(refocus);
        }

        VisualElement SpawnMember(LobbyState lobby, LobbyMember m, LobbyMember me, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(memberTemplate, members);
            if (row == null) return null;
            row.userData = m.accountId;
            var mine = me != null && m.accountId == me.accountId;
            var line = row.Q(className: "lb-row") ?? row;
            line.EnableInClassList("alt", alt);
            line.EnableInClassList("mine", mine);
            line.EnableInClassList("owner", m.owner);
            line.EnableInClassList("moderator", m.moderator);
            line.EnableInClassList("ready", lobby.IsPrivate && m.ready);
            line.EnableInClassList("waiting", lobby.Phase is LobbyPhase.Loading or LobbyPhase.Countdown or LobbyPhase.Racing && !m.inRace);
            line.EnableInClassList("voted", lobby.Phase == LobbyPhase.Voting && lobby.VoteOf(m.accountId) != null);
            var kickable = !mine && (OnlineService.IsModerator || OnlineLobby.IsOwner);
            line.EnableInClassList("kickable", kickable);
            var country = row.Q<Label>("lb-country");
            country.text = string.IsNullOrEmpty(m.country) ? "--" : m.country.ToUpperInvariant();
            country.tooltip = Countries.Name(m.country);
            row.SetText("lb-name", m.username);
            row.SetText("lb-sub", m.HasLoadout ? ContentDisplay.CharacterName(m.characterId) + UiText.Separator + ContentDisplay.KartName(m.kartId) : noLoadoutText);
            ContentDisplay.SetDriver(row, m.characterId, m.kartId);
            row.SetText("lob-ping", m.pingMs > 0 ? m.pingMs.ToString(CultureInfo.InvariantCulture) + " ms" : pingUnknown);
            row.SetText("lob-points", m.setPoints.ToString(CultureInfo.InvariantCulture));
            var kick = row.Q<Button>("lob-kick");
            if (kick != null && kickable) kick.clicked += () => AskKick(kick, m);
            return row;
        }

        void AskKick(Button from, LobbyMember m)
        {
            Click();
            TextPrompt.Show(from, string.Format(kickQuestionFormat, m.username), kickLabel, async reason =>
            {
                var r = await OnlineLobby.KickMember(m.accountId, reason);
                SetStatus(r.Ok ? string.Format(kickedFormat, m.username) : r.message, r.Ok);
            }, LobbyLimits.KickReason, "", true, !OnlineService.IsModerator);
        }

        void ShowSide(LobbyState lobby, LobbyMember me)
        {
            var character = me != null ? GameRoot.Instance.Content.Get<CharacterDefinition>(me.characterId) : null;
            var kart = me != null ? GameRoot.Instance.Content.Get<KartDefinition>(me.kartId) : null;
            ContentDisplay.SetDriver(Q<VisualElement>("side"), me?.characterId, me?.kartId);
            Q<Label>("loadout-name").text = character != null ? character.DisplayName : noLoadoutText;
            Q<Label>("loadout-kart").text = kart != null ? kart.DisplayName : "";
            var waiting = lobby.Phase is LobbyPhase.Waiting or LobbyPhase.Voting or LobbyPhase.Results or LobbyPhase.SetResults;
            Q<Button>("pick-driver").SetEnabled(waiting);
            Q<Button>("pick-kart").SetEnabled(waiting && character != null);
            var ready = Q<Button>("ready");
            ready.SetVisible(lobby.IsPrivate);
            var isReady = me != null && me.ready;
            ready.EnableInClassList("selected", isReady);
            Q<Label>("ready-label").text = isReady ? readyText : notReadyText;
            var vote = Q<Button>("vote");
            vote.SetVisible(lobby.Phase == LobbyPhase.Voting);
            Q<Label>("vote-label").text = me != null && lobby.VoteOf(me.accountId) != null ? changeVoteText : voteText;
            var owner = OnlineLobby.IsOwner && lobby.Phase == LobbyPhase.Waiting;
            var start = Q<Button>("start");
            start.SetVisible(owner);
            if (owner) Q<Label>("start-label").text = StartLabel(lobby);
            SetVisible("room-settings", owner);
        }

        string StartLabel(LobbyState lobby)
        {
            if (lobby.settings.mode == RoomMode.Championship)
            {
                var next = NextCupTrack(lobby);
                return string.Format(startCupFormat, next != null ? next.DisplayName : "?");
            }
            return lobby.settings.OwnerPicks ? pickTrackText : startVoteText;
        }

        // The cup's track after the races of the set so far (a finished set starts over).
        static TrackDefinition NextCupTrack(LobbyState lobby)
        {
            var cup = GameRoot.Instance.Content.Get<CupDefinition>(lobby.settings.cupId);
            if (cup == null || cup.tracks.Count == 0) return null;
            var done = lobby.setRace;
            return cup.tracks[done >= cup.tracks.Count ? 0 : done];
        }

        void ShowResults(LobbyState lobby)
        {
            var list = Q<VisualElement>("result-list");
            list.Clear();
            var rows = lobby.lastResults.Count > 0 ? lobby.lastResults : OnlineLobby.LastResults?.rows ?? new List<RaceResultRow>();
            Q<Label>("results-title").text = rows.Count == 0 ? noResultsTitle : lobby.Phase == LobbyPhase.SetResults ? setTitle : lastRaceTitle;
            var sorted = new List<RaceResultRow>(rows);
            sorted.Sort((a, b) => a.position.CompareTo(b.position));
            var i = 0;
            foreach (var r in sorted)
            {
                var row = new VisualElement();
                row.AddToClassList("lob-result-row");
                row.EnableInClassList("alt", i++ % 2 == 1);
                row.EnableInClassList("mine", r.accountId == OnlineLobby.MyAccountId && !r.ai);
                row.Add(Cell(UiText.Ordinal(r.position), "lob-result-pos"));
                row.Add(Cell(r.username, "lob-result-name"));
                row.Add(Cell(r.finishMs > 0 ? UiText.Clock(r.finishMs / 1000f) : dnfText, "lob-result-time"));
                row.Add(Cell((lobby.Phase == LobbyPhase.SetResults ? r.setPoints : r.points).ToString("+0;-0;0", CultureInfo.InvariantCulture), "lob-result-points"));
                list.Add(row);
            }
        }

        static Label Cell(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        // Voting opens the vote page by itself once per round while the player has not voted.
        void AutoOpenVote(LobbyState lobby, LobbyMember me)
        {
            if (lobby.Phase != LobbyPhase.Voting || me == null || lobby.VoteOf(me.accountId) != null) return;
            if (autoVoteFor == lobby.phaseEnds || Flow.Current != this) return;
            autoVoteFor = lobby.phaseEnds;
            Root.schedule.Execute(OpenVote);
        }

        void CopyCode()
        {
            var lobby = OnlineLobby.Current;
            if (lobby == null || string.IsNullOrEmpty(lobby.code)) return;
            GUIUtility.systemCopyBuffer = lobby.code;
            SetStatus(codeCopiedText, true);
        }

        void CopyLink()
        {
            var lobby = OnlineLobby.Current;
            if (lobby == null || string.IsNullOrEmpty(lobby.code)) return;
            GUIUtility.systemCopyBuffer = JoinLinks.Link(lobby.code);
            SetStatus(linkCopiedText, true);
        }

        // Driver (then kart) or just the kart, starting from the current loadout.
        void OpenLoadout(bool driver)
        {
            var me = OnlineLobby.Me;
            var content = GameRoot.Instance.Content;
            Selection.lobbyPick = LobbyPick.Loadout;
            Selection.character = me != null ? content.Get<CharacterDefinition>(me.characterId) : null;
            Selection.kart = me != null ? content.Get<KartDefinition>(me.kartId) : null;
            if (driver || Selection.character == null) Flow.Show<CharacterScreen>();
            else Flow.Show<KartScreen>();
        }

        async void ToggleReady()
        {
            var me = OnlineLobby.Me;
            if (me == null) return;
            var r = await OnlineLobby.SetReady(!me.ready);
            if (!r.Ok) SetStatus(r.message, false);
        }

        void OpenVote()
        {
            if (OnlineLobby.Current?.Phase != LobbyPhase.Voting) return;
            Selection.lobbyPick = LobbyPick.Vote;
            Flow.Show<TrackScreen>();
        }

        async void StartNext()
        {
            var lobby = OnlineLobby.Current;
            if (lobby == null) return;
            if (lobby.settings.mode == RoomMode.Championship)
            {
                var next = NextCupTrack(lobby);
                var r = await OnlineLobby.StartRace(next != null ? next.Id : "");
                if (!r.Ok) SetStatus(r.message, false);
                return;
            }
            if (lobby.settings.OwnerPicks)
            {
                Selection.lobbyPick = LobbyPick.OwnerPick;
                Flow.Show<TrackScreen>();
                return;
            }
            var vote = await OnlineLobby.StartRace("");
            if (!vote.Ok) SetStatus(vote.message, false);
        }
    }
}
