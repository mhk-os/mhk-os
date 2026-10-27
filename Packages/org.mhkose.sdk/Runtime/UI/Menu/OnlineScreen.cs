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
    /// Online play (Online.uxml). While logged out: Log in (the Account page). Logged in: one table of every public lobby
    /// (mode, number, mod set, anticheat, engine class, players, phase; races first, then mod set, anticheat on first,
    /// then number), a row joins its lobby. The side panel joins a private room by code and creates one. Content warnings (packs that differ from other
    /// players'), refusals and why the player last left a lobby show in the status line. A room link
    /// (<see cref="JoinLinks"/>) opens this page, which joins the room once logged in. The footer opens the Account and
    /// Players pages.
    /// </summary>
    public class OnlineScreen : MenuScreen
    {
        [SerializeField, Tooltip("A row of the lobby table (Templates/OnlineInstanceRow.uxml).")]
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

        // Table width (px) below which the rows leave out the mod logos: the fixed columns, the set name and five logos.
        const float LogosMinWidth = 1180f;

        // The lobby focused last while the game runs: the table opens on it.
        static long focusedLobby;

        readonly List<Button> rows = new();
        // The table showed lobbies since the page opened.
        bool listed;
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
            listed = false;
            statusLabel = Q<Label>("onl-status-label");
            OnClick("login", () => Flow.Show<AccountScreen>());
            OnClick("account", () => Flow.Show<AccountScreen>());
            OnClick("players", () => Flow.Get<PlayersScreen>()?.Open(true));
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
            OnlineUi.Spatial(Root, Navigate);
            KeepFocusedInView(Q<ScrollView>("lobby-scroll"), Q<VisualElement>("lobby-list"));
            var list = Q<VisualElement>("lobby-list");
            list.RegisterCallback<GeometryChangedEvent>(_ => list.EnableInClassList("no-logos", list.layout.width < LogosMinWidth));
            SetStatus(null, false);
            FillRows(false);
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
            rows.Clear();
            statusLabel = null;
        }

        // Up / Down walk the table row by row (the nearest-element navigation would also do, but the rows are one column);
        // the room code field goes left to the focused lobby row.
        VisualElement Navigate(VisualElement from, NavigationMoveEvent.Direction d)
        {
            var code = Q<TextField>("code");
            if (d == NavigationMoveEvent.Direction.Left && (from == code || code.Contains(from)) && code.cursorIndex <= 0) return LastRow();
            if (from is not Button row || !rows.Contains(row)) return null;
            if (d is not (NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Down)) return null;
            var step = d == NavigationMoveEvent.Direction.Down ? 1 : -1;
            for (var i = rows.IndexOf(row) + step; i >= 0 && i < rows.Count; i += step)
                if (OnlineUi.CanFocus(rows[i])) return rows[i];
            return null;
        }

        // The row focused last, else the first that can be joined.
        VisualElement LastRow()
        {
            foreach (var r in rows)
                if (r.userData is LobbySummary l && l.lobbyId == focusedLobby && OnlineUi.CanFocus(r)) return r;
            foreach (var r in rows)
                if (OnlineUi.CanFocus(r)) return r;
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
            foreach (var row in rows) row.SetEnabled(on && Joinable(row.userData as LobbySummary));
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

        bool Joinable(LobbySummary l) => l != null && l.players < l.maxPlayers && OnlineLobby.ContentProblem(l.packs) == null;

        // One row per public lobby: races first, then mod set (category order), anticheat on first, then number.
        void FillRows(bool keepFocus)
        {
            var list = Q<VisualElement>("lobby-list");
            var focused = list.panel?.focusController?.focusedElement as Button;
            // Keep the focus on the table across refreshes; the first list moves it from Create room to the table.
            var refocus = keepFocus && focused != null && (rows.Contains(focused) || (!listed && focused == Q<Button>("create")));
            list.Clear();
            rows.Clear();
            var sorted = new List<LobbySummary>(lobbies);
            sorted.Sort((a, b) =>
            {
                var c = (a.mode == RoomMode.BattleSet).CompareTo(b.mode == RoomMode.BattleSet);
                if (c == 0) c = SetOrder(a).CompareTo(SetOrder(b));
                if (c == 0) c = b.anticheat.CompareTo(a.anticheat);
                return c != 0 ? c : a.instance.CompareTo(b.instance);
            });
            var i = 0;
            foreach (var l in sorted)
            {
                var row = UiTree.Spawn<Button>(instanceTemplate, list);
                if (row == null) break;
                FillRow(row, l);
                row.SetTransitionDelay(40 + 20 * i++);
                rows.Add(row);
            }
            if (rows.Count == 0)
            {
                var none = new Label(noLobbiesText);
                none.AddToClassList("onl-none");
                list.Add(none);
            }
            SetEnabled(LoggedIn && !busy);
            var target = LastRow() ?? Q<Button>("create");
            FocusTarget = target;
            if (refocus) FocusLater(target);
            listed |= rows.Count > 0;
        }

        // Position of a lobby's mod set in the category list (equal sets of races and battles share it).
        static int SetOrder(LobbySummary l)
        {
            var name = OnlineUi.SetName(l.packs);
            for (var i = 0; i < LobbyCategories.All.Count; i++)
                if (OnlineUi.SetName(LobbyCategories.All[i].packs) == name) return i;
            return LobbyCategories.All.Count;
        }

        void FillRow(Button row, LobbySummary l)
        {
            row.userData = l;
            var battle = l.mode == RoomMode.BattleSet;
            var mode = row.Q("row-mode");
            mode.SetIcon(battle ? "swords" : "sports_score");
            mode.tooltip = battle ? "Battles" : "Races";
            row.SetText("row-instance", "#" + l.instance.ToString(CultureInfo.InvariantCulture));
            row.SetText("row-set", OnlineUi.SetName(l.packs));
            row.Q("row-set").tooltip = OnlineUi.SetTooltip(l.packs);
            OnlineUi.FillLogos(row.Q("row-logos"), l.packs);
            var ac = row.Q("row-ac");
            ac.SetIcon(l.anticheat ? "shield" : "remove_moderator");
            ac.EnableInClassList("off", !l.anticheat);
            ac.tooltip = l.anticheat ? "Anticheat: players' games check each other's karts, cheaters are removed from the race."
                : "No anticheat: nobody checks the karts.";
            row.SetText("row-engine", OnlineUi.EngineClassName(l.engineClass));
            row.SetText("row-players", l.players.ToString(CultureInfo.InvariantCulture) + "/" + l.maxPlayers.ToString(CultureInfo.InvariantCulture));
            row.Q("row-fill").style.width = Length.Percent(l.maxPlayers > 0 ? 100f * Mathf.Clamp01((float)l.players / l.maxPlayers) : 0f);
            var full = l.players >= l.maxPlayers;
            row.EnableInClassList("full", full);
            var (icon, phase) = PhaseOf(l.phase);
            if (l.setRace > 0 && l.phase is LobbyPhase.Loading or LobbyPhase.Countdown or LobbyPhase.Racing or LobbyPhase.Results)
                phase += string.Format(CultureInfo.InvariantCulture, " \u00b7 race {0}/{1}", l.setRace, l.setSize);
            row.Q("row-phase-icon").SetIcon(icon);
            row.SetText("row-phase", phase);
            row.SetText("row-join", full ? "Full" : "Join");
            row.tooltip = OnlineLobby.ContentProblem(l.packs) ?? "";
            row.RegisterCallback<FocusInEvent>(_ => focusedLobby = l.lobbyId);
            row.clicked += () =>
            {
                Click();
                Join(() => OnlineLobby.Join(l));
            };
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
            FillRows(true);
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
