using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Race settings (RaceSettings.uxml) before a race of any mode, as <see cref="RuleRows"/> in the look of
    /// the room settings. Engine class (also the computer drivers' skill), opponents, the track's variant
    /// (<see cref="TrackVariantRows"/>, single race, battle, Last Man Standing and time trial), laps (a single race up to
    /// <see cref="CustomRaceSetup.MaxLaps"/> or the track's own, a time trial one lap or the track's, Last Man Standing races
    /// one lap per opponent) and powerups (opens the <see cref="ItemOddsScreen"/>). Rows that do not apply to the mode are
    /// left out. The side stands the 3D turntable above a compact card of the track or cup and the driver, so the card
    /// never covers the podium. Start! starts.
    /// </summary>
    public class RaceSettingsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Laps value when the track's own lap count is used (it fits the number field).")]
        string lapsTrackText = "Track";
        [SerializeField, Tooltip("Powerups value with the default item odds.")]
        string standardText = "Standard";
        [SerializeField, Tooltip("Powerups value with the player's own item odds.")]
        string customText = "Custom";
        [SerializeField, Tooltip("Powerups value when every item odd is 0 % (the race has no powerups).")]
        string offText = "Off";
        [SerializeField, Tooltip("Time trial laps values, a single lap or the track's laps ({0} = laps).")]
        string oneLapText = "1 lap", lapsFormat = "{0} laps";
        [SerializeField, Tooltip("Range hint of the Opponents input ({0} = fewest, {1} = most).")]
        string opponentsRangeFormat = "{0} to {1}";
        [SerializeField, Tooltip("Range hint of the Laps input ({1} = most, 0 is the track's own count).")]
        string lapsRangeFormat = "1 to {1}";
        [SerializeField, Tooltip("Setting row template (Templates/SettingRow.uxml) of the track variant rows.")]
        VisualTreeAsset settingRowTemplate;

        const int MaxLaps = CustomRaceSetup.MaxLaps;
        // Rows slide in one after another like the other pages' rows.
        const long FirstRowMs = 120, RowStepMs = 50;

        readonly List<PowerupOdds> odds = new();

        Action start;
        string what;
        Texture2D image;
        RuleRows rules;

        /// <summary>Opens the screen for <paramref name="what"/> (track or cup name). Start! runs <paramref name="start"/>.</summary>
        public void Open(Action start, string what, Texture2D image)
        {
            this.start = start;
            this.what = what;
            this.image = image;
            Flow.Show(this);
        }

        protected override void Bind()
        {
            var s = Selection;
            Q<Label>("info-mode").text = s.ModeName;
            Q<VisualElement>("info-mode-icon").SetIcon(ModeIcon(s.mode));
            rules = new RuleRows(Q<VisualElement>("rule-rows"), Root, Click, opponentsRangeFormat);
            BindRows(s, GameRoot.Instance.Config);
            UiAnim.Stagger(rules.Container, RowStepMs, FirstRowMs, "from-left");
            OnlineUi.Spatial(Root, rules.Route);
            BindPreview(s);
            Q<VisualElement>("showcase-area").RegisterCallback<GeometryChangedEvent>(OnShowcaseAreaChanged);
            FocusTarget = OnClick("start", () =>
            {
                RememberDifficulty(s.difficulty);
                start();
            });
        }

        protected override void Unbind()
        {
            base.Unbind();
            rules = null;
            Flow.FrameShowcase(null);
        }

        // The turntable stands in the free area above the card, whatever the screen's aspect.
        void OnShowcaseAreaChanged(GeometryChangedEvent e)
        {
            if (e.target is not VisualElement area || area.panel == null) return;
            var size = area.panel.visualTree.layout.size;
            var r = area.worldBound;
            Flow.FrameShowcase(r.width > 0f && r.height > 0f && size.x > 0f && size.y > 0f
                ? new Rect(r.x / size.x, 1f - r.yMax / size.y, r.width / size.x, r.height / size.y)
                : null);
        }

        // The engine class carries over to the next race of every mode, also after the game restarts.
        static void RememberDifficulty(Difficulty d)
        {
            var root = GameRoot.Instance;
            if (root.Settings.lastDifficulty == d) return;
            root.Settings.lastDifficulty = d;
            root.SaveSettings();
        }

        void BindRows(MenuSelection s, GameConfig config)
        {
            rules.Clear();
            var battle = s.mode == GameMode.Battle;
            var single = s.mode == GameMode.SingleRace;
            var lastMan = s.mode == GameMode.LastManStanding;
            var champ = s.mode == GameMode.Championship;
            var timeTrialLaps = s.mode == GameMode.TimeTrial && s.track != null && s.track.defaultLaps > 1;
            var items = battle || single || lastMan;
            var classes = Mathf.Max(1, config.difficulties.Length);
            rules.Choice("speed", "Engine class", "Kart speed, and how well the computer drivers race", () => classes,
                () => Mathf.Clamp((int)s.difficulty, 0, classes - 1), i => config.GetDifficulty((Difficulty)i).label, i =>
                {
                    s.difficulty = (Difficulty)i;
                    Flow.ShowcaseSelection();
                });
            if (items || champ) BindOpponents(s, battle || lastMan ? 1 : 0, champ);
            var afterVariants = rules.Container.childCount;
            if (single)
                rules.Number("flag", "Laps", "Track races the track's own lap count", 0, MaxLaps,
                    () => s.useTrackLaps ? 0 : Mathf.Clamp(s.laps, 1, MaxLaps), v => SelectLaps(s, v), lapsTrackText, lapsRangeFormat);
            // Time trials race one lap or the track's laps (MenuSelection.timeTrialLaps 1 or 0).
            if (timeTrialLaps)
                rules.Choice("flag", "Laps", null, () => 2, () => s.timeTrialLaps == 1 ? 0 : 1,
                    i => i == 0 ? oneLapText : string.Format(lapsFormat, s.track.defaultLaps), i => s.timeTrialLaps = i == 0 ? 1 : 0);
            if (items)
                rules.Link("redeem", "Powerups", "Which powerups the item boxes give", PowerupsValue(), () => Flow.Show<ItemOddsScreen>());
            BindVariantRows(s, afterVariants < rules.Container.childCount ? rules.Container[afterVariants] : null);
        }

        // Single races, battles, Last Man Standing and time trials race the picked variant of their track; cups race the
        // variants they set, a custom championship's races are set on its track list page. The rows go before the laps.
        void BindVariantRows(MenuSelection s, VisualElement before)
        {
            var variants = new TrackVariantRows(rules.Container, Root, settingRowTemplate, Click, before);
            var picked = (s.mode is GameMode.SingleRace or GameMode.Battle or GameMode.LastManStanding or GameMode.TimeTrial) && s.track != null;
            if (picked) variants.Show(s.track, s.variantKey);
            s.variantKey = variants.Variant != null ? variants.Variant.Key : "";
            variants.Changed += v => s.variantKey = v.Key;
            if (!variants.HasRows) return;
            // The variant rows (template rows, in modifier order) take the rule row look with their modifier's icon.
            var set = TrackVariants.Of(s.track);
            var templateRows = new List<VisualElement>();
            foreach (var row in rules.Container.Children())
                if (!row.ClassListContains(RuleRows.RowClass)) templateRows.Add(row);
            var m = 0;
            foreach (var row in templateRows)
            {
                while (m < set.Modifiers.Count && !set.Modifiers[m].IsChoice) m++;
                RuleRows.Adopt(row, m < set.Modifiers.Count ? ModifierIcon(set.Modifiers[m++].ModifierId) : "tune");
            }
        }

        static string ModifierIcon(string modifierId)
        {
            var id = modifierId ?? "";
            if (id.IndexOf("direction", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("reverse", StringComparison.OrdinalIgnoreCase) >= 0)
                return "compare_arrows";
            if (id.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0) return "timer";
            if (id.IndexOf("weather", StringComparison.OrdinalIgnoreCase) >= 0) return "landscape";
            return "tune";
        }

        // Single races may have no opponents, battles and Last Man Standing need one. Championships start with every other
        // driver until the player sets a number.
        void BindOpponents(MenuSelection s, int min, bool champ)
        {
            var max = MenuSelection.MaxOpponents;
            if (!champ) s.opponents = Mathf.Clamp(s.opponents, min, max);
            rules.Number("smart_toy", "Opponents", "Computer drivers in the race", min, max, () => champ ? s.ChampionshipOpponents : s.opponents, v =>
            {
                if (champ) s.championshipOpponents = v;
                else s.opponents = v;
            });
        }

        string PowerupsValue()
        {
            var own = ItemOdds.PlayerOdds(Content, Settings, odds);
            return ItemOdds.Total(odds) <= 0f ? offText : own ? customText : standardText;
        }

        // Laps value 0 is the track's own lap count, the others are that many laps.
        static void SelectLaps(MenuSelection s, int value)
        {
            s.useTrackLaps = value == 0;
            if (value > 0) s.laps = value;
        }

        static string ModeIcon(GameMode mode) => mode switch
        {
            GameMode.Championship => "emoji_events",
            GameMode.TimeTrial => "timer",
            GameMode.Battle => "swords",
            GameMode.LastManStanding => "person",
            _ => "play_arrow",
        };

        void BindPreview(MenuSelection s)
        {
            Q<VisualElement>("preview-image").SetImage(image);
            var name = Q<Label>("info-name");
            name.text = what;
            name.tooltip = what;
            SetVisible("info-driver", s.character != null);
            if (s.character == null) return;
            Thumb("info-character", s.character.Portrait, s.character.DisplayName);
            var kart = Q<VisualElement>("info-kart");
            var shown = s.kart != null && !s.kart.IsNoKart;
            kart.SetVisible(shown);
            if (shown) Thumb("info-kart", s.kart.Icon, s.kart.DisplayName);
        }

        void Thumb(string name, Texture2D icon, string displayName)
        {
            var thumb = Q<VisualElement>(name);
            thumb.SetImage(icon);
            thumb.tooltip = displayName;
        }
    }
}
