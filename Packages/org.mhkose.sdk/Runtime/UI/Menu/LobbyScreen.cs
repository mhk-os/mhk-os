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
    /// The online lobby the player is in (Lobby.uxml). Header: name, the room code with Copy code / Copy link and the
    /// lobby's rules as chips. A banner with the phase and its countdown. One card per player (driver, kart, name with its
    /// score rank and player card, country, ping, set points, owner / moderator / ready / waiting / voted badges, kick for
    /// the room owner and moderators, also on the player card), one per AI driver of the set (character, kart, set
    /// points), ranked together by set points, and a tile for the free places up to the room size. Side: the player's
    /// driver and kart (the Character and Kart pages limited to the lobby's content), Ready in private rooms, Vote while
    /// voting (the Track page with the lobby's tracks and Random, opened by itself when voting starts), Race AI while the
    /// player is alone in a public lobby without AI, the owner's Start and Room settings, the last race's results (names
    /// with rank and player card, AI drivers marked) and Leave. Races start from RaceSetup (OnlineRace) and come back here
    /// with <see cref="OnlineLobby.ReturnToLobby"/>.
    /// </summary>
    public class LobbyScreen : MenuScreen
    {
        [SerializeField, Tooltip("Player card (Templates/LobbyMemberCard.uxml).")]
        VisualTreeAsset memberTemplate;

        [Header("Texts")]
        [SerializeField] string waitingOwnerText = "Start when you are ready";
        [SerializeField] string waitingOthersText = "Waiting for the owner";
        [SerializeField] string aloneText = "Waiting for players";
        [SerializeField] string votingText = "Vote for the next track";
        [SerializeField, Tooltip("{0} = track.")] string loadingFormat = "Loading {0}";
        [SerializeField] string countdownText = "Get ready!";
        [SerializeField] string racingText = "Race running";
        [SerializeField] string sittingOutText = "You race in the next one";
        [SerializeField] string soloRacingText = "Racing AI";
        [SerializeField, Tooltip("Badge of an AI driver's card and result row.")] string aiDriverText = "Computer driver";
        [SerializeField] string resultsText = "Results";
        [SerializeField] string setResultsText = "Set results";
        [SerializeField, Tooltip("{0} = race in the set, {1} = races in the set.")] string setRaceFormat = "Race {0} of {1}";
        [SerializeField, Tooltip("Lobby title of a public lobby. {0} = mod set, {1} = number.")] string publicTitleFormat = "{0} #{1}";
        [SerializeField] string noLoadoutText = "Choose a driver";
        [SerializeField] string readyText = "Ready", notReadyText = "Ready?";
        [SerializeField] string voteText = "Vote", changeVoteText = "Change vote";
        [SerializeField] string startVoteText = "Start voting", pickTrackText = "Choose track";
        [SerializeField] string soloText = "Race AI while you wait";
        [SerializeField, Tooltip("Championship start. {0} = next track of the cup.")] string startCupFormat = "Start: {0}";
        [SerializeField] string lastRaceTitle = "Last race", setTitle = "Set results", soloTitle = "Last race vs AI";
        [SerializeField] string leaveQuestion = "Leave this lobby?", leaveLabel = "Leave";
        [SerializeField, Tooltip("{0} = player.")] string kickQuestionFormat = "Remove {0} from the lobby? They cannot come back for 10 minutes. Reason (shown to them):";
        [SerializeField] string kickLabel = "Kick";
        [SerializeField, Tooltip("{0} = player.")] string kickedFormat = "{0} was removed.";
        [SerializeField] string codeCopiedText = "Code copied", linkCopiedText = "Link copied";
        [SerializeField] string dnfText = "DNF";

        // Player cards: five per row, card heights (px), the gap between cards, the card's height around the portrait (top
        // row, name, badges, points, padding) and the portrait's size range.
        const int CardsPerRow = 5;
        const float MaxCardHeight = 292f, MinCardHeight = 168f, CompactBelow = 262f, CardGap = 16f;
        const float CardChrome = 168f, MinPortrait = 56f, MaxPortrait = 124f;
        const int CountdownMs = 250;

        Label countdown, status;
        VisualElement members;
        IVisualElementScheduledItem tick;
        readonly RankTags ranks = new();
        PlayerCardOptions cardOptions;
        // Voting round (lobby and race of the set) the vote page already opened for by itself. Not the phase's end time:
        // the server may move it while the round runs, which reopened the page right after the player left it.
        static (long lobby, int race) autoVoteFor;

        static List<string> NoPacks { get; } = new();

        /// <summary>The current lobby's content of type <typeparamref name="T"/> (empty outside a lobby).</summary>
        public static List<T> LobbyContent<T>() where T : ContentDefinition =>
            ContentManifests.LobbyContent<T>(GameRoot.Instance.Content, OnlineLobby.Current?.settings.packs ?? NoPacks);

        /// <summary>The current lobby fights battles (arenas).</summary>
        public static bool IsBattle => OnlineLobby.Current != null && OnlineLobby.Current.settings.mode == RoomMode.BattleSet;

        /// <summary>
        /// The player is alone in a waiting public lobby without AI, where they may start a solo race against AI (a
        /// private room's owner starts races with Start, also alone; AI lobbies start by themselves).
        /// </summary>
        public static bool CanRaceAlone
        {
            get
            {
                var lobby = OnlineLobby.Current;
                return lobby != null && !lobby.IsPrivate && lobby.settings.maxAi == 0 && lobby.Phase == LobbyPhase.Waiting
                       && lobby.members.Count == 1 && OnlineLobby.Me != null;
            }
        }

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

        /// <summary>Track page of the room owner or of a player racing AI: starts the race there (<c>""</c> = random) and returns to the lobby.</summary>
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
            OnlineUi.Spatial(Root);
            countdown = Q<Label>("countdown");
            status = Q<Label>("lob-status-label");
            members = Q<VisualElement>("member-list");
            Q<ScrollView>("member-scroll").RegisterCallback<GeometryChangedEvent>(_ => FitCards());
            OnClick("copy-code", CopyCode);
            OnClick("copy-link", CopyLink);
            OnClick("pick-driver", OpenLoadout);
            OnClick("ready", ToggleReady);
            OnClick("vote", OpenVote);
            OnClick("solo", OpenSolo);
            OnClick("start", StartNext);
            OnClick("room-settings", () => Flow.Get<RoomSettingsScreen>().OpenEdit());
            SetStatus(null, false);
            cardOptions = new PlayerCardOptions { report = SetStatus };
            ranks.Attach(Root);
            OnlineLobby.Changed += Refresh;
            OnlineLobby.Left += OnLeft;
            tick = Root.schedule.Execute(Tick).Every(CountdownMs);
            Refresh();
            FocusTarget = FirstAction();
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineLobby.Changed -= Refresh;
            OnlineLobby.Left -= OnLeft;
            tick?.Pause();
            ranks.Detach();
            PlayerCard.Close();
            cardOptions = null;
            tick = null;
            countdown = status = null;
            members = null;
        }

        // The most likely next step: the loadout when there is none, else the shown action.
        Button FirstAction()
        {
            if (!(OnlineLobby.Me?.HasLoadout ?? false)) return Q<Button>("pick-driver");
            foreach (var name in new[] { "vote", "solo", "start", "ready" })
            {
                var b = Q<Button>(name);
                if (b.resolvedStyle.display != DisplayStyle.None && b.style.display != DisplayStyle.None) return b;
            }
            return Q<Button>("pick-driver");
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
            box.Q(className: "icon")?.SetIcon(good ? "check" : "error");
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
            Q<Label>("title").text = lobby.IsPrivate
                ? lobby.summary.name
                : string.Format(publicTitleFormat, OnlineUi.SetName(lobby.settings.packs), lobby.summary.instance);
            var hasCode = !string.IsNullOrEmpty(lobby.code);
            SetVisible("code-box", hasCode);
            if (hasCode) Q<Label>("code").text = lobby.code;
            var chips = Q<VisualElement>("settings-chips");
            OnlineUi.SettingsChips(chips, lobby.settings, lobby.IsPrivate);
            var mods = InfoChips.Add(null, "extension", OnlineUi.SetName(lobby.settings.packs), OnlineUi.SetTooltip(lobby.settings.packs));
            chips.Insert(0, mods);
            InfoChips.Add(chips, "group", lobby.members.Count.ToString(CultureInfo.InvariantCulture) + "/" + lobby.settings.maxPlayers.ToString(CultureInfo.InvariantCulture), "Players");
            var footer = Q<VisualElement>("footer-chips");
            footer.Clear();
            InfoChips.Add(footer, lobby.IsPrivate ? "lock" : "public", lobby.IsPrivate ? "Private room" : "Public lobby");
        }

        void ShowBanner(LobbyState lobby, LobbyMember me)
        {
            var alone = lobby.members.Count == 1;
            var soloRace = OnlineRace.Current != null && OnlineRace.Current.Solo && OnlineRace.Current.AiCount > 0;
            var (text, icon) = lobby.Phase switch
            {
                LobbyPhase.Waiting when alone && !lobby.IsPrivate => (aloneText, "hourglass_top"),
                LobbyPhase.Waiting => (lobby.IsPrivate ? OnlineLobby.IsOwner ? waitingOwnerText : waitingOthersText : aloneText, "hourglass_top"),
                LobbyPhase.Voting => (votingText, "how_to_vote"),
                LobbyPhase.Loading => (string.Format(loadingFormat, ContentDisplay.TrackName(lobby.trackId)), "sync"),
                LobbyPhase.Countdown => (countdownText, "counter_3"),
                LobbyPhase.Racing => (me != null && !me.inRace ? sittingOutText : soloRace ? soloRacingText : racingText, "sports_score"),
                LobbyPhase.Results => (resultsText, "emoji_events"),
                _ => (setResultsText, "emoji_events"),
            };
            Q<Label>("phase").text = text;
            Q<VisualElement>("phase-icon").SetIcon(icon);
            var parts = new List<string>();
            if (lobby.setRace > 0) parts.Add(string.Format(setRaceFormat, lobby.setRace, lobby.setSize));
            if (!string.IsNullOrEmpty(lobby.trackId) && lobby.Phase is not (LobbyPhase.Loading or LobbyPhase.Waiting or LobbyPhase.Voting))
                parts.Add(ContentDisplay.TrackName(lobby.trackId));
            var sub = Q<Label>("phase-sub");
            sub.text = string.Join(UiText.Separator, parts);
            sub.SetVisible(parts.Count > 0);
        }

        void ShowMembers(LobbyState lobby, LobbyMember me)
        {
            var focused = members.panel?.focusController?.focusedElement as VisualElement;
            long focusedId = 0;
            var focusedName = focused is Label { name: "mem-name" };
            if (focused != null)
                foreach (var card in members.Children())
                    if (card.Contains(focused) && card.userData is long id) focusedId = id;
            members.Clear();
            var list = new List<LobbyMember>(lobby.members);
            VisualElement refocus = null;
            var showPoints = lobby.setRace > 0 || list.Exists(m => m.setPoints != 0) || lobby.aiDrivers.Exists(d => d.setPoints != 0);
            // Standings: points first, then who joined first; AI drivers among them by points (after humans on a tie).
            list.Sort((a, b) => a.setPoints != b.setPoints ? b.setPoints.CompareTo(a.setPoints) : a.joined.CompareTo(b.joined));
            var ai = new List<AiDriver>(lobby.aiDrivers);
            ai.Sort((a, b) => b.setPoints.CompareTo(a.setPoints));
            int h = 0, d = 0;
            while (h < list.Count || d < ai.Count)
            {
                if (h < list.Count && (d >= ai.Count || list[h].setPoints >= ai[d].setPoints))
                {
                    var m = list[h++];
                    var card = SpawnMember(lobby, m, me, showPoints);
                    if (card == null) break;
                    if (m.accountId == focusedId) refocus = card.Q(focusedName ? "mem-name" : "mem-kick");
                }
                else if (SpawnAi(ai[d++], showPoints) == null) break;
            }
            var free = Mathf.Min(lobby.settings.maxPlayers, LobbyLimits.MaxPlayers) - list.Count;
            if (free > 0) members.Add(FreeTile(free, lobby.settings.maxAi));
            if (refocus != null) FocusLater(refocus);
            FitCards();
        }

        // One quiet tile for the free places (the room size stays obvious without a box per place).
        static VisualElement FreeTile(int free, int maxAi)
        {
            var tip = free == 1 ? "1 free place" : free + " free places";
            if (maxAi > 0) tip += $": computer drivers race in free places (up to {maxAi})";
            var tile = new VisualElement { tooltip = tip };
            tile.AddToClassList("mem-free-tile");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("icon");
            icon.AddToClassList("i-person_add");
            icon.AddToClassList("mem-free-icon");
            tile.Add(icon);
            var count = new Label("+" + free.ToString(CultureInfo.InvariantCulture)) { pickingMode = PickingMode.Ignore };
            count.AddToClassList("mem-free-count");
            tile.Add(count);
            var label = new Label("free") { pickingMode = PickingMode.Ignore };
            label.AddToClassList("mem-free-label");
            tile.Add(label);
            return tile;
        }

        // Two rows of five cards fill the height left between banner and footer (wide phone screens have less of it):
        // the cards and their round portrait shrink with it, short cards use the compact text sizes.
        void FitCards()
        {
            if (members == null) return;
            var height = Q<ScrollView>("member-scroll").layout.height;
            var width = members.layout.width;
            if (float.IsNaN(height) || height <= 0f || float.IsNaN(width) || width <= 0f) return;
            // Five per row with even gaps and no trailing gap: the cards end flush with the banner's edges (one pixel spare:
            // the layout rounds to physical pixels at non-integer scales and would wrap the fifth card).
            var cardWidth = Mathf.Floor((width - (CardsPerRow - 1) * CardGap - 1f) / CardsPerRow);
            var card = Mathf.Clamp(Mathf.Floor(height / 2f) - CardGap, MinCardHeight, MaxCardHeight);
            var portrait = Mathf.Min(Mathf.Clamp(card - CardChrome, MinPortrait, MaxPortrait), cardWidth - 2f * CardGap);
            var kart = Mathf.Round(portrait * 0.5f);
            var index = 0;
            foreach (var c in members.Children())
            {
                c.style.width = cardWidth;
                c.style.height = card;
                c.style.marginRight = index++ % CardsPerRow == CardsPerRow - 1 ? 0f : CardGap;
                c.style.marginBottom = CardGap;
                c.EnableInClassList("compact", card < CompactBelow);
                foreach (var name in new[] { "mem-driver", "driver-portrait" })
                {
                    var e = c.Q(name);
                    if (e == null) continue;
                    e.style.width = portrait;
                    e.style.height = portrait;
                    e.style.borderTopLeftRadius = e.style.borderTopRightRadius = e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = portrait / 2f;
                }
                var k = c.Q("driver-kart");
                if (k == null) continue;
                k.style.width = kart;
                k.style.height = kart;
                k.style.borderTopLeftRadius = k.style.borderTopRightRadius = k.style.borderBottomLeftRadius = k.style.borderBottomRightRadius = kart / 2f;
            }
        }

        VisualElement SpawnMember(LobbyState lobby, LobbyMember m, LobbyMember me, bool showPoints)
        {
            var card = UiTree.Spawn<VisualElement>(memberTemplate, members);
            if (card == null) return null;
            card.userData = m.accountId;
            var mine = me != null && m.accountId == me.accountId;
            card.EnableInClassList("mine", mine);
            var waiting = lobby.Phase is LobbyPhase.Loading or LobbyPhase.Countdown or LobbyPhase.Racing && !m.inRace;
            card.EnableInClassList("waiting", waiting);
            var country = card.Q<Label>("mem-country");
            country.SetVisible(!string.IsNullOrEmpty(m.country));
            country.text = string.IsNullOrEmpty(m.country) ? "" : m.country.ToUpperInvariant();
            country.tooltip = Countries.Name(m.country);
            var (platformIcon, platformName) = OnlineUi.PlatformIcon(m.platform);
            var platform = card.Q("mem-platform");
            platform.SetVisible(platformIcon != null);
            if (platformIcon != null) platform.SetIcon(platformIcon);
            platform.tooltip = platformName ?? "";
            var name = card.Q<Label>("mem-name");
            name.text = m.username;
            ranks.Add(card.Q<Label>("mem-rank"), m.accountId);
            ContentDisplay.SetDriver(card, m.characterId, m.kartId);
            var ping = card.Q("mem-ping");
            ping.Clear();
            // The server measures the ping in races only: none before the first one.
            if (m.pingMs > 0)
                InfoChips.Add(ping, OnlineUi.PingIcon(m.pingMs), m.pingMs.ToString(CultureInfo.InvariantCulture), $"Ping {m.pingMs} ms", OnlineUi.PingState(m.pingMs));
            var points = card.Q<Label>("mem-points");
            points.text = m.setPoints.ToString(CultureInfo.InvariantCulture);
            points.SetVisible(showPoints);
            var badges = card.Q("mem-badges");
            badges.Clear();
            if (mine) InfoChips.Add(badges, null, "You", null, InfoChipState.On);
            if (m.owner) InfoChips.Add(badges, "crown", null, "Room owner", InfoChipState.On);
            if (m.moderator) InfoChips.Add(badges, "shield_person", null, "Moderator");
            if (lobby.IsPrivate && m.ready) InfoChips.Add(badges, "check", null, "Ready", InfoChipState.Good);
            if (waiting) InfoChips.Add(badges, "hourglass_top", null, "Races in the next one");
            if (lobby.Phase == LobbyPhase.Voting && lobby.VoteOf(m.accountId) != null) InfoChips.Add(badges, "how_to_vote", null, "Voted");
            var kick = card.Q<Button>("mem-kick");
            var kickable = !mine && (OnlineService.IsModerator || OnlineLobby.IsOwner);
            kick.SetVisible(kickable);
            if (kickable) kick.clicked += () => AskKick(kick, m);
            PlayerCard.Attach(name, m.accountId, m.username, cardOptions, kickable ? from => AskKick(from, m) : (Action<VisualElement>)null);
            return card;
        }

        // An AI driver of the set: its character and kart, set points and a badge; nobody can kick or open it.
        VisualElement SpawnAi(AiDriver driver, bool showPoints)
        {
            var card = UiTree.Spawn<VisualElement>(memberTemplate, members);
            if (card == null) return null;
            card.AddToClassList("ai");
            card.Q("mem-country").SetVisible(false);
            card.Q("mem-platform").SetVisible(false);
            card.Q("mem-rank").SetVisible(false);
            card.Q("mem-ping").Clear();
            card.Q<Label>("mem-name").text = OnlineRace.CharacterName(driver.characterId);
            ContentDisplay.SetDriver(card, driver.characterId, driver.kartId);
            var points = card.Q<Label>("mem-points");
            points.text = driver.setPoints.ToString(CultureInfo.InvariantCulture);
            points.SetVisible(showPoints);
            var badges = card.Q("mem-badges");
            badges.Clear();
            InfoChips.Add(badges, "smart_toy", null, aiDriverText);
            card.Q<Button>("mem-kick").SetVisible(false);
            return card;
        }

        void AskKick(VisualElement from, LobbyMember m)
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
            // The same pictures as the player cards.
            ContentDisplay.SetDriver(Root.Q(className: "lob-loadout"), me?.characterId, me?.kartId);
            Q<Label>("loadout-name").text = character != null ? character.DisplayName : noLoadoutText;
            Q<Label>("loadout-kart").text = kart != null ? kart.DisplayName : "";
            var between = lobby.Phase is LobbyPhase.Waiting or LobbyPhase.Voting or LobbyPhase.Results or LobbyPhase.SetResults;
            Q<Button>("pick-driver").SetEnabled(between);
            var alone = CanRaceAlone;
            var ready = Q<Button>("ready");
            ready.SetVisible(lobby.IsPrivate && !OnlineLobby.IsOwner && lobby.Phase == LobbyPhase.Waiting);
            var isReady = me != null && me.ready;
            ready.EnableInClassList("selected", isReady);
            Q<Label>("ready-label").text = isReady ? readyText : notReadyText;
            var vote = Q<Button>("vote");
            vote.SetVisible(lobby.Phase == LobbyPhase.Voting);
            Q<Label>("vote-label").text = me != null && lobby.VoteOf(me.accountId) != null ? changeVoteText : voteText;
            var solo = Q<Button>("solo");
            solo.SetVisible(alone);
            Q<Label>("solo-label").text = soloText;
            var owner = OnlineLobby.IsOwner && lobby.Phase == LobbyPhase.Waiting;
            var start = Q<Button>("start");
            start.SetVisible(owner);
            if (owner) Q<Label>("start-label").text = StartLabel(lobby);
            SetVisible("room-settings", OnlineLobby.IsOwner && lobby.Phase == LobbyPhase.Waiting);
        }

        string StartLabel(LobbyState lobby)
        {
            if (lobby.settings.mode == RoomMode.Championship) return string.Format(startCupFormat, ContentDisplay.TrackName(NextChampionshipTrack(lobby)));
            return lobby.settings.OwnerPicks ? pickTrackText : startVoteText;
        }

        // The cup's (or the room's own list's) track after the races of the set so far (a finished set starts over), "" when none.
        static string NextChampionshipTrack(LobbyState lobby)
        {
            var s = lobby.settings;
            var done = lobby.setRace;
            if (s.CustomTracks) return s.trackIds.Count > 0 ? s.trackIds[done >= s.trackIds.Count ? 0 : done] : "";
            var cup = GameRoot.Instance.Content.Get<CupDefinition>(s.cupId);
            if (cup == null || cup.tracks.Count == 0) return "";
            var next = cup.tracks[done >= cup.tracks.Count ? 0 : done];
            return next != null ? next.Id : "";
        }

        // The last race: from the lobby (results phases) or the race's own result (solo races have no results phase).
        // Solo races give no points: no points column then. Human racers' names carry their rank and player card, AI
        // drivers their character's name and a badge. The set results rank by set points.
        void ShowResults(LobbyState lobby)
        {
            var list = Q<VisualElement>("result-list");
            list.Clear();
            var rows = lobby.lastResults.Count > 0 ? lobby.lastResults : OnlineLobby.LastResults?.rows ?? new List<RaceResultRow>();
            SetVisible("results", rows.Count > 0);
            if (rows.Count == 0) return;
            var points = rows.Exists(r => r.points != 0);
            var setPhase = lobby.Phase == LobbyPhase.SetResults;
            Q<Label>("results-title").text = setPhase ? setTitle : points ? lastRaceTitle : soloTitle;
            var sorted = new List<RaceResultRow>(rows);
            if (setPhase) sorted.Sort((a, b) => a.setPoints != b.setPoints ? b.setPoints.CompareTo(a.setPoints) : a.position.CompareTo(b.position));
            else sorted.Sort((a, b) => a.position.CompareTo(b.position));
            var place = 0;
            foreach (var r in sorted)
            {
                var shown = setPhase ? ++place : r.position;
                var row = new VisualElement();
                row.AddToClassList("lob-result-row");
                if (shown <= 3) row.AddToClassList("medal-" + shown.ToString(CultureInfo.InvariantCulture));
                row.EnableInClassList("mine", r.accountId == OnlineLobby.MyAccountId && !r.ai);
                row.Add(Cell(shown.ToString(CultureInfo.InvariantCulture), "lob-result-pos"));
                var who = new VisualElement();
                who.AddToClassList("lob-result-who");
                var name = Cell(r.ai ? OnlineRace.CharacterName(r.characterId) : r.username, "lob-result-name");
                who.Add(name);
                if (r.ai)
                {
                    var mark = new VisualElement { tooltip = aiDriverText };
                    mark.AddToClassList("icon");
                    mark.AddToClassList("lob-result-ai");
                    mark.SetIcon("smart_toy");
                    who.Add(mark);
                }
                row.Add(who);
                row.Add(Cell(r.finishMs > 0 ? UiText.Clock(r.finishMs / 1000f) : dnfText, "lob-result-time"));
                if (points) row.Add(Cell((setPhase ? r.setPoints : r.points).ToString(setPhase ? "0" : "+0;-0;0", CultureInfo.InvariantCulture), "lob-result-points"));
                list.Add(row);
                if (r.ai) continue;
                ranks.AddAfter(name, r.accountId);
                var member = r.accountId != OnlineLobby.MyAccountId && (OnlineService.IsModerator || OnlineLobby.IsOwner) ? lobby.Member(r.accountId) : null;
                PlayerCard.Attach(name, r.accountId, r.username, cardOptions, member != null ? from => AskKick(from, member) : (Action<VisualElement>)null);
            }
        }

        static Label Cell(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        // Voting opens the vote page by itself once per round while the player has not voted. Back from it returns
        // here for good: the Vote button reopens it.
        void AutoOpenVote(LobbyState lobby, LobbyMember me)
        {
            // Any other phase re-arms it for the next round.
            if (lobby.Phase != LobbyPhase.Voting)
            {
                autoVoteFor = default;
                return;
            }
            if (me == null || lobby.VoteOf(me.accountId) != null) return;
            var round = (lobby.summary.lobbyId, lobby.setRace);
            if (autoVoteFor == round || Flow.Current != this) return;
            autoVoteFor = round;
            // A scheduled item of the screen's root resumes when the root is shown again (Back from the vote page): it
            // must open the page once only, else Back seems to do nothing.
            var pending = true;
            Root.schedule.Execute(() =>
            {
                if (!pending) return;
                pending = false;
                OpenVote();
            });
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

        // Driver, then kart (KartScreen sends both at once and returns here), starting from the current loadout.
        void OpenLoadout()
        {
            var me = OnlineLobby.Me;
            var content = GameRoot.Instance.Content;
            Selection.lobbyPick = LobbyPick.Loadout;
            Selection.character = me != null ? content.Get<CharacterDefinition>(me.characterId) : null;
            Selection.kart = me != null ? content.Get<KartDefinition>(me.kartId) : null;
            Flow.Show<CharacterScreen>();
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
            if (OnlineLobby.Current?.Phase != LobbyPhase.Voting || Flow.Current != this) return;
            Selection.lobbyPick = LobbyPick.Vote;
            Flow.Show<TrackScreen>();
        }

        void OpenSolo()
        {
            if (!CanRaceAlone) return;
            if (!(OnlineLobby.Me?.HasLoadout ?? false))
            {
                OpenLoadout();
                return;
            }
            Selection.lobbyPick = LobbyPick.Solo;
            Flow.Show<TrackScreen>();
        }

        async void StartNext()
        {
            var lobby = OnlineLobby.Current;
            if (lobby == null) return;
            if (lobby.settings.mode == RoomMode.Championship)
            {
                var r = await OnlineLobby.StartRace(NextChampionshipTrack(lobby));
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
