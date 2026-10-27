using System;
using System.Collections.Generic;
using System.Globalization;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>Player-visible texts of the player card and the rank tags (<see cref="PlayerCard"/>, <see cref="RankTags"/>).</summary>
    [Serializable]
    public sealed class PlayerCardTexts
    {
        [Tooltip("Rank tag next to a name and the card's rank ({0} = rank).")]
        public string rankFormat = "#{0}";
        [Tooltip("Tooltip of a rank tag ({0} = rank, {1} = score).")]
        public string rankTipFormat = "Rank {0} with {1} points";
        [Tooltip("Card rank of a player without a rank.")]
        public string unrankedText = "Unranked";
        [Tooltip("Card score ({0} = points) and its tooltip.")]
        public string scoreFormat = "{0} points";
        [Tooltip("Tooltip of the card's rank and score.")]
        public string scoreTip = "Ranking: points from online races";
        [Tooltip("Moderator badge.")]
        public string moderatorText = "Moderator";
        [Tooltip("Last active: while the player is logged in, never, and the chip tooltip.")]
        public string onlineNowText = "Online now", neverActiveText = "Never", lastActiveTip = "Last active";
        [Tooltip("Tooltip of the registration date.")]
        public string registeredTip = "Registered";
        [Tooltip("Section headings: online races, time trials, moderation.")]
        public string racesHeading = "Online races", timeTrialsHeading = "Time trials", moderationHeading = "Moderation";
        [Tooltip("Tooltips of the online race chips: races, wins, podiums.")]
        public string racesTip = "Online races raced", winsTip = "Wins", podiumsTip = "Podiums (top three)";
        [Tooltip("Tooltips of the time trial chips: runs, leaderboards, best rank.")]
        public string ttRunsTip = "Runs on the leaderboards", ttBoardsTip = "Leaderboards with a run", ttBestTip = "Best leaderboard rank";
        [Tooltip("Moderators: the disabled chip and its tooltip ({0} = reason).")]
        public string disabledText = "Disabled", disabledTipFormat = "Disabled: {0}";
        [Tooltip("Moderators: tooltips of cheat kicks, content strikes, hidden runs, reported runs.")]
        public string cheatKicksTip = "Kicked from races for cheating", contentStrikesTip = "Content strikes (other game content in anticheat lobbies)",
            hiddenRunsTip = "Hidden runs", flaggedRunsTip = "Runs other players reported";
        [Tooltip("While the profile loads.")]
        public string loadingText = "Loading";
        [Tooltip("Actions: kick from the lobby, the player's time trial runs, disable / enable the account (moderators).")]
        public string kickText = "Kick", runsText = "Time trial runs", disableText = "Disable", enableText = "Enable";
        [Tooltip("Date format of the card (.NET custom format).")]
        public string dateFormat = "d MMM yyyy";
    }

    /// <summary>What a screen offers on its player cards besides the profile.</summary>
    public sealed class PlayerCardOptions
    {
        /// <summary>Menu whose player runs page 'Time trial runs' opens; null leaves the link out (lobbies).</summary>
        public MenuFlow runsFlow;
        /// <summary>Status line of the screen for the moderator actions (text, good).</summary>
        public Action<string, bool> report;
        /// <summary>After a moderator changed the account.</summary>
        public Action changed;
    }

    /// <summary>
    /// The mini-profile next to a player's name (one at a time): name, country, moderator badge, rank and score, online
    /// races, registration, last activity and time trials (<see cref="OnlineProfiles.Get"/>), for moderators the
    /// account's state with Disable / Enable (<see cref="ModerationPrompts.ToggleAccount"/>); Kick where the screen
    /// allows it and 'Time trial runs' outside lobbies (<see cref="PlayerCardOptions"/>). Hovering the name with a mouse
    /// or focusing it shows the card (moving onto the card keeps it), clicking or tapping the name or pressing Submit on
    /// it pins it with the focus on its actions; Back, a tap outside, scrolling or leaving the screen close it. Styles:
    /// MHKOnline.uss (<c>.pcard</c>, <c>.player-name</c>, <c>.player-rank</c>).
    /// </summary>
    public static class PlayerCard
    {
        const long HoverDelayMs = 350, FocusDelayMs = 450, HideDelayMs = 300;
        // Focus opens the card only this soon after a keyboard/gamepad move, so a screen focusing its first row does not.
        const float NavigatedSeconds = 1f;
        const float Gap = 6f, EdgeMargin = 12f;
        const string NameClass = "player-name", PanelClass = "pcard", ShownClass = "shown";

        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly PlayerCardTexts DefaultTexts = new();

        sealed class Target
        {
            public VisualElement trigger, anchor;
            public long accountId;
            public string username;
            public PlayerCardOptions options;
            public Action<VisualElement> kick;
        }

        sealed class Shown
        {
            public Target target;
            public VisualElement host, panel;
            public PlayerCardTexts texts;
            public readonly List<Button> buttons = new();
            public PlayerProfile profile;
            public string error;
            /// <summary>The profile came from GetProfile (not only the cached summary).</summary>
            public bool loaded, pinned;
        }

        [AutoStaticsCleanup] static Shown shown;
        [AutoStaticsCleanup] static IVisualElementScheduledItem later;
        [AutoStaticsCleanup] static Target laterFor, refocused;
        [AutoStaticsCleanup] static float navigatedAt = float.NegativeInfinity;
        [AutoStaticsCleanup] static readonly HashSet<IPanel> watchedPanels = new();

        /// <summary>The card texts: the Players screen's (also while it is closed), else the defaults.</summary>
        public static PlayerCardTexts Texts
        {
            get
            {
                var screen = UnityEngine.Object.FindAnyObjectByType<PlayersScreen>(FindObjectsInactive.Include);
                return screen != null && screen.CardTexts != null ? screen.CardTexts : DefaultTexts;
            }
        }

        /// <summary>
        /// The name element <paramref name="name"/> opens the card of <paramref name="accountId"/>. A name outside a
        /// button takes the focus itself; a name inside one (leaderboard rows) gets hover and tap only, a separate
        /// trigger (<see cref="Attach(VisualElement, VisualElement, long, string, PlayerCardOptions, Action{VisualElement})"/>)
        /// serves the gamepad. <paramref name="kick"/> (null = none) removes the player from the lobby.
        /// </summary>
        public static void Attach(VisualElement name, long accountId, string username, PlayerCardOptions options, Action<VisualElement> kick = null) =>
            Attach(name, name, accountId, username, options, kick);

        /// <summary>
        /// <paramref name="trigger"/> (the name, or a row's button) opens the card of <paramref name="accountId"/> placed
        /// at <paramref name="anchor"/> (the name). A button trigger pins it on click.
        /// </summary>
        public static void Attach(VisualElement trigger, VisualElement anchor, long accountId, string username, PlayerCardOptions options,
            Action<VisualElement> kick = null)
        {
            if (trigger == null || anchor == null || accountId <= 0) return;
            var t = new Target { trigger = trigger, anchor = anchor, accountId = accountId, username = username ?? "", options = options, kick = kick };
            if (trigger == anchor) anchor.AddToClassList(NameClass);
            if (trigger is Button button) button.clicked += () => Toggle(t);
            else
            {
                // Names inside a row's button leave the focus to the button (spatial navigation skips nested targets).
                trigger.focusable = !InsideButton(trigger);
                trigger.pickingMode = PickingMode.Position;
                trigger.RegisterCallback<PointerDownEvent>(e => OnPointerDown(t, e));
                trigger.RegisterCallback<NavigationSubmitEvent>(e =>
                {
                    e.StopPropagation();
                    Open(t, true, true);
                });
            }
            trigger.RegisterCallback<PointerEnterEvent>(e =>
            {
                if (e.pointerType == UnityEngine.UIElements.PointerType.mouse && !IsShownFor(t)) Schedule(t, () => Open(t, false, false), HoverDelayMs);
                else if (IsShownFor(t)) CancelLater();
            });
            trigger.RegisterCallback<PointerLeaveEvent>(_ => OnLeave(t));
            trigger.RegisterCallback<AttachToPanelEvent>(e => WatchNavigation(e.destinationPanel));
            if (trigger.panel != null) WatchNavigation(trigger.panel);
            trigger.RegisterCallback<FocusInEvent>(_ =>
            {
                // Back from the card gives the focus back without opening it again.
                if (refocused == t)
                {
                    refocused = null;
                    return;
                }
                if (!IsShownFor(t) && Time.unscaledTime - navigatedAt < NavigatedSeconds) Schedule(t, () => Open(t, false, false), FocusDelayMs);
            });
            trigger.RegisterCallback<FocusOutEvent>(e => OnFocusOut(t, e));
            anchor.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (IsShownFor(t)) Place(shown);
            });
            // A removed name closes its card on the next frame unless the refilled list attached the player again.
            anchor.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (!IsShownFor(t)) return;
                var s = shown;
                s.host.schedule.Execute(() =>
                {
                    if (shown == s && s.target == t) Close(false);
                });
            });
            // A list refilled under an open card (lobby updates): the card moves to the new name.
            if (shown != null && shown.target.accountId == accountId && shown.target.anchor.panel == null && ScreenOf(anchor) == shown.host)
            {
                shown.target = t;
                Render(shown);
            }
        }

        // Remembers when the panel last saw a keyboard/gamepad move (each panel once).
        static void WatchNavigation(IPanel panel)
        {
            if (panel == null || !watchedPanels.Add(panel)) return;
            panel.visualTree.RegisterCallback<NavigationMoveEvent>(_ => navigatedAt = Time.unscaledTime, TrickleDown.TrickleDown);
        }

        /// <summary>Closes the open card, if any.</summary>
        public static void Close() => Close(false);

        static bool InsideButton(VisualElement e)
        {
            for (var p = e.parent; p != null; p = p.parent)
                if (p is Button) return true;
            return false;
        }

        static bool IsShownFor(Target t) => shown != null && shown.target == t;

        static void Schedule(Target t, Action action, long delayMs)
        {
            CancelLater();
            laterFor = t;
            later = t.trigger.schedule.Execute(() =>
            {
                laterFor = null;
                action();
            });
            later.ExecuteLater(delayMs);
        }

        static void CancelLater()
        {
            later?.Pause();
            later = null;
            laterFor = null;
        }

        static void OnPointerDown(Target t, PointerDownEvent e)
        {
            if (e.button != 0) return;
            var touch = e.pointerType != UnityEngine.UIElements.PointerType.mouse;
            // A tap opens the card instead of the row's action; a mouse click on a name inside a row keeps the row's.
            if (!touch && InsideButton(t.trigger)) return;
            e.StopPropagation();
            Toggle(t);
        }

        static void Toggle(Target t)
        {
            if (IsShownFor(t) && shown.pinned) Close(true);
            else Open(t, true, true);
        }

        static void OnLeave(Target t)
        {
            if (laterFor == t) CancelLater();
            if (IsShownFor(t) && !shown.pinned) Schedule(t, () => Close(false), HideDelayMs);
        }

        static void OnFocusOut(Target t, FocusOutEvent e)
        {
            if (laterFor == t) CancelLater();
            if (!IsShownFor(t)) return;
            if (e.relatedTarget is VisualElement next && shown.panel.Contains(next)) return;
            Close(false);
        }

        // Shows the card of t (or moves the open one of the same player to it); pinned cards stay until closed.
        static void Open(Target t, bool pin, bool focusCard)
        {
            CancelLater();
            var host = ScreenOf(t.anchor);
            if (host == null) return;
            if (shown != null && shown.target.accountId == t.accountId && shown.host == host)
            {
                shown.target = t;
                shown.pinned |= pin;
                Render(shown);
            }
            else
            {
                Close(false);
                var panel = new VisualElement { focusable = false };
                panel.AddToClassList("panel");
                panel.AddToClassList(PanelClass);
                shown = new Shown { target = t, host = host, panel = panel, texts = Texts, pinned = pin };
                var s = shown;
                panel.RegisterCallback<PointerEnterEvent>(_ => CancelLater());
                panel.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    if (shown == s && !s.pinned) Schedule(s.target, () => Close(false), HideDelayMs);
                });
                panel.RegisterCallback<NavigationMoveEvent>(e => OnNavigate(s, e));
                panel.RegisterCallback<NavigationCancelEvent>(e =>
                {
                    e.StopPropagation();
                    Close(true);
                });
                panel.RegisterCallback<GeometryChangedEvent>(_ => Place(s));
                // The screen closed under the card.
                panel.RegisterCallback<DetachFromPanelEvent>(_ =>
                {
                    if (shown == s) Close(false);
                });
                host.RegisterCallback<PointerDownEvent>(OnHostPointerDown, TrickleDown.TrickleDown);
                host.RegisterCallback<WheelEvent>(OnHostWheel, TrickleDown.TrickleDown);
                host.Add(panel);
                if (OnlineProfiles.TryGetSummary(t.accountId, out var summary)) s.profile = new PlayerProfile { player = summary };
                Render(s);
                LoadAsync(s, t.accountId).LogExceptionsAndForget();
            }
            if (focusCard && shown.buttons.Count > 0) shown.buttons[0].Focus();
        }

        static void Close(bool refocus)
        {
            CancelLater();
            var s = shown;
            if (s == null) return;
            shown = null;
            s.host.UnregisterCallback<PointerDownEvent>(OnHostPointerDown, TrickleDown.TrickleDown);
            s.host.UnregisterCallback<WheelEvent>(OnHostWheel, TrickleDown.TrickleDown);
            s.panel.RemoveFromHierarchy();
            var trigger = s.target.trigger;
            if (!refocus || trigger.panel == null || !trigger.canGrabFocus || trigger.focusController?.focusedElement == trigger) return;
            refocused = s.target;
            trigger.Focus();
        }

        // A tap anywhere but on the card or its name closes it (the tap still acts).
        static void OnHostPointerDown(PointerDownEvent e)
        {
            if (shown == null || e.target is not VisualElement v) return;
            if (shown.panel.Contains(v) || shown.target.trigger.Contains(v)) return;
            Close(false);
        }

        // Scrolling the list moves the name away from the card.
        static void OnHostWheel(WheelEvent e)
        {
            if (shown != null && !(e.target is VisualElement v && shown.panel.Contains(v))) Close(false);
        }

        // The card keeps the gamepad: the arrows walk its buttons.
        static void OnNavigate(Shown s, NavigationMoveEvent e)
        {
            if (s.buttons.Count == 0 || e.target is not VisualElement from) return;
            var at = from is Button b ? s.buttons.IndexOf(b) : -1;
            var step = e.direction is NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Left or NavigationMoveEvent.Direction.Previous ? -1 : 1;
            s.buttons[Mathf.Clamp(at < 0 ? 0 : at + step, 0, s.buttons.Count - 1)].Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        static async Awaitable LoadAsync(Shown s, long accountId)
        {
            var r = await OnlineProfiles.Get(accountId);
            if (shown != s || s.target.accountId != accountId) return;
            if (r.Ok)
            {
                s.profile = r.value;
                s.loaded = true;
            }
            else s.error = r.result.message;
            Render(s);
        }

        // Fills the card from what is known: the name at once, the summary from the cache, the profile once it arrived.
        static void Render(Shown s)
        {
            var t = s.target;
            var x = s.texts;
            var p = s.profile;
            var summary = p?.player;
            // A re-render keeps the focus on the card's actions.
            var focusedInside = s.panel.focusController?.focusedElement is VisualElement f && s.panel.Contains(f);
            s.panel.Clear();
            s.buttons.Clear();
            var head = Box(s.panel, "pcard-head");
            var name = new Label(summary != null && summary.username.Length > 0 ? summary.username : t.username);
            name.AddToClassList("pcard-name");
            head.Add(name);
            var badges = Box(head, "info-chips");
            badges.AddToClassList("pcard-badges");
            var country = summary?.country;
            if (!string.IsNullOrEmpty(country)) InfoChips.Add(badges, null, country.ToUpperInvariant(), Countries.Name(country));
            if (summary != null && summary.IsModerator) InfoChips.Add(badges, "shield_person", x.moderatorText, null, InfoChipState.On);
            if (summary != null)
            {
                var rank = Box(s.panel, "pcard-rank-row");
                rank.tooltip = x.scoreTip;
                rank.Add(Text(summary.rank > 0 ? string.Format(Invariant, x.rankFormat, summary.rank) : x.unrankedText, "pcard-rank"));
                rank.Add(Text(string.Format(Invariant, x.scoreFormat, summary.score), "pcard-score"));
                var dates = Chips(s.panel);
                InfoChips.Add(dates, "person_add", Date(summary.created, x), x.registeredTip);
                InfoChips.Add(dates, "history", summary.online ? x.onlineNowText : summary.lastActive == default ? x.neverActiveText : Date(summary.lastActive, x),
                    x.lastActiveTip, summary.online ? InfoChipState.Good : InfoChipState.Normal);
            }
            // A profile built from the cached summary alone has no race or time trial numbers yet.
            var full = s.loaded;
            if (full)
            {
                Heading(s.panel, x.racesHeading);
                var races = Chips(s.panel);
                InfoChips.Add(races, "sports_score", Count(p.races), x.racesTip);
                InfoChips.Add(races, "emoji_events", Count(p.wins), x.winsTip, p.wins > 0 ? InfoChipState.On : InfoChipState.Normal);
                InfoChips.Add(races, "military_tech", Count(p.podiums), x.podiumsTip);
                Heading(s.panel, x.timeTrialsHeading);
                var tt = Chips(s.panel);
                InfoChips.Add(tt, "timer", Count(p.ttRuns), x.ttRunsTip);
                InfoChips.Add(tt, "map", Count(p.ttBoards), x.ttBoardsTip);
                InfoChips.Add(tt, "leaderboard", p.ttBestRank > 0 ? string.Format(Invariant, x.rankFormat, p.ttBestRank) : "-", x.ttBestTip,
                    p.ttBestRank is > 0 and <= 3 ? InfoChipState.On : InfoChipState.Normal);
                if (OnlineService.IsModerator)
                {
                    Heading(s.panel, x.moderationHeading);
                    var mod = Chips(s.panel);
                    if (p.disabled) InfoChips.Add(mod, "gavel", x.disabledText, string.Format(x.disabledTipFormat, p.disabledReason), InfoChipState.Bad);
                    InfoChips.Add(mod, "gavel", Count(p.cheatKicks), x.cheatKicksTip, p.cheatKicks > 0 ? InfoChipState.Bad : InfoChipState.Off);
                    InfoChips.Add(mod, "extension", Count(p.contentStrikes), x.contentStrikesTip, p.contentStrikes > 0 ? InfoChipState.Bad : InfoChipState.Off);
                    InfoChips.Add(mod, "visibility_off", Count(p.hiddenRuns), x.hiddenRunsTip, p.hiddenRuns > 0 ? InfoChipState.Normal : InfoChipState.Off);
                    InfoChips.Add(mod, "flag", Count(p.flaggedRuns), x.flaggedRunsTip, p.flaggedRuns > 0 ? InfoChipState.Bad : InfoChipState.Off);
                    if (p.disabled && !string.IsNullOrEmpty(p.disabledReason)) s.panel.Add(Text(p.disabledReason, "pcard-note"));
                }
            }
            else s.panel.Add(Text(s.error ?? x.loadingText, "pcard-note"));
            AddActions(s, full);
            if (focusedInside && s.buttons.Count > 0) s.buttons[0].Focus();
            Place(s);
        }

        static void AddActions(Shown s, bool full)
        {
            var t = s.target;
            var x = s.texts;
            var me = OnlineService.Account;
            var self = me != null && (long)me.id == t.accountId;
            var row = Box(s.panel, "pcard-actions");
            if (t.kick != null && !self) AddButton(s, row, "close", x.kickText, "danger", () => t.kick(t.anchor));
            if (t.options?.runsFlow != null)
                AddButton(s, row, "timer", x.runsText, "secondary", () => t.options.runsFlow.Get<PlayerRunsScreen>()?.Open(t.accountId, Name(s)));
            var p = s.profile;
            if (full && OnlineService.IsModerator && !self && !(p.player.IsModerator && !p.disabled))
                AddButton(s, row, "gavel", p.disabled ? x.enableText : x.disableText, "danger", () =>
                    ModerationPrompts.ToggleAccount(t.anchor, t.accountId, Name(s), t.options?.report ?? NoReport, () =>
                    {
                        OnlineProfiles.Invalidate(t.accountId);
                        t.options?.changed?.Invoke();
                    }));
            row.SetVisible(row.childCount > 0);
        }

        static void NoReport(string text, bool good) { }

        static string Name(Shown s) => s.profile?.player != null && s.profile.player.username.Length > 0 ? s.profile.player.username : s.target.username;

        // Card buttons close the card first: their prompts open over the screen, anchored at the name.
        static void AddButton(Shown s, VisualElement row, string icon, string text, string kind, Action action)
        {
            var b = new Button();
            b.AddToClassList("mhk-button");
            b.AddToClassList("small");
            b.AddToClassList(kind);
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.AddToClassList("icon");
            i.SetIcon(icon);
            b.Add(i);
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("button-label");
            b.Add(l);
            b.clicked += () =>
            {
                UnityEngine.Object.FindAnyObjectByType<MenuFlow>()?.Click();
                Close(false);
                action();
            };
            row.Add(b);
            s.buttons.Add(b);
        }

        // Below the name, left-aligned, else above it; inside the screen.
        static void Place(Shown s)
        {
            var size = s.panel.layout.size;
            if (shown != s || s.target.anchor.panel == null || float.IsNaN(size.x) || size.x <= 0f) return;
            var a = s.host.WorldToLocal(s.target.anchor.worldBound);
            var area = s.host.layout.size;
            var x = Mathf.Clamp(a.xMin, EdgeMargin, Mathf.Max(EdgeMargin, area.x - size.x - EdgeMargin));
            var y = a.yMax + Gap;
            if (y + size.y > area.y - EdgeMargin) y = Mathf.Max(EdgeMargin, a.yMin - Gap - size.y);
            s.panel.style.left = x;
            s.panel.style.top = y;
            s.panel.AddToClassList(ShownClass);
        }

        // The screen root the card floats over: the topmost element below the panel's root.
        static VisualElement ScreenOf(VisualElement from)
        {
            var top = from?.panel?.visualTree;
            if (top == null) return null;
            var e = from;
            while (e.parent != null && e.parent != top) e = e.parent;
            return e;
        }

        static VisualElement Box(VisualElement parent, string cls)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList(cls);
            parent.Add(e);
            return e;
        }

        static VisualElement Chips(VisualElement parent)
        {
            var e = Box(parent, InfoChips.ContainerClass);
            e.AddToClassList("pcard-chips");
            return e;
        }

        static void Heading(VisualElement parent, string text) => parent.Add(Text(text, "pcard-heading"));

        static Label Text(string text, string cls)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            return l;
        }

        static string Count(int n) => n.ToString(Invariant);

        static string Date(DateTime utc, PlayerCardTexts x) => utc == default ? "-" : utc.ToLocalTime().ToString(x.dateFormat, Invariant);
    }
}
