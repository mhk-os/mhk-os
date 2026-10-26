using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Online play (Online.uxml): while logged out a Log in button (the Account page); logged in the ten public lobby
    /// categories grouped into races and battles (packs, anticheat, players) with Quick join, the lobbies of the selected
    /// category with Join, joining a private room by its code, and Create room. Content warnings (packs that differ from
    /// other players'), refusals and why the player last left a lobby show in the status line. A room link
    /// (<see cref="JoinLinks"/>) opens this page, which joins the room once logged in.
    /// </summary>
    public class OnlineScreen : MenuScreen
    {
        [SerializeField, Tooltip("Category row (Templates/OnlineCategoryRow.uxml).")]
        VisualTreeAsset categoryTemplate;
        [SerializeField, Tooltip("Lobby row of the side panel (Templates/OnlineInstanceRow.uxml).")]
        VisualTreeAsset instanceTemplate;
        [SerializeField, Tooltip("Seconds between list refreshes while the page is open.")]
        float refreshSeconds = 5f;

        [Header("Texts")]
        [SerializeField] string racesGroup = "RACES", battlesGroup = "BATTLES";
        [SerializeField] string loggedOutText = "Log in to race other players online.";
        [SerializeField, Tooltip("{0} = room code.")] string linkLoginFormat = "Log in to join room {0}.";
        [SerializeField, Tooltip("{0} = room code.")] string linkJoiningFormat = "Joining room {0}...";
        [SerializeField] string loginText = "Log in";
        [SerializeField] string announcingText = "Sending your content to the server...";
        [SerializeField, Tooltip("{0} = players, {1} = lobbies.")] string playersFormat = "{0} in {1}";
        [SerializeField] string emptyText = "Empty";
        [SerializeField, Tooltip("{0} = number.")] string instanceFormat = "Lobby #{0}";
        [SerializeField] string noInstancesText = "No lobby yet: Quick join opens one.";
        [SerializeField, Tooltip("Side panel note of public lobbies.")]
        string publicRulesText = "150cc, each track's laps, standard items. Sets of 4 races with points; vote for every track.";
        [SerializeField] string kickedByOwnerText = "The room owner removed you.", kickedByModText = "A moderator removed you.";
        [SerializeField] string closedText = "The lobby closed.";

        // Category row and lobby list of the last refresh.
        readonly Dictionary<int, VisualElement> categoryRows = new();
        List<LobbySummary> lobbies = new();
        int selected = 1;
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

        /// <summary>"Race set", "Battle set" or the championship's cup.</summary>
        public static string ModeName(RoomSettings s) => s.mode switch
        {
            RoomMode.BattleSet => "Battles",
            RoomMode.Championship => "Championship: " + ContentDisplayCup(s.cupId),
            _ => "Races",
        };

        static string ContentDisplayCup(string cupId)
        {
            var cup = GameRoot.Instance.Content.Get<Modding.CupDefinition>(cupId);
            return cup != null ? cup.DisplayName : cupId;
        }

        /// <summary>The games of <paramref name="packs"/>, comma separated.</summary>
        public static string PackNames(IReadOnlyList<string> packs)
        {
            var names = new List<string>();
            if (packs != null)
                foreach (var p in packs) names.Add(UiText.GameName(p));
            return string.Join(", ", names);
        }

        static string CategoryName(LobbyCategory c) => c.packs.Length switch
        {
            1 => UiText.GameName(c.packs[0]) + " only",
            4 => "The four games",
            _ => "The four games + " + UiText.GameName(c.packs[c.packs.Length - 1]),
        };

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
            code.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;
                e.StopPropagation();
                JoinCode();
            }, TrickleDown.TrickleDown);
            LeaderRows.KeepFocusedRowInView(Q<ScrollView>("cat-scroll"), Q<VisualElement>("cat-list"));
            OnlineService.Changed += ShowState;
            OnlineLobby.Changed += ShowState;
            OnlineService.Connect();
            FillCategories();
            SetStatus(null, false);
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
            categoryRows.Clear();
            statusLabel = null;
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
            SetStatus(string.Join("\n", lines) + " Anticheat lobbies with it are closed to you.", false);
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

        void FillCategories()
        {
            var list = Q<VisualElement>("cat-list");
            list.Clear();
            categoryRows.Clear();
            var group = (RoomMode)255;
            var i = 0;
            VisualElement first = null;
            foreach (var c in LobbyCategories.All)
            {
                var battle = c.Battle;
                var g = battle ? RoomMode.BattleSet : RoomMode.RaceSet;
                if (g != group)
                {
                    group = g;
                    var title = new Label(battle ? battlesGroup : racesGroup);
                    title.AddToClassList("onl-group-title");
                    list.Add(title);
                }
                var row = UiTree.Spawn<VisualElement>(categoryTemplate, list);
                if (row == null) break;
                categoryRows[c.id] = row;
                row.EnableInClassList("alt", i++ % 2 == 1);
                row.SetText("cat-name", CategoryName(c));
                row.SetText("cat-packs", string.Join(UiText.Separator, Array.ConvertAll(c.packs, UiText.GameName)));
                row.Q("cat-anticheat").SetVisible(c.anticheat);
                row.Q("cat-no-anticheat").SetVisible(!c.anticheat);
                var problem = OnlineLobby.ContentProblem(c.packs);
                var category = c;
                var main = row.Q<Button>("lb-main");
                main.clicked += () =>
                {
                    Click();
                    Select(category.id);
                };
                main.RegisterCallback<FocusInEvent>(_ => Select(category.id));
                var quick = row.Q<Button>("cat-quick");
                quick.SetEnabled(problem == null);
                if (problem != null) quick.tooltip = problem;
                quick.clicked += () =>
                {
                    Click();
                    Join(() => OnlineLobby.QuickJoin(category.id));
                };
                first ??= main;
            }
            FocusTarget = first;
            Select(selected);
        }

        void Select(int category)
        {
            selected = category;
            foreach (var pair in categoryRows) pair.Value.EnableInClassList("selected", pair.Key == category);
            ShowCategory();
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
            foreach (var pair in categoryRows)
            {
                int players = 0, count = 0;
                foreach (var l in lobbies)
                {
                    if (l.category != pair.Key) continue;
                    players += l.players;
                    count++;
                }
                pair.Value.SetText("cat-players", players > 0 ? string.Format(playersFormat, players, count == 1 ? "1 lobby" : count + " lobbies") : emptyText);
            }
            ShowCategory();
        }

        void ShowCategory()
        {
            if (statusLabel == null || !LobbyCategories.TryGet(selected, out var c)) return;
            Q<Label>("side-title").text = (c.Battle ? "Battles: " : "Races: ") + CategoryName(c);
            var problem = OnlineLobby.ContentProblem(c.packs);
            Q<Label>("side-sub").text = (c.anticheat ? "Anticheat on. " : "Anticheat off. ") + publicRulesText + (problem != null ? "\n" + problem : "");
            var list = Q<VisualElement>("inst-list");
            list.Clear();
            var i = 0;
            foreach (var l in lobbies)
            {
                if (l.category != selected) continue;
                var row = UiTree.Spawn<VisualElement>(instanceTemplate, list);
                if (row == null) break;
                row.EnableInClassList("alt", i++ % 2 == 1);
                row.SetText("inst-name", string.Format(instanceFormat, l.instance));
                row.SetText("inst-players", l.players.ToString(CultureInfo.InvariantCulture) + " / " + l.maxPlayers.ToString(CultureInfo.InvariantCulture));
                row.SetText("inst-phase", PhaseName(l.phase));
                var main = row.Q<Button>("lb-main");
                main.SetEnabled(problem == null && l.players < l.maxPlayers);
                var lobby = l;
                main.clicked += () =>
                {
                    Click();
                    Join(() => OnlineLobby.Join(lobby));
                };
            }
            if (i == 0)
            {
                var none = new Label(noInstancesText);
                none.AddToClassList("onl-side-sub");
                list.Add(none);
            }
        }

        static string PhaseName(LobbyPhase phase) => phase switch
        {
            LobbyPhase.Waiting => "Waiting",
            LobbyPhase.Voting => "Voting",
            LobbyPhase.Loading => "Loading",
            LobbyPhase.Countdown => "Starting",
            LobbyPhase.Racing => "Racing",
            LobbyPhase.Results => "Results",
            _ => "Set results",
        };

        void CreateRoom()
        {
            if (!LoggedIn) return;
            Flow.Get<RoomSettingsScreen>().Open();
        }

        void JoinCode()
        {
            var text = Q<TextField>("code").value;
            if (string.IsNullOrWhiteSpace(text)) return;
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
