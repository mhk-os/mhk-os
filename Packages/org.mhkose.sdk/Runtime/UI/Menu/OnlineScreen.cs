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
    /// Online play (Online.uxml). While logged out: Log in (the Account page). Logged in: Races / Battles and anticheat
    /// on / off choose the public categories, one card per mod set ("MHK", "MHK + TINcan", "MHK 2") with its players;
    /// clicking a card joins its fullest lobby with room (Quick join), the focused card's lobbies are listed below it.
    /// The side panel joins a private room by code and creates one. Content warnings (packs that differ from other
    /// players'), refusals and why the player last left a lobby show in the status line. A room link
    /// (<see cref="JoinLinks"/>) opens this page, which joins the room once logged in.
    /// </summary>
    public class OnlineScreen : MenuScreen
    {
        [SerializeField, Tooltip("Mod set card (Templates/OnlineSetCard.uxml).")]
        VisualTreeAsset setTemplate;
        [SerializeField, Tooltip("Lobby of the selected set (Templates/OnlineInstanceRow.uxml).")]
        VisualTreeAsset instanceTemplate;
        [SerializeField, Tooltip("Seconds between list refreshes while the page is open.")]
        float refreshSeconds = 5f;

        [Header("Texts")]
        [SerializeField] string loggedOutText = "Log in to race online.";
        [SerializeField, Tooltip("{0} = room code.")] string linkLoginFormat = "Log in to join room {0}.";
        [SerializeField, Tooltip("{0} = room code.")] string linkJoiningFormat = "Joining room {0}...";
        [SerializeField] string loginText = "Log in";
        [SerializeField] string announcingText = "Sending your content to the server...";
        [SerializeField] string codePlaceholder = "Room code";
        [SerializeField] string noLobbiesText = "No lobby yet";
        [SerializeField] string kickedByOwnerText = "The room owner removed you.", kickedByModText = "A moderator removed you.";
        [SerializeField] string closedText = "The lobby closed.";
        [SerializeField] string strikeSuffix = " Anticheat lobbies with it are closed to you.";

        // Remembered while the game runs: the page opens on the player's last choice.
        static bool battles;
        static bool anticheat = true;
        static int selected = 1;

        readonly Dictionary<int, VisualElement> cards = new();
        List<LobbySummary> lobbies = new();
        bool busy, refreshing;
        IVisualElementScheduledItem refresh;
        Label statusLabel;

        /// <summary>A room link arrived: this page now in the main menu, or when the menu starts next.</summary>
        public static void OpenForLink()
        {
            // The menu's backdrop track is the active scene in the main menu: look for the menu's own flow instead.
            var flow = MainFlow();
            if (flow == null || flow.Current == null)
            {
                MenuFlow.OpenOnStart = typeof(OnlineScreen);
                return;
            }
            if (flow.Current is OnlineScreen online)
            {
                online.TryPendingJoin();
                return;
            }
            flow.ShowTitle();
            flow.Show<OnlineScreen>();
        }

        // The main menu's flow (the race pause overlay has no title screen).
        static MenuFlow MainFlow()
        {
            foreach (var f in FindObjectsByType<MenuFlow>(FindObjectsInactive.Exclude))
                if (f.titleScreen != null) return f;
            return null;
        }

        protected override void Bind()
        {
            Selection.lobbyPick = LobbyPick.None;
            statusLabel = Q<Label>("onl-status-label");
            OnClick("login", () => Flow.Show<AccountScreen>());
            OnClick("account", () => Flow.Show<AccountScreen>());
            OnClick("refresh", () => Refresh(true));
            OnClick("create", CreateRoom);
            OnClick("join-code", JoinCode);
            var code = Q<TextField>("code");
            code.textEdition.placeholder = codePlaceholder;
            code.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;
                e.StopPropagation();
                JoinCode();
            }, TrickleDown.TrickleDown);
            OnlineService.Changed += ShowState;
            OnlineLobby.Changed += ShowState;
            OnlineService.Connect();
            OnlineUi.Segmented(Q<VisualElement>("mode-tabs"), new[] { ("sports_score", "Races", (string)null), ("swords", "Battles", null) },
                battles ? 1 : 0, i =>
                {
                    Click();
                    battles = i == 1;
                    FillSets(true);
                });
            OnlineUi.Segmented(Q<VisualElement>("anticheat-tabs"),
                new[]
                {
                    ("shield", "Anticheat", "Players' games check each other's karts, cheaters are removed from the race."),
                    ("remove_moderator", "Off", "No anticheat: nobody checks the karts."),
                }, anticheat ? 0 : 1, i =>
                {
                    Click();
                    anticheat = i == 0;
                    FillSets(true);
                });
            OnlineUi.Spatial(Root, Navigate);
            SetStatus(null, false);
            FillSets(false);
            ShowLeft();
            ShowState();
            refresh = Root.schedule.Execute(() => Refresh(false)).Every((long)(refreshSeconds * 1000f));
            Refresh(false);
        }

        protected override void Unbind()
        {
            base.Unbind();
            OnlineService.Changed -= ShowState;
            OnlineLobby.Changed -= ShowState;
            refresh?.Pause();
            refresh = null;
            cards.Clear();
            statusLabel = null;
        }

        // On top of the nearest-element navigation: the tabs and the lobby list go to the selected set card, the set cards
        // up to the selected mode tab, and the room code field left to the selected set card.
        VisualElement Navigate(VisualElement from, NavigationMoveEvent.Direction d)
        {
            var modeTabs = Q<VisualElement>("mode-tabs");
            var grid = Q<VisualElement>("set-grid");
            var code = Q<TextField>("code");
            cards.TryGetValue(selected, out var card);
            var setCard = OnlineUi.CanFocus(card) ? card : null;
            if (d == NavigationMoveEvent.Direction.Down && (from.parent == modeTabs || from.parent == Q<VisualElement>("anticheat-tabs")))
                return setCard;
            if (d == NavigationMoveEvent.Direction.Up && from.parent == grid) return OnlineUi.SelectedChip(modeTabs);
            if (d == NavigationMoveEvent.Direction.Up && Q<VisualElement>("inst-list").Contains(from)) return setCard;
            if (d == NavigationMoveEvent.Direction.Left && (from == code || code.Contains(from)) && code.cursorIndex <= 0) return setCard;
            return null;
        }

        bool LoggedIn => OnlineService.IsLoggedIn;

        void ShowState()
        {
            if (statusLabel == null) return;
            Root.Q("page").EnableInClassList("onl-logged-out", !LoggedIn);
            Q<Label>("account-label").text = OnlineService.Username ?? loginText;
            var pending = OnlineLobby.PendingJoinCode;
            Q<Label>("onl-login-label").text = !string.IsNullOrEmpty(OnlineService.LastError) && OnlineService.State == OnlineState.Offline
                ? OnlineService.LastError
                : pending != null ? string.Format(linkLoginFormat, pending) : loggedOutText;
            SetEnabled(LoggedIn && !busy);
            if (!LoggedIn) return;
            ShowContentState();
            TryPendingJoin();
        }

        void SetEnabled(bool on)
        {
            Q<Button>("create").SetEnabled(on);
            Q<Button>("join-code").SetEnabled(on);
            Q<Button>("refresh").SetEnabled(on);
            foreach (var card in cards.Values) card.SetEnabled(on && card.userData == null);
        }

        // Packs that differ from other players' stay visible: anticheat lobbies with them refuse the player.
        void ShowContentState()
        {
            switch (OnlineLobby.Content)
            {
                case ContentAnnouncement.Announcing:
                    SetStatus(announcingText, true);
                    return;
                case ContentAnnouncement.Failed:
                    SetStatus(OnlineLobby.ContentError, false);
                    return;
            }
            if (OnlineLobby.Strikes.Count == 0)
            {
                if (statusLabel.text == announcingText) SetStatus(null, false);
                return;
            }
            var lines = new List<string>();
            foreach (var s in OnlineLobby.Strikes) lines.Add(s.message);
            SetStatus(string.Join("\n", lines) + strikeSuffix, false);
        }

        void ShowLeft()
        {
            var left = OnlineLobby.LastLeft;
            if (left == null) return;
            OnlineLobby.ForgetLastLeft();
            var text = left.reason switch
            {
                LobbyLeftReason.KickedByOwner => kickedByOwnerText,
                LobbyLeftReason.KickedByModerator => kickedByModText,
                LobbyLeftReason.Closed => closedText,
                _ => "",
            };
            if (!string.IsNullOrEmpty(left.message)) text = string.IsNullOrEmpty(text) ? left.message : text + " " + left.message;
            SetStatus(text, false);
        }

        void SetStatus(string text, bool good)
        {
            if (statusLabel == null) return;
            statusLabel.text = text ?? "";
            var box = Q<VisualElement>("onl-status");
            box.SetVisible(!string.IsNullOrEmpty(text));
            box.EnableInClassList("good", good);
            box.Q(className: "icon")?.SetIcon(good ? "sync" : "warning");
        }

        // One card per category of the chosen mode and anticheat setting.
        void FillSets(bool focus)
        {
            var grid = Q<VisualElement>("set-grid");
            grid.Clear();
            cards.Clear();
            VisualElement first = null, current = null;
            var i = 0;
            foreach (var c in LobbyCategories.All)
            {
                if (c.Battle != battles || c.anticheat != anticheat) continue;
                var card = UiTree.Spawn<Button>(setTemplate, grid);
                if (card == null) break;
                cards[c.id] = card;
                card.SetTransitionDelay(60 + 40 * i++);
                card.SetText("set-name", OnlineUi.SetName(c.packs));
                card.Q("set-name").tooltip = OnlineUi.SetTooltip(c.packs);
                FillLogos(card.Q("set-logos"), c.packs);
                var problem = OnlineLobby.ContentProblem(c.packs);
                card.userData = problem;
                card.tooltip = problem ?? "";
                var category = c;
                card.clicked += () =>
                {
                    Click();
                    Join(() => OnlineLobby.QuickJoin(category.id));
                };
                card.RegisterCallback<FocusInEvent>(_ => Select(category.id));
                card.RegisterCallback<PointerEnterEvent>(_ => Select(category.id));
                first ??= card;
                if (c.id == selected) current = card;
            }
            if (current == null && first != null) selected = CategoryOf(first);
            ShowPlayers();
            Select(selected);
            SetEnabled(LoggedIn && !busy);
            var target = current ?? first;
            FocusTarget = target;
            if (focus) FocusLater(target);
        }

        int CategoryOf(VisualElement card)
        {
            foreach (var pair in cards)
                if (pair.Value == card) return pair.Key;
            return selected;
        }

        // The logos of the set's packs, as in the Mods screen.
        static void FillLogos(VisualElement row, IReadOnlyList<string> packs)
        {
            row.Clear();
            var registry = GameRoot.Instance.Content;
            foreach (var id in packs)
            {
                var pack = FindPack(registry, id);
                var logo = new VisualElement { pickingMode = PickingMode.Ignore, tooltip = UiText.GameName(id) };
                logo.AddToClassList("onl-set-logo");
                if (pack != null && pack.logo != null) logo.SetImage(pack.logo);
                else logo.AddToClassList("onl-set-logo-missing");
                row.Add(logo);
            }
        }

        static ContentPack FindPack(ContentRegistry registry, string id)
        {
            foreach (var p in registry.Packs)
                if (p != null && string.Equals(p.packId, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        void Select(int category)
        {
            selected = category;
            foreach (var pair in cards) pair.Value.EnableInClassList("selected", pair.Key == category);
            ShowInstances();
        }

        async void Refresh(bool click)
        {
            if (!LoggedIn || refreshing || busy) return;
            refreshing = true;
            var r = await OnlineLobby.ListLobbies();
            refreshing = false;
            if (statusLabel == null) return;
            if (!r.Ok)
            {
                if (click || OnlineLobby.Content != ContentAnnouncement.Failed) SetStatus(r.result.message, false);
                return;
            }
            lobbies = r.value;
            ShowPlayers();
            ShowInstances();
        }

        // Players racing in each category: the live count of the cards.
        void ShowPlayers()
        {
            foreach (var pair in cards)
            {
                int players = 0, count = 0;
                foreach (var l in lobbies)
                {
                    if (l.category != pair.Key) continue;
                    players += l.players;
                    count++;
                }
                var chips = pair.Value.Q("set-chips");
                chips.Clear();
                InfoChips.Add(chips, "group", players.ToString(CultureInfo.InvariantCulture),
                    players == 1 ? "1 player online" : players + " players online", players > 0 ? InfoChipState.On : InfoChipState.Normal);
                if (count > 1) InfoChips.Add(chips, "stadium", count.ToString(CultureInfo.InvariantCulture), count + " lobbies");
            }
        }

        // The lobbies of the selected category: number, players, phase.
        void ShowInstances()
        {
            if (statusLabel == null) return;
            var list = Q<VisualElement>("inst-list");
            list.Clear();
            var problem = cards.TryGetValue(selected, out var card) ? card.userData as string : null;
            var shown = 0;
            foreach (var l in lobbies)
            {
                if (l.category != selected) continue;
                var row = UiTree.Spawn<Button>(instanceTemplate, list);
                if (row == null) break;
                shown++;
                row.SetText("inst-name", "#" + l.instance.ToString(CultureInfo.InvariantCulture));
                var chips = row.Q("inst-chips");
                InfoChips.Add(chips, "group", l.players.ToString(CultureInfo.InvariantCulture) + "/" + l.maxPlayers.ToString(CultureInfo.InvariantCulture), "Players");
                var (icon, phase) = PhaseOf(l.phase);
                InfoChips.Add(chips, icon, phase);
                row.SetEnabled(problem == null && l.players < l.maxPlayers);
                var lobby = l;
                row.clicked += () =>
                {
                    Click();
                    Join(() => OnlineLobby.Join(lobby));
                };
            }
            if (shown > 0) return;
            var none = new Label(noLobbiesText);
            none.AddToClassList("onl-none");
            list.Add(none);
        }

        /// <summary>Icon and short name of a lobby phase.</summary>
        public static (string icon, string text) PhaseOf(LobbyPhase phase) => phase switch
        {
            LobbyPhase.Waiting => ("hourglass_top", "Waiting"),
            LobbyPhase.Voting => ("how_to_vote", "Voting"),
            LobbyPhase.Loading => ("sync", "Loading"),
            LobbyPhase.Countdown => ("counter_3", "Starting"),
            LobbyPhase.Racing => ("sports_score", "Racing"),
            LobbyPhase.Results => ("emoji_events", "Results"),
            _ => ("emoji_events", "Set results"),
        };

        void CreateRoom()
        {
            if (!LoggedIn) return;
            Flow.Get<RoomSettingsScreen>().Open();
        }

        void JoinCode()
        {
            var text = Q<TextField>("code").value;
            if (string.IsNullOrWhiteSpace(text))
            {
                FocusLater(Q<TextField>("code"));
                return;
            }
            Click();
            Join(() => OnlineLobby.JoinByCode(text));
        }

        /// <summary>A room link's code once logged in (the link may have arrived before the login).</summary>
        internal void TryPendingJoin()
        {
            var code = OnlineLobby.PendingJoinCode;
            if (code == null || !LoggedIn || busy || statusLabel == null) return;
            OnlineLobby.PendingJoinCode = null;
            Q<TextField>("code").SetValueWithoutNotify(code);
            SetStatus(string.Format(linkJoiningFormat, code), true);
            Join(() => OnlineLobby.JoinByCode(code));
        }

        async void Join(Func<Task<OnlineResult>> join)
        {
            if (busy) return;
            busy = true;
            SetEnabled(false);
            var r = await join();
            busy = false;
            if (statusLabel == null)
            {
                // The player left the page while the join ran: they did not stay to play, so they leave the lobby again
                // (else the next race would pull them out of whatever they do now).
                if (r.Ok) _ = OnlineLobby.Leave();
                return;
            }
            SetEnabled(LoggedIn);
            if (!r.Ok)
            {
                SetStatus(r.message, false);
                return;
            }
            SetStatus(null, false);
            Flow.Show<LobbyScreen>();
        }
    }
}
