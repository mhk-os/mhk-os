using System;
using System.Collections.Generic;
using System.Globalization;
using MHK.Core;
using MHK.Powerups;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Race settings (RaceSettings.uxml) before a race, championship or time trial: difficulty (a time trial's engine
    /// class), opponents, the track's variant (<see cref="TrackVariantRows"/>, single race, battle and time trial), laps (a
    /// time trial: one lap or the track's) and powerups (rows that do not apply to the mode are hidden, Powerups opens the
    /// <see cref="ItemOddsScreen"/>), the track or cup and driver preview, and Start!.
    /// </summary>
    public class RaceSettingsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Laps value when the track's own lap count is used.")]
        string trackLapsText = "Track default";
        [SerializeField, Tooltip("Powerups value with the default item odds.")]
        string standardText = "Standard";
        [SerializeField, Tooltip("Powerups value with the player's own item odds.")]
        string customText = "Custom";
        [SerializeField, Tooltip("Powerups value when every item odd is 0 % (the race has no powerups).")]
        string offText = "Off";
        [SerializeField, Tooltip("Time trial laps values: a single lap, the track's laps ({0} = laps).")]
        string oneLapText = "1 lap", lapsFormat = "{0} laps";
        [SerializeField, Tooltip("Range hint of the Opponents input ({0} = fewest, {1} = most).")]
        string opponentsRangeFormat = "{0} to {1}";
        [SerializeField, Tooltip("Setting row template (Templates/SettingRow.uxml) of the track variant rows.")]
        VisualTreeAsset settingRowTemplate;

        const int MaxLaps = 9;

        readonly List<PowerupOdds> odds = new();

        Action start;
        string what;
        Texture2D image;

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
            BindRaceRows(s, GameRoot.Instance.Config);
            BindVariantRows(s);
            BindPreview(s);
            FocusTarget = OnClick("start", () => start());
        }

        // Single races, battles and time trials race the picked variant of their track; championships roll theirs per race
        // when they start (MenuSelection.BuildSession), a custom championship's races are set on its track list page.
        void BindVariantRows(MenuSelection s)
        {
            var container = Q<VisualElement>("variant-rows");
            var rows = new TrackVariantRows(container, Root, settingRowTemplate, Click);
            if ((s.mode is GameMode.SingleRace or GameMode.Battle or GameMode.TimeTrial) && s.track != null) rows.Show(s.track, s.variantKey);
            container.SetVisible(rows.HasRows);
            s.variantKey = rows.Variant != null ? rows.Variant.Key : "";
            rows.Changed += v => s.variantKey = v.Key;
        }

        void BindRaceRows(MenuSelection s, GameConfig config)
        {
            BindChoice("difficulty", () => s.difficulty, d => s.difficulty = d, d => config.GetDifficulty(d).label);
            var battle = s.mode == GameMode.Battle;
            var single = s.mode == GameMode.SingleRace;
            var champ = s.mode == GameMode.Championship;
            var timeTrialLaps = s.mode == GameMode.TimeTrial && s.track != null && s.track.defaultLaps > 1;
            SetVisible("opponents", battle || single || champ);
            SetVisible("laps", single || timeTrialLaps);
            SetVisible("powerups", battle || single);
            if (battle || single || champ) BindOpponents(s, battle ? 1 : 0, champ);
            if (battle || single)
                BindLink("powerups", PowerupsValue(), () => Flow.Show<ItemOddsScreen>());
            if (single)
                BindChoice("laps", () => MaxLaps + 1, () => s.useTrackLaps ? 0 : Mathf.Clamp(s.laps, 1, MaxLaps),
                    i => i == 0 ? trackLapsText : Number(i), i => SelectLaps(s, i));
            // Time trials race one lap or the track's laps (MenuSelection.timeTrialLaps 1 or 0).
            if (timeTrialLaps)
                BindChoice("laps", () => 2, () => s.timeTrialLaps == 1 ? 0 : 1,
                    i => i == 0 ? oneLapText : string.Format(lapsFormat, s.track.defaultLaps), i => s.timeTrialLaps = i == 0 ? 1 : 0);
        }

        // Single races may have no opponents, battles need one. Championships start with every other driver until the
        // player sets a number.
        void BindOpponents(MenuSelection s, int min, bool champ)
        {
            var max = MenuSelection.MaxOpponents;
            if (!champ) s.opponents = Mathf.Clamp(s.opponents, min, max);
            NumberInput.Bind(Q<VisualElement>("opponents"), min, max, () => champ ? s.ChampionshipOpponents : s.opponents, v =>
            {
                if (champ) s.championshipOpponents = v;
                else s.opponents = v;
            }, opponentsRangeFormat, Click);
        }

        string PowerupsValue()
        {
            var own = ItemOdds.PlayerOdds(Content, Settings, odds);
            return ItemOdds.Total(odds) <= 0f ? offText : own ? customText : standardText;
        }

        static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);

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
            _ => "play_arrow",
        };

        void BindPreview(MenuSelection s)
        {
            Q<VisualElement>("preview-image").SetImage(image);
            Q<Label>("info-name").text = what;
            SetVisible("info-driver", s.character != null);
            if (s.character == null) return;
            Thumb("info-character", s.character.Portrait, s.character.DisplayName);
            var kart = Q<VisualElement>("info-kart");
            kart.SetVisible(s.kart != null);
            if (s.kart != null) Thumb("info-kart", s.kart.Icon, s.kart.DisplayName);
        }

        void Thumb(string name, Texture2D icon, string displayName)
        {
            var thumb = Q<VisualElement>(name);
            thumb.SetImage(icon);
            thumb.tooltip = displayName;
        }
    }
}
