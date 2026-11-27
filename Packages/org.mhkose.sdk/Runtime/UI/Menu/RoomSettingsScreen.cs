using System;
using System.Collections.Generic;
using System.Globalization;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using MHK.Powerups;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Settings of a private room (RoomSettings.uxml), creating one (from the Online page, starting from the last room's
    /// settings) or the owner changing them while the room waits. Name, mode (segmented tabs for races, battles, Last Man
    /// Standing on race tracks, and championships of a cup or of the owner's own track list, edited on the Track list
    /// page), the mods as toggle cards (any set of the loaded content packs, third-party mods included, at least one must
    /// bring tracks or arenas for the mode), and the rules as <see cref="RuleRows"/> in the look of the race settings.
    /// Switches for anticheat, end at the line, items and the ghosts' powerups, value pills for engine class, finish wait,
    /// item odds (standard, the room's own, or the player's own odds limited to the room's powerups), the track pick and
    /// the championship's cup, number inputs for laps, players, AI (fills free places up to its count) and races per set.
    /// Rows that do not apply to the mode are left out. A chip row sums the room up.
    /// </summary>
    public class RoomSettingsScreen : MenuScreen
    {
        [Header("Texts")]
        [SerializeField] string createTitle = "Create room", editTitle = "Room settings";
        [SerializeField] string createLabel = "Create room", saveLabel = "Save";
        [SerializeField, Tooltip("Laps value when every track's own lap count is used (it fits the number field).")] string trackLapsText = "Track";
        [SerializeField] string defaultOddsText = "Standard";
        [SerializeField, Tooltip("Item odds value with the player's own odds (Powerups screen).")] string ownOddsText = "My odds";
        [SerializeField, Tooltip("Item odds value while the room keeps the odds it already has.")] string roomOddsText = "This room's odds";
        [SerializeField] string noTracksText = "Choose at least one mod with tracks.";
        [SerializeField] string noArenasText = "Choose at least one mod with arenas.";
        [SerializeField] string noCupText = "The chosen mods have no cup.";
        [SerializeField] string noTrackListText = "Choose the championship's tracks.";
        [SerializeField, Tooltip("Title of the Track list page for the championship.")] string trackListTitle = "Championship tracks";
        [SerializeField, Tooltip("Confirm button of the Track list page.")] string trackListDone = "Done";
        [SerializeField, Tooltip("Range hint of the number inputs. {0} = min, {1} = max.")] string rangeFormat = "{0} to {1}";
        [SerializeField, Tooltip("Range hint of the Laps input ({1} = most, 0 is every track's own count).")] string lapsRangeFormat = "1 to {1}";
        [SerializeField, Tooltip("{0} = count.")] string tracksFormat = "{0} tracks", arenasFormat = "{0} arenas";

        // The mode tabs in their order on the page (the wire values differ).
        static readonly RoomMode[] Modes = { RoomMode.RaceSet, RoomMode.BattleSet, RoomMode.LastManStandingSet, RoomMode.Championship };
        // Finish wait values (seconds) of its dropdown, within RoomSettings.MinFinishWait and MaxFinishWait.
        static readonly int[] FinishWaits = { 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300 };

        RoomSettings settings;
        bool editing;
        // Item odds choice: null = default odds, KeepRoomOdds = the odds the room (or the last room) already has,
        // OwnOdds = the player's own odds. Saving rebuilds the odds only when the player chose another value.
        const string KeepRoomOdds = "\u0001room", OwnOdds = "\u0001own";
        string oddsChoice;
        // The odds the room had when the screen opened (KeepRoomOdds).
        List<PowerupOdds> keptOdds = new();
        VisualElement modGrid, summary;
        RuleRows rules;
        Label statusLabel;
        // Championship over the room's own track list (settings.trackIds) instead of a cup.
        bool ownTracks;
        // The AI row: its input is bound again when the room size changes (its maximum is the size - 1).
        VisualElement aiRow;

        /// <summary>Opens the screen to create a room from the last room's settings.</summary>
        public void Open()
        {
            editing = false;
            settings = Settings.lastRoom != null ? Settings.lastRoom.Clone() : new RoomSettings();
            oddsChoice = null;
            Sanitize();
            Flow.Show(this);
        }

        /// <summary>Opens the screen on the settings of the player's room (owner, while it waits).</summary>
        public void OpenEdit()
        {
            var lobby = OnlineLobby.Current;
            if (lobby == null) return;
            editing = true;
            settings = lobby.settings.Clone();
            oddsChoice = null;
            Sanitize();
            Flow.Show(this);
        }

        protected override void Bind()
        {
            settings ??= new RoomSettings();
            OnlineUi.Spatial(Root, Navigate);
            Q<Label>("title").text = editing ? editTitle : createTitle;
            Q<Label>("save-label").text = editing ? saveLabel : createLabel;
            Q<VisualElement>("save-icon").SetIcon(editing ? "check" : "add");
            OnClick("save", Save);
            var name = Q<TextField>("room-name");
            name.SetValueWithoutNotify(settings.name);
            name.textEdition.placeholder = (OnlineService.Username ?? "") + "'s room";
            name.RegisterValueChangedCallback(e =>
            {
                settings.name = e.newValue;
                ShowSummary();
            });
            modGrid = Q<VisualElement>("mod-grid");
            rules = new RuleRows(Q<VisualElement>("rule-rows"), Root, Click, rangeFormat);
            rules.Changed += ShowSummary;
            summary = Q<VisualElement>("summary");
            statusLabel = Q<Label>("room-status-label");
            KeepFocusedInView(Q<ScrollView>("mod-scroll"), modGrid);
            // The row of the focused control scrolls into view (the focus sits on the controls inside the rows).
            var ruleScroll = Q<ScrollView>("rule-scroll");
            rules.Container.RegisterCallback<FocusInEvent>(e =>
            {
                var row = rules?.RowOf(e.target as VisualElement);
                if (row != null) ruleScroll.ScrollTo(row);
            });
            SetStatus(null);
            OnlineUi.Segmented(Q<VisualElement>("mode-tabs"),
                new[]
                {
                    ("sports_score", "Races", (string)null), ("swords", "Battles", null),
                    ("person", "Last Man Standing", "Races on race tracks, the last kart of every lap is knocked out"),
                    ("emoji_events", "Championship", "The races of one cup or of your own track list"),
                },
                Mathf.Max(0, Array.IndexOf(Modes, settings.mode)), i =>
                {
                    Click();
                    settings.mode = Modes[i];
                    FixCup();
                    Changed(true);
                });
            FillMods();
            Changed(false);
            FocusTarget = Q<VisualElement>("mode-tabs").Q<Button>(className: "selected");
        }

        // The wide name field goes down to the selected mode chip (not the one under its middle). Up / Down in the rule rows
        // go row by row (RuleRows.Route), and Left from a row's first control goes to the mod card at its height.
        VisualElement Navigate(VisualElement from, NavigationMoveEvent.Direction d)
        {
            var name = Q<TextField>("room-name");
            if (from == name || name.Contains(from))
                return d == NavigationMoveEvent.Direction.Down ? OnlineUi.SelectedChip(Q<VisualElement>("mode-tabs")) : null;
            var row = rules.RowOf(from);
            if (row == null) return null;
            if (d != NavigationMoveEvent.Direction.Left) return rules.Route(from, d);
            if (RuleRows.FirstControl(row) != from) return null;
            VisualElement nearest = null;
            var y = from.worldBound.center.y;
            foreach (var card in modGrid.Children())
                if (OnlineUi.CanFocus(card) && (nearest == null || Mathf.Abs(card.worldBound.center.y - y) < Mathf.Abs(nearest.worldBound.center.y - y)))
                    nearest = card;
            return nearest;
        }

        protected override void Unbind()
        {
            base.Unbind();
            modGrid = summary = null;
            rules = null;
            statusLabel = null;
            aiRow = null;
        }

        // The mode or the mods changed: the controls that depend on them, the cards' counts and the summary.
        void Changed(bool keepFocus)
        {
            ShowModCounts();
            FillRules(keepFocus);
            ShowSummary();
            SetStatus(null);
        }

        void SetStatus(string text)
        {
            if (statusLabel == null) return;
            statusLabel.text = text ?? "";
            SetVisible("room-status", !string.IsNullOrEmpty(text));
        }

        static ContentRegistry Registry => GameRoot.Instance.Content;

        // Packs a room can use: every loaded pack with gameplay content except the game's own.
        static List<string> AvailablePacks()
        {
            var list = new List<string>();
            foreach (var m in ContentManifests.Build(Registry))
                if (m.items.Count > 0 && !string.Equals(m.packId, ContentManifests.GamePackId, StringComparison.OrdinalIgnoreCase)) list.Add(m.packId);
            return list;
        }

        // Keeps only loaded packs (the four games when none is left), a known mode, a cup of them and a valid range for
        // every number.
        void Sanitize()
        {
            var available = AvailablePacks();
            settings.packs = settings.packs?.FindAll(p => available.Exists(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))) ?? new List<string>();
            if (settings.packs.Count == 0)
                foreach (var p in available)
                    if (Array.IndexOf(LobbyCategories.All[0].packs, p) >= 0) settings.packs.Add(p);
            if (settings.packs.Count == 0 && available.Count > 0) settings.packs.Add(available[0]);
            if (Array.IndexOf(Modes, settings.mode) < 0) settings.mode = RoomMode.RaceSet;
            settings.engineClass = Mathf.Clamp(settings.engineClass, 0, OnlineUi.EngineClassCount - 1);
            settings.laps = Mathf.Clamp(settings.laps, 0, LobbyLimits.Laps);
            settings.finishWait = FinishWaits[FinishWaitIndex(settings.finishWait)];
            settings.maxPlayers = Mathf.Clamp(settings.maxPlayers, LobbyLimits.MinPlayers, LobbyLimits.MaxPlayers);
            settings.maxAi = Mathf.Clamp(settings.maxAi, 0, settings.maxPlayers - 1);
            settings.trackIds ??= new List<string>();
            if (settings.trackIds.Count > LobbyLimits.TrackIds) settings.trackIds.RemoveRange(LobbyLimits.TrackIds, settings.trackIds.Count - LobbyLimits.TrackIds);
            ownTracks = settings.mode == RoomMode.Championship && string.IsNullOrEmpty(settings.cupId) && settings.trackIds.Count > 0;
            settings.trackPick = Mathf.Clamp(settings.trackPick, 0, 1);
            settings.racesPerSet = Mathf.Clamp(settings.racesPerSet, 1, LobbyLimits.MaxRacesPerSet);
            settings.name = LobbyWire.Clip(settings.name, LobbyLimits.RoomName);
            keptOdds = new List<PowerupOdds>(settings.itemOdds);
            if (keptOdds.Count > 0) oddsChoice = KeepRoomOdds;
            FixCup();
        }

        // The finish wait value nearest to seconds.
        static int FinishWaitIndex(int seconds)
        {
            var best = 0;
            for (var i = 1; i < FinishWaits.Length; i++)
                if (Mathf.Abs(FinishWaits[i] - seconds) < Mathf.Abs(FinishWaits[best] - seconds)) best = i;
            return best;
        }

        void FixCup()
        {
            if (ownTracks) return;
            var cups = Cups();
            if (!cups.Exists(c => c.Id == settings.cupId)) settings.cupId = cups.Count > 0 ? cups[0].Id : "";
        }

        // Race tracks of the room's packs, raceable arenas included. A championship's own list may hold them.
        bool IsRoomTrack(TrackDefinition t) => t != null && t.HostsRaces && ContentManifests.IsLobbyContent(Registry, t, settings.packs);

        // The own list without tracks the room's packs no longer have.
        List<string> RoomTrackIds() => settings.trackIds.FindAll(id => IsRoomTrack(Registry.Get<TrackDefinition>(id)));

        // The Track list page on the championship's own list; Done takes it and comes back here.
        void EditTracks()
        {
            Flow.Get<TrackListScreen>().Open(RoomTrackIds(), IsRoomTrack, ids =>
            {
                settings.trackIds = ids;
                Flow.Back();
            }, null, trackListTitle, trackListDone);
        }

        List<CupDefinition> Cups()
        {
            var cups = ContentManifests.LobbyContent<CupDefinition>(Registry, settings.packs);
            cups.Sort((a, b) => a.order.CompareTo(b.order));
            return cups;
        }

        bool HasPack(string id) => settings.packs.Exists(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));

        // The mode plays on the track (arenas in battles, race tracks and raceable arenas in the other modes).
        bool Fits(TrackDefinition t) => settings.mode == RoomMode.BattleSet ? t.HostsBattles : t.HostsRaces;

        // Tracks (or arenas in battles) a pack brings itself.
        int TracksOf(string packId)
        {
            var n = 0;
            foreach (var t in ContentManifests.LobbyContent<TrackDefinition>(Registry, new[] { packId }))
                if (string.Equals(ContentManifests.PackOf(t.Id), packId, StringComparison.OrdinalIgnoreCase) && Fits(t)) n++;
            return n;
        }

        // One toggle card per loaded pack: logo, name, how many tracks or arenas it brings.
        void FillMods()
        {
            modGrid.Clear();
            var i = 0;
            foreach (var id in AvailablePacks())
            {
                var packId = id;
                var pack = OnlineUi.FindPack(id);
                var card = new Button { tooltip = pack != null && !string.IsNullOrEmpty(pack.description) ? pack.description : UiText.GameName(id), userData = id };
                card.AddToClassList("card");
                card.AddToClassList("room-mod-card");
                card.SetTransitionDelay(40 + 20 * i++);
                var check = new VisualElement { pickingMode = PickingMode.Ignore };
                check.AddToClassList("card-check");
                var checkIcon = new VisualElement { pickingMode = PickingMode.Ignore };
                checkIcon.AddToClassList("icon");
                checkIcon.AddToClassList("i-check");
                check.Add(checkIcon);
                var logo = OnlineUi.Logo(pack, id);
                logo.AddToClassList("room-mod-logo");
                var title = new Label(OnlineUi.PackName(id)) { pickingMode = PickingMode.Ignore };
                title.AddToClassList("room-mod-name");
                var chips = new VisualElement { name = "mod-chips", pickingMode = PickingMode.Ignore };
                chips.AddToClassList("info-chips");
                chips.AddToClassList("room-mod-chips");
                card.Add(logo);
                card.Add(title);
                card.Add(chips);
                card.Add(check);
                card.EnableInClassList("selected", HasPack(id));
                card.clicked += () =>
                {
                    Click();
                    if (HasPack(packId)) settings.packs.RemoveAll(p => string.Equals(p, packId, StringComparison.OrdinalIgnoreCase));
                    else settings.packs.Add(packId);
                    card.EnableInClassList("selected", HasPack(packId));
                    FixCup();
                    Changed(true);
                };
                modGrid.Add(card);
            }
        }

        void ShowModCounts()
        {
            if (modGrid == null) return;
            foreach (var card in modGrid.Children())
            {
                if (card.userData is not string id) continue;
                var chips = card.Q("mod-chips");
                chips.Clear();
                var n = TracksOf(id);
                var battle = settings.mode == RoomMode.BattleSet;
                InfoChips.Add(chips, battle ? "swords" : "map", string.Format(battle ? arenasFormat : tracksFormat, n), null,
                    n > 0 ? InfoChipState.Normal : InfoChipState.Off);
            }
        }

        // The rules as direct controls, rebuilt when the mode changes (some apply to some modes only).
        void FillRules(bool keepFocus)
        {
            if (rules == null) return;
            var focusedIndex = -1;
            var focused = keepFocus ? rules.Container.panel?.focusController?.focusedElement as VisualElement : null;
            var focusedRow = focused != null ? rules.RowOf(focused) : null;
            if (focusedRow != null) focusedIndex = rules.Container.IndexOf(focusedRow);
            rules.Clear();
            var championship = settings.mode == RoomMode.Championship;
            var toLine = OnlineUi.RacesToLine(settings.mode);
            rules.Switch("shield", "Anticheat", "Players' games check each other's karts, cheaters are removed from the race.",
                () => settings.anticheat, v => settings.anticheat = v);
            var classes = OnlineUi.EngineClassCount;
            rules.Choice("speed", "Engine class", "Kart speed for everyone in the room", () => classes,
                () => Mathf.Clamp(settings.engineClass, 0, classes - 1), OnlineUi.EngineClassName, i => settings.engineClass = i);
            if (toLine)
            {
                rules.Number("flag", "Laps", "Track races every track's own lap count", 0, LobbyLimits.Laps, () => settings.laps,
                    v => settings.laps = v, trackLapsText, lapsRangeFormat);
                rules.Choice("hourglass_top", "Finish wait", "How long the race goes on after the first kart finished", () => FinishWaits.Length,
                    () => FinishWaitIndex(settings.finishWait), i => OnlineUi.Duration(FinishWaits[i]), i => settings.finishWait = FinishWaits[i]);
                rules.Switch("skip_next", "End at the line",
                    "After the winner finishes, everyone finishes when they next cross the line. Lapped drivers show +N laps.",
                    () => settings.endAtLine, v => settings.endAtLine = v);
            }
            rules.Switch("redeem", "Items", "Item boxes give powerups", () => settings.items, v =>
            {
                settings.items = v;
                FillRules(true);
            });
            if (settings.items)
            {
                OddsRow();
                if (OnlineUi.HasGhosts(settings.mode))
                    rules.Switch("visibility_off", "Ghost powerups",
                        "Knocked out drivers drive on as ghosts. On, they pick up light powerups. Off, they drive through the item boxes.",
                        () => settings.ghostItems, v => settings.ghostItems = v);
            }
            rules.Number("group", "Players", "Room size, humans and computer drivers together", LobbyLimits.MinPlayers, LobbyLimits.MaxPlayers,
                () => settings.maxPlayers, v =>
                {
                    settings.maxPlayers = v;
                    settings.maxAi = Mathf.Min(settings.maxAi, v - 1);
                    BindAi();
                });
            aiRow = rules.Number("smart_toy", "AI", "Computer drivers race in the places no human takes, at most this many (0 = humans only).",
                0, settings.maxPlayers - 1, () => settings.maxAi, v => settings.maxAi = v);
            if (championship) ChampionshipRows();
            else
            {
                rules.Choice("how_to_vote", "Track pick", "Vote, or the owner picks each track (in race rooms also a whole cup between sets)",
                    () => 2, () => settings.OwnerPicks ? 1 : 0, i => i == 0 ? "Vote" : "Owner picks", i => settings.trackPick = i == 1 ? 0 : 1);
                rules.Number("repeat", "Races per set", "Points add up over a set", 1, LobbyLimits.MaxRacesPerSet, () => settings.racesPerSet,
                    v => settings.racesPerSet = v);
            }
            if (focusedIndex >= 0 && focusedIndex < rules.Container.childCount) FocusLater(RuleRows.FirstControl(rules.Container[focusedIndex]) ?? rules.Container[focusedIndex].Q<Button>());
        }

        // A championship races a cup (its pill) or the owner's own list (opens the Track list page).
        void ChampionshipRows()
        {
            rules.Choice("emoji_events", "Tracks", "A cup, or your own list of up to 35 tracks in your order", () => 2, () => ownTracks ? 1 : 0,
                i => i == 0 ? "Cup" : "Own list", i =>
                {
                    ownTracks = i == 1;
                    if (ownTracks) settings.cupId = "";
                    FixCup();
                    Changed(true);
                });
            if (ownTracks)
            {
                var count = RoomTrackIds().Count;
                rules.Link("map", "Track list", "The championship's races in their order",
                    count == 1 ? "1 track" : count.ToString(CultureInfo.InvariantCulture) + " tracks", EditTracks);
                return;
            }
            rules.Choice("military_tech", "Cup", null, () => Mathf.Max(1, Cups().Count), () => Mathf.Max(0, Cups().FindIndex(c => c.Id == settings.cupId)),
                i =>
                {
                    var cups = Cups();
                    return cups.Count > 0 ? cups[i].DisplayName : noCupText;
                }, i =>
                {
                    var cups = Cups();
                    if (cups.Count > 0) settings.cupId = cups[i].Id;
                });
        }

        // The AI input, bound again whenever the room size changes its maximum.
        void BindAi()
        {
            if (aiRow == null) return;
            rules.BindNumber(aiRow, 0, settings.maxPlayers - 1, () => settings.maxAi, v => settings.maxAi = v);
        }

        // The item odds pill offers the standard odds, the room's own and the player's own.
        void OddsRow()
        {
            var choices = OddsChoices();
            rules.Choice("tune", "Item odds", "How often each powerup comes out of the item boxes", () => choices.Count,
                () => Mathf.Max(0, choices.IndexOf(oddsChoice)),
                i => choices[i] == null ? defaultOddsText : choices[i] == KeepRoomOdds ? roomOddsText : ownOddsText,
                i => oddsChoice = choices[i]);
        }

        // Standard, the room's own odds (when it has some), then the player's own odds (when they give items).
        List<string> OddsChoices()
        {
            var choices = new List<string> { null };
            if (keptOdds.Count > 0) choices.Add(KeepRoomOdds);
            if (ItemOdds.Total(Settings.ownItemOdds) > 0f) choices.Add(OwnOdds);
            return choices;
        }

        // The room's odds of powerups that are still room content (packs may have changed).
        List<PowerupOdds> KeptOdds()
        {
            var powerups = ContentManifests.LobbyContent<PowerupDefinition>(Registry, settings.packs);
            return keptOdds.FindAll(o => powerups.Exists(p => p.Id == o.id));
        }

        // The player's own odds, limited to the room's powerups (the server accepts nothing else).
        List<PowerupOdds> RoomOdds()
        {
            var odds = new List<PowerupOdds>();
            if (!settings.items || oddsChoice == null) return odds;
            if (oddsChoice == KeepRoomOdds) return KeptOdds();
            var powerups = ContentManifests.LobbyContent<PowerupDefinition>(Registry, settings.packs);
            foreach (var o in Settings.ownItemOdds)
            {
                if (odds.Count == LobbyLimits.ItemOdds) break;
                if (powerups.Exists(p => p.Id == o.id) && !odds.Exists(x => x.id == o.id)) odds.Add(new PowerupOdds(o.id, Mathf.Max(0f, o.percent)));
            }
            return odds;
        }

        // What the room will be, as chips.
        void ShowSummary()
        {
            if (summary == null) return;
            var preview = settings.Clone();
            preview.itemOdds = oddsChoice != null && settings.items ? new List<PowerupOdds> { default } : new List<PowerupOdds>();
            if (preview.mode == RoomMode.Championship && ownTracks)
            {
                preview.cupId = "";
                preview.trackIds = RoomTrackIds();
            }
            OnlineUi.SettingsChips(summary, preview, true);
            summary.Insert(0, InfoChips.Add(null, "extension", OnlineUi.SetName(settings.packs), OnlineUi.SetTooltip(settings.packs)));
            InfoChips.Add(summary, "group", settings.maxPlayers.ToString(CultureInfo.InvariantCulture), "Players");
        }

        // Why the room cannot be made: no mods with tracks (or arenas), no cup.
        string Problem()
        {
            var battle = settings.mode == RoomMode.BattleSet;
            var tracks = 0;
            foreach (var t in ContentManifests.LobbyContent<TrackDefinition>(Registry, settings.packs))
                if (Fits(t) && !string.Equals(ContentManifests.PackOf(t.Id), ContentManifests.GamePackId, StringComparison.OrdinalIgnoreCase)) tracks++;
            if (settings.packs.Count == 0 || tracks == 0) return battle ? noArenasText : noTracksText;
            if (settings.mode == RoomMode.Championship && !ownTracks && string.IsNullOrEmpty(settings.cupId)) return noCupText;
            if (settings.mode == RoomMode.Championship && ownTracks && RoomTrackIds().Count == 0) return noTrackListText;
            return null;
        }

        async void Save()
        {
            if (Root == null) return;
            var problem = Problem();
            if (problem != null)
            {
                SetStatus(problem);
                return;
            }
            settings.name = LobbyWire.Clip(settings.name?.Trim(), LobbyLimits.RoomName);
            settings.itemOdds = RoomOdds();
            // What goes to the server: a championship follows the cup or the own list, the owner starts each race on its
            // next track (the server lets vote rooms vote only) and the set is its races (the server counts a cup by
            // racesPerSet, a list by its length). The screen's own choices stay for the next room.
            var send = settings.Clone();
            send.maxAi = Mathf.Clamp(send.maxAi, 0, send.maxPlayers - 1);
            if (send.mode != RoomMode.Championship)
            {
                send.cupId = "";
                send.trackIds.Clear();
            }
            else
            {
                send.trackPick = 0;
                if (ownTracks)
                {
                    send.cupId = "";
                    send.trackIds = RoomTrackIds();
                    send.racesPerSet = Mathf.Clamp(send.trackIds.Count, 1, LobbyLimits.TrackIds);
                }
                else
                {
                    send.trackIds.Clear();
                    var cup = Registry.Get<CupDefinition>(send.cupId);
                    if (cup != null) send.racesPerSet = Mathf.Clamp(cup.tracks.Count, 1, LobbyLimits.MaxRacesPerSet);
                }
            }
            // Battles have no laps, Last Man Standing races one lap per kart but one.
            if (!OnlineUi.RacesToLine(send.mode)) send.laps = 0;
            var save = Q<Button>("save");
            save.SetEnabled(false);
            var r = editing ? await OnlineLobby.UpdateRoom(send) : await OnlineLobby.CreateRoom(send);
            if (Root == null)
            {
                // Left the page while the room was being created: nobody waits in it.
                if (r.Ok && !editing) _ = OnlineLobby.Leave();
                return;
            }
            save.SetEnabled(true);
            if (!r.Ok)
            {
                SetStatus(r.message);
                return;
            }
            Settings.lastRoom = settings.Clone();
            GameRoot.Instance.SaveSettings();
            LobbyScreen.Return(Flow);
        }
    }
}
