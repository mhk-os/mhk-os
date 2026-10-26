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
    /// Settings of a private room (RoomSettings.uxml): creating one (from the Online page, starting from the last room's
    /// settings) or the owner changing them while the room waits. Name, mode (segmented: races, battles, championship and
    /// its cup), the mods as toggle cards (any set of the loaded content packs, third-party mods included; at least one
    /// must bring tracks or arenas for the mode), and the rules as direct controls: anticheat, items, AI switches, engine
    /// class and track pick chips, laps / players / races steppers, item odds dropdown (standard, the room's own, or the
    /// player's own odds of a family, limited to the room's powerups). A chip row sums the room up.
    /// </summary>
    public class RoomSettingsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Setting row (Templates/SettingRow.uxml): cup and item odds dropdowns.")]
        VisualTreeAsset rowTemplate;

        [Header("Texts")]
        [SerializeField] string createTitle = "Create room", editTitle = "Room settings";
        [SerializeField] string createLabel = "Create room", saveLabel = "Save";
        [SerializeField] string trackLapsText = "Track";
        [SerializeField] string defaultOddsText = "Standard";
        [SerializeField, Tooltip("Own odds of a family. {0} = the family's game.")] string ownOddsFormat = "My odds: {0}";
        [SerializeField, Tooltip("Item odds value while the room keeps the odds it already has.")] string roomOddsText = "This room's odds";
        [SerializeField] string noTracksText = "Choose at least one mod with tracks.";
        [SerializeField] string noArenasText = "Choose at least one mod with arenas.";
        [SerializeField] string noCupText = "The chosen mods have no cup.";
        [SerializeField, Tooltip("{0} = count.")] string tracksFormat = "{0} tracks", arenasFormat = "{0} arenas";

        const int MaxLapsChoice = 15;

        RoomSettings settings;
        bool editing;
        // Item odds choice: null = default odds, KeepRoomOdds = the odds the room (or the last room) already has, else
        // the family key of one of the player's own sets. Saving rebuilds the odds only when the player chose another value.
        const string KeepRoomOdds = "\u0001room";
        string oddsFamily;
        // The odds the room had when the screen opened (KeepRoomOdds).
        List<PowerupOdds> keptOdds = new();
        VisualElement modGrid, rules, summary;
        Label statusLabel;
        SettingChoice cupChoice;

        /// <summary>Opens the screen to create a room from the last room's settings.</summary>
        public void Open()
        {
            editing = false;
            settings = Settings.lastRoom != null ? Settings.lastRoom.Clone() : new RoomSettings();
            oddsFamily = null;
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
            oddsFamily = null;
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
            rules = Q<VisualElement>("rule-rows");
            summary = Q<VisualElement>("summary");
            statusLabel = Q<Label>("room-status-label");
            KeepFocusedInView(Q<ScrollView>("mod-scroll"), modGrid);
            SetStatus(null);
            OnlineUi.Segmented(Q<VisualElement>("mode-tabs"),
                new[] { ("sports_score", "Races", (string)null), ("swords", "Battles", null), ("emoji_events", "Championship", "The races of one cup") },
                (int)settings.mode, i =>
                {
                    Click();
                    settings.mode = (RoomMode)i;
                    FixCup();
                    Changed(true);
                });
            cupChoice = BindChoice("cup-row", () => Mathf.Max(1, Cups().Count), () => Mathf.Max(0, Cups().FindIndex(c => c.Id == settings.cupId)),
                i =>
                {
                    var cups = Cups();
                    return cups.Count > 0 ? cups[i].DisplayName : noCupText;
                }, i =>
                {
                    var cups = Cups();
                    if (cups.Count > 0) settings.cupId = cups[i].Id;
                    ShowSummary();
                });
            FillMods();
            Changed(false);
            FocusTarget = Q<VisualElement>("mode-tabs").Q<Button>(className: "selected");
        }

        // The wide name field goes down to the selected mode chip (not the one under its middle); Up / Down in the rule rows
        // go row by row (their controls sit at different heights and sides) to the selected chip or first control, and Left
        // from a row's first control goes to the mod card at its height.
        VisualElement Navigate(VisualElement from, NavigationMoveEvent.Direction d)
        {
            var name = Q<TextField>("room-name");
            if (from == name || name.Contains(from))
                return d == NavigationMoveEvent.Direction.Down ? OnlineUi.SelectedChip(Q<VisualElement>("mode-tabs")) : null;
            var row = from;
            while (row != null && row.parent != rules) row = row.parent;
            if (row == null) return null;
            if (d == NavigationMoveEvent.Direction.Left)
            {
                if (row.Query<Button>().Where(OnlineUi.CanFocus).First() != from) return null;
                VisualElement nearest = null;
                var y = from.worldBound.center.y;
                foreach (var card in Q<VisualElement>("mod-grid").Children())
                    if (OnlineUi.CanFocus(card) && (nearest == null || Mathf.Abs(card.worldBound.center.y - y) < Mathf.Abs(nearest.worldBound.center.y - y)))
                        nearest = card;
                return nearest;
            }
            if (d is not (NavigationMoveEvent.Direction.Up or NavigationMoveEvent.Direction.Down)) return null;
            var step = d == NavigationMoveEvent.Direction.Down ? 1 : -1;
            for (var i = rules.IndexOf(row) + step; i >= 0 && i < rules.childCount; i += step)
            {
                var chip = rules[i].Q(className: "seg-chip");
                var target = chip != null ? OnlineUi.SelectedChip(chip.parent) : rules[i].Query<Button>().Where(OnlineUi.CanFocus).First();
                if (OnlineUi.CanFocus(target)) return target;
            }
            return null;
        }

        protected override void Unbind()
        {
            base.Unbind();
            modGrid = rules = summary = null;
            statusLabel = null;
            cupChoice = null;
        }

        // The mode or the mods changed: the controls that depend on them, the cards' counts and the summary.
        void Changed(bool keepFocus)
        {
            SetVisible("cup-row", settings.mode == RoomMode.Championship);
            cupChoice?.Refresh();
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

        // Keeps only loaded packs (the four games when none is left), a cup of them and a valid range for every number.
        void Sanitize()
        {
            var available = AvailablePacks();
            settings.packs = settings.packs?.FindAll(p => available.Exists(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))) ?? new List<string>();
            if (settings.packs.Count == 0)
                foreach (var p in available)
                    if (Array.IndexOf(LobbyCategories.All[0].packs, p) >= 0) settings.packs.Add(p);
            if (settings.packs.Count == 0 && available.Count > 0) settings.packs.Add(available[0]);
            settings.engineClass = Mathf.Clamp(settings.engineClass, 0, 2);
            settings.laps = Mathf.Clamp(settings.laps, 0, MaxLapsChoice);
            settings.maxPlayers = Mathf.Clamp(settings.maxPlayers, LobbyLimits.MinPlayers, LobbyLimits.MaxPlayers);
            settings.trackPick = Mathf.Clamp(settings.trackPick, 0, 1);
            settings.racesPerSet = Mathf.Clamp(settings.racesPerSet, 1, LobbyLimits.MaxRacesPerSet);
            settings.name = LobbyWire.Clip(settings.name, LobbyLimits.RoomName);
            keptOdds = new List<PowerupOdds>(settings.itemOdds);
            if (keptOdds.Count > 0) oddsFamily = KeepRoomOdds;
            FixCup();
        }

        void FixCup()
        {
            var cups = Cups();
            if (!cups.Exists(c => c.Id == settings.cupId)) settings.cupId = cups.Count > 0 ? cups[0].Id : "";
        }

        List<CupDefinition> Cups()
        {
            var cups = ContentManifests.LobbyContent<CupDefinition>(Registry, settings.packs);
            cups.Sort((a, b) => a.order.CompareTo(b.order));
            return cups;
        }

        bool HasPack(string id) => settings.packs.Exists(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));

        // Tracks (or arenas in battles) a pack brings itself.
        int TracksOf(string packId)
        {
            var arena = settings.mode == RoomMode.BattleSet;
            var n = 0;
            foreach (var t in ContentManifests.LobbyContent<TrackDefinition>(Registry, new[] { packId }))
                if (string.Equals(ContentManifests.PackOf(t.Id), packId, StringComparison.OrdinalIgnoreCase)
                    && (t.layout == TrackLayout.Arena) == arena) n++;
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

        // The rules as direct controls, rebuilt when the mode changes (some apply to one mode only).
        void FillRules(bool keepFocus)
        {
            if (rules == null) return;
            var focusedIndex = -1;
            var focused = keepFocus ? rules.panel?.focusController?.focusedElement as VisualElement : null;
            for (var i = 0; i < rules.childCount; i++)
                if (focused != null && rules[i].Contains(focused)) focusedIndex = i;
            rules.Clear();
            var battle = settings.mode == RoomMode.BattleSet;
            var championship = settings.mode == RoomMode.Championship;
            Switch("shield", "Anticheat", "Players' games check each other's karts, cheaters are removed from the race.",
                () => settings.anticheat, v => settings.anticheat = v);
            var classes = GameRoot.Instance.Config.difficulties;
            var classItems = new List<(string, string, string)>();
            for (var i = 0; i < Mathf.Min(3, classes.Length); i++) classItems.Add((null, classes[i].label, null));
            Segment("speed", "Engine", classItems, settings.engineClass, i => settings.engineClass = i);
            if (!battle)
                Stepper("flag", "Laps", 0, MaxLapsChoice, () => settings.laps, v => settings.laps = v,
                    v => v == 0 ? trackLapsText : v.ToString(CultureInfo.InvariantCulture), "0 = every track's own lap count");
            Switch("redeem", "Items", null, () => settings.items, v =>
            {
                settings.items = v;
                FillRules(true);
            });
            if (settings.items) OddsRow();
            Switch("smart_toy", "AI when alone", "Computer drivers race you while you are alone in the room.",
                () => settings.aiWhenAlone, v => settings.aiWhenAlone = v);
            Stepper("group", "Players", LobbyLimits.MinPlayers, LobbyLimits.MaxPlayers, () => settings.maxPlayers, v => settings.maxPlayers = v,
                v => v.ToString(CultureInfo.InvariantCulture), "Room size");
            if (!championship)
            {
                Segment("how_to_vote", "Track", new[] { ("how_to_vote", "Vote", (string)null), ("edit", "Owner picks", null) },
                    settings.OwnerPicks ? 1 : 0, i => settings.trackPick = i == 1 ? 0 : 1);
                Stepper("repeat", "Races per set", 1, LobbyLimits.MaxRacesPerSet, () => settings.racesPerSet, v => settings.racesPerSet = v,
                    v => v.ToString(CultureInfo.InvariantCulture), "Points add up over a set");
            }
            if (focusedIndex >= 0 && focusedIndex < rules.childCount) FocusLater(rules[focusedIndex].Q<Button>());
        }

        // Row: icon, label, control on the right.
        VisualElement Row(string icon, string label, string tip)
        {
            var row = new VisualElement { tooltip = tip ?? "" };
            row.AddToClassList("room-row");
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.AddToClassList("icon");
            i.AddToClassList("room-row-icon");
            i.AddToClassList("i-" + icon);
            row.Add(i);
            var l = new Label(label) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("room-row-label");
            row.Add(l);
            rules.Add(row);
            return row;
        }

        void Switch(string icon, string label, string tip, Func<bool> get, Action<bool> set)
        {
            var row = Row(icon, label, tip);
            var button = new Button();
            button.AddToClassList("mhk-switch");
            var knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.AddToClassList("switch-knob");
            button.Add(knob);
            button.EnableInClassList("on", get());
            button.clicked += () =>
            {
                Click();
                set(!get());
                button.EnableInClassList("on", get());
                ShowSummary();
            };
            row.Add(button);
        }

        void Segment(string icon, string label, IReadOnlyList<(string, string, string)> items, int selected, Action<int> set)
        {
            var row = Row(icon, label, null);
            var tabs = new VisualElement();
            tabs.AddToClassList("tabs");
            tabs.AddToClassList("room-seg");
            row.Add(tabs);
            OnlineUi.Segmented(tabs, items, selected, i =>
            {
                Click();
                set(i);
                ShowSummary();
            });
        }

        void Stepper(string icon, string label, int min, int max, Func<int> get, Action<int> set, Func<int, string> text, string tip)
        {
            var row = Row(icon, label, tip);
            var box = new VisualElement();
            box.AddToClassList("room-stepper");
            var minus = StepButton("remove");
            var value = new Label(text(get()));
            value.AddToClassList("room-stepper-value");
            var plus = StepButton("add");
            void Step(int d)
            {
                var v = Mathf.Clamp(get() + d, min, max);
                if (v == get()) return;
                Click();
                set(v);
                value.text = text(v);
                minus.SetEnabled(v > min);
                plus.SetEnabled(v < max);
                ShowSummary();
            }
            minus.clicked += () => Step(-1);
            plus.clicked += () => Step(1);
            minus.SetEnabled(get() > min);
            plus.SetEnabled(get() < max);
            box.Add(minus);
            box.Add(value);
            box.Add(plus);
            row.Add(box);
        }

        static Button StepButton(string icon)
        {
            var b = new Button();
            b.AddToClassList("mhk-button");
            b.AddToClassList("small");
            b.AddToClassList("secondary");
            b.AddToClassList("icon-only");
            b.AddToClassList("room-step");
            var i = new VisualElement { pickingMode = PickingMode.Ignore };
            i.AddToClassList("icon");
            i.AddToClassList("i-" + icon);
            b.Add(i);
            return b;
        }

        // Item odds: a dropdown (standard, the room's own, the player's own sets).
        void OddsRow()
        {
            var row = Row("tune", "Odds", "Item odds");
            if (rowTemplate == null) return;
            var holder = new VisualElement();
            holder.AddToClassList("setting-row");
            holder.AddToClassList("room-odds");
            rowTemplate.CloneTree(holder);
            holder.Q("setting-label")?.SetVisible(false);
            row.Add(holder);
            var choices = OddsChoices();
            new SettingChoice(holder, Root, () => choices.Count, () => Mathf.Max(0, choices.IndexOf(oddsFamily)),
                i => choices[i] == null ? defaultOddsText : choices[i] == KeepRoomOdds ? roomOddsText : string.Format(ownOddsFormat, OnlineUi.PackName(choices[i])),
                i =>
                {
                    oddsFamily = choices[i];
                    ShowSummary();
                }, null, Click);
        }

        // Standard, the room's own odds (when it has some), then every family the player has own odds for.
        List<string> OddsChoices()
        {
            var choices = new List<string> { null };
            if (keptOdds.Count > 0) choices.Add(KeepRoomOdds);
            foreach (var set in Settings.itemOdds)
                if (set != null && ItemOdds.Total(set.odds) > 0f && !choices.Contains(set.family)) choices.Add(set.family);
            return choices;
        }

        // The room's odds of powerups that are still room content (packs may have changed).
        List<PowerupOdds> KeptOdds()
        {
            var powerups = ContentManifests.LobbyContent<PowerupDefinition>(Registry, settings.packs);
            return keptOdds.FindAll(o => powerups.Exists(p => p.Id == o.id));
        }

        // The player's own odds of the chosen family, limited to the room's powerups (the server accepts nothing else).
        List<PowerupOdds> RoomOdds()
        {
            var odds = new List<PowerupOdds>();
            if (!settings.items || oddsFamily == null) return odds;
            if (oddsFamily == KeepRoomOdds) return KeptOdds();
            var own = Settings.ItemOddsFor(oddsFamily);
            if (own == null) return odds;
            var powerups = ContentManifests.LobbyContent<PowerupDefinition>(Registry, settings.packs);
            foreach (var o in own.odds)
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
            preview.itemOdds = oddsFamily != null && settings.items ? new List<PowerupOdds> { default } : new List<PowerupOdds>();
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
                if ((t.layout == TrackLayout.Arena) == battle && !string.Equals(ContentManifests.PackOf(t.Id), ContentManifests.GamePackId, StringComparison.OrdinalIgnoreCase)) tracks++;
            if (settings.packs.Count == 0 || tracks == 0) return battle ? noArenasText : noTracksText;
            if (settings.mode == RoomMode.Championship && string.IsNullOrEmpty(settings.cupId)) return noCupText;
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
            // What goes to the server: a championship follows the cup, the owner starts each race on the cup's next track
            // (the server lets vote rooms vote only) and the set is the cup's races (the server counts it by racesPerSet).
            // The screen's own choices stay for the next room.
            var send = settings.Clone();
            if (send.mode != RoomMode.Championship) send.cupId = "";
            else
            {
                send.trackPick = 0;
                var cup = Registry.Get<CupDefinition>(send.cupId);
                if (cup != null) send.racesPerSet = Mathf.Clamp(cup.tracks.Count, 1, LobbyLimits.MaxRacesPerSet);
            }
            if (send.mode == RoomMode.BattleSet) send.laps = 0;
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
