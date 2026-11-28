using System;
using System.Collections.Generic;
using MHK.Cosmetics;
using MHK.Modding;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Cosmetics (Cosmetics.uxml), opened by the hanger of the kart page and the race settings. A chip per slot, then
    /// None and a card per cosmetic of the slot. Picking a card wears the cosmetic on every driver (None takes the
    /// slot's one off, and a driver with a cosmetic of its own wears that one instead). The card shows it on the
    /// turntable. A locked card is only shown there, with the achievement that unlocks it. Below the cards the worn
    /// cosmetic takes one of the base colours, or the player's own one from the colour picker that the Custom swatch
    /// opens. Back returns and sends the cosmetics to the online lobby.
    /// </summary>
    public class CosmeticsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Slot chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Cosmetic card (Templates/PortraitCard.uxml).")]
        VisualTreeAsset cardTemplate;
        [SerializeField, Tooltip("Seconds between the cards' entry animations.")]
        float cardStep = 0.025f;
        [SerializeField, Tooltip("Entry animation delay of the first card, seconds.")]
        float cardFirst = 0.12f;
        [SerializeField, Tooltip("Title of the card that takes the slot's cosmetic off.")]
        string noneTitle = "None";
        [SerializeField, Tooltip("Title of that card when the shown driver has a cosmetic of its own in the slot, which it wears then.")]
        string ownTitle = "Own";
        [SerializeField, Tooltip("Chip of each slot, in CosmeticSlot order.")]
        string[] slotNames = { "Hats", "Body" };
        [SerializeField, Tooltip("Info panel name while the slot is empty, in CosmeticSlot order.")]
        string[] emptyNames = { "No hat", "Nothing on the back" };
        [SerializeField, Tooltip("Info panel line of a cosmetic of each slot, in CosmeticSlot order.")]
        string[] slotLines = { "Hat, every driver can wear it", "Body item, every driver can wear it" };
        [SerializeField, Tooltip("What unlocks a locked cosmetic ({0} = achievement title, {1} = its description).")]
        string unlockFormat = "Unlock it with the achievement {0}. {1}";
        [SerializeField, Tooltip("What unlocks a cosmetic of a secret achievement that is not unlocked yet.")]
        string secretUnlock = "Unlock it with a secret achievement.";
        [SerializeField, Tooltip("What unlocks a cosmetic whose achievement is not loaded.")]
        string missingUnlock = "Its achievement comes with a mod that is not loaded.";
        [SerializeField, Tooltip("Tooltip of the swatch that opens the colour picker.")]
        string customTip = "Custom colour";

        const string LockedClass = "locked", SelectedClass = "selected", CurrentClass = "current", LightClass = "light";
        // Swatches at least this bright get a dark check.
        const float LightLuminance = 0.6f;

        readonly List<(Button card, CosmeticDefinition item)> cards = new();
        readonly List<(Button swatch, Color32 color)> swatches = new();
        CosmeticSlot slot;
        CosmeticDefinition shown;
        VisualElement grid, tabs, colors, swatchRow, pickerPanel, pickerSquare, preview, infoSlot, infoLock;
        Label infoName, infoGame, infoSlotText, infoLockText, hex;
        Button customSwatch;
        ColorPicker picker;
        // The Custom swatch is chosen (the picker shows), also while the colour picked is one of the base colours.
        bool customOpen;

        protected override void Bind()
        {
            grid = Q<VisualElement>("card-grid");
            tabs = Q<VisualElement>("tabs");
            colors = Q<VisualElement>("cos-colors");
            swatchRow = Q<VisualElement>("cos-swatches");
            infoName = Q<Label>("info-name");
            infoGame = Q<Label>("info-game");
            infoSlot = Q<VisualElement>("info-slot");
            infoSlotText = Q<Label>("info-slot-text");
            infoLock = Q<VisualElement>("info-lock");
            infoLockText = Q<Label>("info-lock-text");
            pickerPanel = Q<VisualElement>("cos-picker");
            pickerSquare = Q<VisualElement>("cos-square");
            preview = Q<VisualElement>("cos-preview");
            hex = Q<Label>("cos-hex");
            picker = new ColorPicker(pickerSquare, Q<VisualElement>("cos-hues"));
            picker.Changed += color => SetColor(color, true);
            FillSwatches();
            KeepFocusedInView(Q<ScrollView>("card-scroll"), grid);
            grid.RegisterCallback<NavigationMoveEvent>(OnGridNavigate);
            tabs.RegisterCallback<NavigationMoveEvent>(OnTabsNavigate);
            swatchRow.RegisterCallback<NavigationMoveEvent>(OnSwatchesNavigate);
            FrameShowcaseIn(Q<VisualElement>("showcase-area"));
            FocusTarget = ShowSlot(slot);
        }

        protected override void Unbind()
        {
            base.Unbind();
            cards.Clear();
            swatches.Clear();
            picker?.Dispose();
            picker = null;
            Flow.FrameShowcase(null);
        }

        // The turntable goes back to what the player wears, the lobby learns it.
        protected override void GoBack()
        {
            Flow.ShowcaseSelection();
            OnlineLobby.RefreshCosmetics();
            base.GoBack();
        }

        // Fills the chips, cards and info for slot s and returns the card to focus (the shown one).
        VisualElement ShowSlot(CosmeticSlot s)
        {
            slot = s;
            var labels = new List<string>();
            foreach (CosmeticSlot each in Enum.GetValues(typeof(CosmeticSlot))) labels.Add(Text(slotNames, each, each.ToString()));
            ChipGroup.Fill(tabs, chipTemplate, labels, (int)slot, index =>
            {
                Click();
                FocusLater(ShowSlot((CosmeticSlot)index));
            });
            shown = CosmeticWardrobe.Worn(Settings, Content, slot);
            FillGrid();
            Describe();
            return CardOf(shown);
        }

        void FillGrid()
        {
            grid.Clear();
            cards.Clear();
            AddCard(null);
            foreach (var cosmetic in Content.All<CosmeticDefinition>())
                if (cosmetic.slot == slot) AddCard(cosmetic);
            UiAnim.Stagger(grid, cardStep, cardFirst, "pop");
            MarkCards();
        }

        void AddCard(CosmeticDefinition item)
        {
            var card = UiTree.Spawn<Button>(cardTemplate, grid);
            if (card == null) return;
            var own = item == null ? OwnIn(slot) : null;
            var face = item != null ? item : own;
            card.Q("card-image")?.SetImage(face != null ? face.Icon : null);
            if (face == null || face.Icon == null) AddSymbol(card.Q(className: "card-well") ?? card, face == null ? "close" : "checkroom");
            card.SetText("card-title", item != null ? item.DisplayName : own != null ? ownTitle : noneTitle);
            if (item != null && !CosmeticWardrobe.IsUnlocked(item))
            {
                card.AddToClassList(LockedClass);
                card.Add(LockBadge());
                card.tooltip = UnlockText(item);
            }
            card.clicked += () =>
            {
                Click();
                Pick(item);
            };
            cards.Add((card, item));
        }

        // The cosmetic the shown driver wears of its own in slot s, null for none.
        CosmeticDefinition OwnIn(CosmeticSlot s) => Selection.character != null ? Selection.character.OwnCosmetic(s) : null;

        static void AddSymbol(VisualElement well, string icon)
        {
            var symbol = new VisualElement { pickingMode = PickingMode.Ignore };
            symbol.AddToClassList("icon");
            symbol.AddToClassList("card-symbol");
            symbol.SetIcon(icon);
            well.Add(symbol);
        }

        static VisualElement LockBadge()
        {
            var badge = new VisualElement { pickingMode = PickingMode.Ignore };
            badge.AddToClassList("card-lock");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("icon");
            icon.SetIcon("lock");
            badge.Add(icon);
            return badge;
        }

        // An unlocked cosmetic (or None) is worn at once, a locked one only shown.
        void Pick(CosmeticDefinition item)
        {
            shown = item;
            if (item == null) CosmeticWardrobe.TakeOff(Settings, Content, slot);
            else if (CosmeticWardrobe.IsUnlocked(item)) CosmeticWardrobe.Wear(Settings, Content, item);
            MarkCards();
            Describe();
        }

        // The worn card has the check, the shown card the thick ring.
        void MarkCards()
        {
            var worn = CosmeticWardrobe.Worn(Settings, Content, slot);
            foreach (var (card, item) in cards)
            {
                card.EnableInClassList(SelectedClass, item == worn);
                card.EnableInClassList(CurrentClass, item == shown);
            }
        }

        VisualElement CardOf(CosmeticDefinition item)
        {
            foreach (var (card, cardItem) in cards)
                if (cardItem == item) return card;
            return cards.Count > 0 ? cards[0].card : null;
        }

        void Describe()
        {
            var wearable = shown == null || CosmeticWardrobe.IsUnlocked(shown);
            var own = shown == null ? OwnIn(slot) : null;
            infoName.text = shown != null ? shown.DisplayName : own != null ? own.DisplayName : Text(emptyNames, slot, noneTitle);
            infoGame.text = shown != null ? UiText.GameName(shown.SourceGame) : Text(slotNames, slot, slot.ToString());
            infoSlot.SetVisible(wearable);
            infoSlotText.text = Text(slotLines, slot, "");
            infoLock.SetVisible(!wearable);
            if (!wearable) infoLockText.text = UnlockText(shown);
            var tintable = shown != null && wearable && shown.tintable;
            colors.SetVisible(tintable);
            if (tintable)
            {
                var color = CosmeticWardrobe.ColorOf(Settings, shown);
                customOpen = BaseIndex(color) < 0;
                ShowColor(color, true);
            }
            Preview();
        }

        string UnlockText(CosmeticDefinition cosmetic)
        {
            var achievement = CosmeticWardrobe.UnlockedBy(cosmetic);
            if (achievement == null) return missingUnlock;
            return achievement.Secret && !achievement.Unlocked ? secretUnlock : string.Format(unlockFormat, achievement.Title, achievement.Description);
        }

        // The driver on the turntable wears the shown cosmetic in this slot and the worn ones in the others.
        void Preview()
        {
            var preview = new List<CosmeticChoice>();
            foreach (CosmeticSlot each in Enum.GetValues(typeof(CosmeticSlot)))
            {
                var cosmetic = each == slot ? shown : CosmeticWardrobe.Worn(Settings, Content, each);
                if (cosmetic != null) preview.Add(new CosmeticChoice(cosmetic.Id, CosmeticWardrobe.ColorOf(Settings, cosmetic)));
            }
            Flow.ShowOnTurntable(Selection.character, Selection.kart, preview);
        }

        void FillSwatches()
        {
            swatchRow.Clear();
            swatches.Clear();
            foreach (var color in CosmeticWardrobe.BaseColors)
            {
                var swatch = Swatch("check");
                swatch.style.backgroundColor = (Color)color;
                swatch.EnableInClassList(LightClass, Luminance(color) >= LightLuminance);
                var picked = color;
                swatch.clicked += () =>
                {
                    Click();
                    SetColor(picked, false);
                };
                swatches.Add((swatch, color));
            }
            customSwatch = Swatch("palette");
            customSwatch.AddToClassList("cos-custom");
            customSwatch.tooltip = customTip;
            customSwatch.clicked += () =>
            {
                Click();
                customOpen = true;
                ShowColor(CosmeticWardrobe.ColorOf(Settings, shown), true);
                FocusLater(pickerSquare);
            };
        }

        Button Swatch(string icon)
        {
            var swatch = new Button();
            swatch.AddToClassList("cos-swatch");
            var symbol = new VisualElement { pickingMode = PickingMode.Ignore };
            symbol.AddToClassList("icon");
            symbol.SetIcon(icon);
            swatch.Add(symbol);
            swatchRow.Add(swatch);
            return swatch;
        }

        static float Luminance(Color32 c) => (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;

        static int BaseIndex(Color32 color)
        {
            var colors = CosmeticWardrobe.BaseColors;
            for (var i = 0; i < colors.Count; i++)
                if (colors[i].r == color.r && colors[i].g == color.g && colors[i].b == color.b) return i;
            return -1;
        }

        // A base swatch closes the picker, the picker's own colours keep it open.
        void SetColor(Color32 color, bool custom)
        {
            if (shown == null) return;
            customOpen = custom;
            CosmeticWardrobe.SetColor(Settings, shown, color);
            ShowColor(color, false);
            Preview();
        }

        // Checks the base swatch of the colour, else the Custom swatch. That one then shows the colour and opens the
        // picker, which moves to the colour when asked.
        void ShowColor(Color32 color, bool movePicker)
        {
            var index = customOpen ? -1 : BaseIndex(color);
            for (var i = 0; i < swatches.Count; i++) swatches[i].swatch.EnableInClassList(SelectedClass, i == index);
            var custom = index < 0;
            customSwatch.EnableInClassList(SelectedClass, custom);
            customSwatch.style.backgroundColor = custom ? new StyleColor((Color)color) : new StyleColor(StyleKeyword.Null);
            customSwatch.EnableInClassList(LightClass, !custom || Luminance(color) >= LightLuminance);
            pickerPanel.SetVisible(custom);
            preview.style.backgroundColor = (Color)color;
            hex.text = "#" + ColorUtility.ToHtmlStringRGB(color);
            if (movePicker) picker.SetColor(color);
        }

        // By grid index like the selection pages. Up from the first row goes to the slot chips, down from the last row
        // to the colours (the footer while they are hidden).
        void OnGridNavigate(NavigationMoveEvent e)
        {
            if (e.target is not VisualElement card || card.parent != grid) return;
            var target = CardGridNavigation.Target(grid, card, e.direction);
            if (target == null && e.direction == NavigationMoveEvent.Direction.Up && tabs.childCount > 0)
                target = tabs.Q(className: SelectedClass) ?? tabs[0];
            if (target == null && e.direction == NavigationMoveEvent.Direction.Down)
                target = colors.resolvedStyle.display != DisplayStyle.None ? SwatchToFocus() : FooterButton;
            CardGridNavigation.Move(e, target);
        }

        void OnTabsNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Down || cards.Count == 0) return;
            CardGridNavigation.Move(e, CardOf(shown));
        }

        void OnSwatchesNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Up || cards.Count == 0) return;
            CardGridNavigation.Move(e, CardOf(shown));
        }

        VisualElement SwatchToFocus()
        {
            foreach (var (swatch, _) in swatches)
                if (swatch.ClassListContains(SelectedClass)) return swatch;
            return swatches.Count > 0 ? swatches[0].swatch : null;
        }

        static string Text(string[] texts, CosmeticSlot s, string fallback) =>
            texts != null && (int)s < texts.Length && !string.IsNullOrEmpty(texts[(int)s]) ? texts[(int)s] : fallback;
    }
}
