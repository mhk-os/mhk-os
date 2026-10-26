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
    /// settings) or the owner changing them while the room waits. Name, mode (race set, battle set, championship), the
    /// packs (a subset of the loaded ones), anticheat (on by default), engine class, laps, items and their odds (default
    /// or the player's own odds of a family, limited to the room's powerups), AI while alone, max players, how the track
    /// is chosen, races per set or the cup.
    /// </summary>
    public class RoomSettingsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Setting row (Templates/SettingRow.uxml).")]
        VisualTreeAsset rowTemplate;

        [Header("Texts")]
        [SerializeField] string createTitle = "Create room", editTitle = "Room settings";
        [SerializeField] string createLabel = "Create room", saveLabel = "Save";
        [SerializeField] string onText = "On", offText = "Off";
        [SerializeField] string trackLapsText = "Track default";
        [SerializeField] string defaultOddsText = "Standard";
        [SerializeField, Tooltip("Own odds of a family. {0} = the family's game.")] string ownOddsFormat = "My odds: {0}";
        [SerializeField, Tooltip("Item odds value while the room keeps the odds it already has.")] string roomOddsText = "This room's odds";
        [SerializeField] string ownerPicksText = "Owner picks", voteText = "Vote";
        [SerializeField] string packsSection = "PACKS";
        [SerializeField] string noPacksText = "Choose at least one pack with tracks.";
        [SerializeField] string noCupText = "The chosen packs have no cup.";
        [SerializeField] string anticheatTip = "Players' games check each other's karts and the server removes cheaters from the race.";
        [SerializeField] string aiTip = "Computer drivers race you while you are alone in the room.";

        const int MaxLapsChoice = 15;

        static readonly string[] ModeTexts = { "Race set", "Battle set", "Championship" };

        RoomSettings settings;
        bool editing;
        // Item odds choice: null = default odds, KeepRoomOdds = the odds the room (or the last room) already has, else
        // the family key of one of the player's own sets. Saving rebuilds the odds only when the player chose another value.
        const string KeepRoomOdds = "\u0001room";
        string oddsFamily;
        // The odds the room had when the screen opened (KeepRoomOdds).
        List<PowerupOdds> keptOdds = new();
        VisualElement rows;
        Label statusLabel;

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
            Q<Label>("title").text = editing ? editTitle : createTitle;
            Q<Label>("save-label").text = editing ? saveLabel : createLabel;
            Q<VisualElement>("save-icon").SetIcon(editing ? "check" : "add");
            OnClick("save", Save);
            var name = Q<TextField>("room-name");
            name.SetValueWithoutNotify(settings.name);
            name.textEdition.placeholder = (OnlineService.Username ?? "") + "'s room";
            name.RegisterValueChangedCallback(e => settings.name = e.newValue);
            rows = Q<VisualElement>("room-rows");
            statusLabel = Q<Label>("room-status-label");
            KeepFocusedInView(Q<ScrollView>("room-scroll"), rows);
            SetStatus(null);
            FillRows();
        }

        protected override void Unbind()
        {
            base.Unbind();
            rows = null;
            statusLabel = null;
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

        // Keeps only loaded packs (the base games when none is left), a cup of them and a valid range for every number.
        void Sanitize()
        {
            var available = AvailablePacks();
            settings.packs = settings.packs?.FindAll(p => available.Exists(a => string.Equals(a, p, StringComparison.OrdinalIgnoreCase))) ?? new List<string>();
            if (settings.packs.Count == 0)
                foreach (var p in available)
                    if (LobbyCategories.All[0].packs is { } basePacks && Array.IndexOf(basePacks, p) >= 0) settings.packs.Add(p);
            if (settings.packs.Count == 0 && available.Count > 0) settings.packs.Add(available[0]);
            settings.engineClass = Mathf.Clamp(settings.engineClass, 0, 2);
            settings.laps = Mathf.Clamp(settings.laps, 0, LobbyLimits.Laps);
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

        void FillRows()
        {
            if (rows == null) return;
            var focusedIndex = -1;
            var focused = rows.panel?.focusController?.focusedElement as VisualElement;
            for (var i = 0; i < rows.childCount; i++)
                if (focused != null && rows[i].Contains(focused)) focusedIndex = i;
            rows.Clear();
            var championship = settings.mode == RoomMode.Championship;
            var battle = settings.mode == RoomMode.BattleSet;
            Row("Mode", () => ModeTexts.Length, () => (int)settings.mode, i => ModeTexts[i], i =>
            {
                settings.mode = (RoomMode)i;
                FixCup();
                FillRows();
            });
            Section(packsSection);
            foreach (var pack in AvailablePacks())
            {
                var id = pack;
                Row(UiText.GameName(id), () => 2, () => settings.packs.Exists(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase)) ? 1 : 0,
                    i => i == 1 ? onText : offText, i =>
                    {
                        settings.packs.RemoveAll(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));
                        if (i == 1) settings.packs.Add(id);
                        FixCup();
                        FillRows();
                    });
            }
            Section("");
            Toggle("Anticheat", () => settings.anticheat, v => settings.anticheat = v, anticheatTip);
            var classes = GameRoot.Instance.Config.difficulties;
            Row("Engine class", () => Mathf.Min(3, classes.Length), () => settings.engineClass, i => classes[i].label, i => settings.engineClass = i);
            if (!battle)
                Row("Laps", () => MaxLapsChoice + 1, () => Mathf.Min(settings.laps, MaxLapsChoice),
                    i => i == 0 ? trackLapsText : i.ToString(CultureInfo.InvariantCulture), i => settings.laps = i);
            Toggle("Items", () => settings.items, v =>
            {
                settings.items = v;
                FillRows();
            });
            if (settings.items)
            {
                var choices = OddsChoices();
                Row("Item odds", () => choices.Count, () => Mathf.Max(0, choices.IndexOf(oddsFamily)),
                    i => choices[i] == null ? defaultOddsText : choices[i] == KeepRoomOdds ? roomOddsText : string.Format(ownOddsFormat, UiText.GameName(choices[i])),
                    i => oddsFamily = choices[i]);
            }
            Toggle("AI when alone", () => settings.aiWhenAlone, v => settings.aiWhenAlone = v, aiTip);
            Row("Max players", () => LobbyLimits.MaxPlayers - LobbyLimits.MinPlayers + 1, () => settings.maxPlayers - LobbyLimits.MinPlayers,
                i => (i + LobbyLimits.MinPlayers).ToString(CultureInfo.InvariantCulture), i => settings.maxPlayers = i + LobbyLimits.MinPlayers);
            if (championship)
            {
                var cups = Cups();
                Row("Cup", () => Mathf.Max(1, cups.Count), () => Mathf.Max(0, cups.FindIndex(c => c.Id == settings.cupId)),
                    i => cups.Count > 0 ? cups[i].DisplayName : noCupText, i =>
                    {
                        if (cups.Count > 0) settings.cupId = cups[i].Id;
                    });
            }
            else
            {
                Row("Track", () => 2, () => settings.trackPick, i => i == 0 ? ownerPicksText : voteText, i => settings.trackPick = i);
                Row("Races per set", () => LobbyLimits.MaxRacesPerSet, () => settings.racesPerSet - 1,
                    i => (i + 1).ToString(CultureInfo.InvariantCulture), i => settings.racesPerSet = i + 1);
            }
            if (focusedIndex >= 0 && focusedIndex < rows.childCount) FocusLater(rows[focusedIndex].Q<Button>());
        }

        // Standard, the room's own odds (when it has some), then every family the player has own odds for.
        List<string> OddsChoices()
        {
            var choices = new List<string> { null };
            if (keptOdds.Count > 0) choices.Add(KeepRoomOdds);
            choices.AddRange(OwnOddsFamilies());
            return choices;
        }

        // The room's odds of powerups that are still room content (packs may have changed).
        List<PowerupOdds> KeptOdds()
        {
            var powerups = ContentManifests.LobbyContent<PowerupDefinition>(Registry, settings.packs);
            return keptOdds.FindAll(o => powerups.Exists(p => p.Id == o.id));
        }

        // Families the player has own odds for (Powerups screen), with at least one powerup above 0 %.
        List<string> OwnOddsFamilies()
        {
            var list = new List<string>();
            foreach (var set in Settings.itemOdds)
                if (set != null && ItemOdds.Total(set.odds) > 0f && !list.Contains(set.family)) list.Add(set.family);
            return list;
        }

        void Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("room-section");
            label.SetVisible(!string.IsNullOrEmpty(text));
            rows.Add(label);
        }

        void Row(string label, Func<int> count, Func<int> index, Func<int, string> text, Action<int> select, string tip = null)
        {
            if (rowTemplate == null) return;
            // The template is the row's content (label and value button): the row element is this page's.
            var row = new VisualElement();
            row.AddToClassList("setting-row");
            rowTemplate.CloneTree(row);
            rows.Add(row);
            row.SetText("setting-label", label);
            new SettingChoice(row, Root, count, index, text, select, tip != null ? _ => tip : null, Click);
        }

        void Toggle(string label, Func<bool> get, Action<bool> set, string tip = null) =>
            Row(label, () => 2, () => get() ? 1 : 0, i => i == 1 ? onText : offText, i => set(i == 1), tip);

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

        async void Save()
        {
            if (Root == null) return;
            if (settings.packs.Count == 0 || ContentManifests.LobbyContent<TrackDefinition>(Registry, settings.packs).Count == 0)
            {
                SetStatus(noPacksText);
                return;
            }
            if (settings.mode == RoomMode.Championship && string.IsNullOrEmpty(settings.cupId))
            {
                SetStatus(noCupText);
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
