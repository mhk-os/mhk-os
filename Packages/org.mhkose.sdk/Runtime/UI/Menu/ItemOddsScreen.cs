using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Powerups (ItemOdds.uxml): single race and battle item odds per powerup, grouped by game or mod, for the item family
    /// of the picked track. Every odd at 0 % turns powerups off. Saved per family in <see cref="UserSettings.itemOdds"/>.
    /// </summary>
    public class ItemOddsScreen : MenuScreen
    {
        sealed class Row
        {
            public int index;
            public int order;
            public ContentPack pack;
            public VisualElement element;
            public Label value;
        }

        const float MaxPercent = 100f;
        const float SnapTolerance = 0.001f;
        const float HalfShownDecimal = 0.05f;

        [SerializeField, Tooltip("Heading of a game or mod (Templates/ItemOddsGroup.uxml).")]
        VisualTreeAsset groupTemplate;
        [SerializeField, Tooltip("One powerup (Templates/ItemOddsRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Sum line. {0} = sum of all odds.")]
        string totalFormat = "Total {0}";
        [SerializeField, Tooltip("Appended to the sum line when the sum is not 100 %.")]
        string scaledText = ", scaled to 100 % in the race";
        [SerializeField, Tooltip("Appended to the sum line. {0} = item family, {1} = its share, {2} = share of all other powerups.")]
        string familyFormat = "  |  {0} items {1}  |  other games {2}";
        [SerializeField, Tooltip("Tag of a powerup another game shares with the track's family. {0} = family.")]
        string sharedFormat = "Also a {0} item";
        [SerializeField, Tooltip("Percent step of Left / Right and the arrow buttons.")]
        [Min(0.1f)] float step = 1f;
        [SerializeField, Tooltip("Arrow buttons held down: milliseconds before they repeat, and between repeats.")]
        Vector2Int repeatMs = new(400, 70);
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.012f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.12f;

        readonly List<PowerupOdds> odds = new();
        readonly List<PowerupOdds> defaults = new();
        readonly List<Row> rows = new();
        readonly List<(ContentPack pack, Label total)> groups = new();
        ContentPack family;
        Label total, off;
        Button footerTarget;
        Row lastRow;

        void Reset() => showcaseVisible = false;

        string FamilyKey => ItemOdds.FamilyKey(family);

        protected override void Bind()
        {
            family = ItemOdds.FamilyOf(Content, Selection.track);
            ItemOdds.Defaults(Content, family, defaults);
            ItemOdds.PlayerOdds(Content, Settings, Selection.track, odds);
            total = Q<Label>("odds-total");
            off = Q<Label>("odds-off");
            var list = Q<VisualElement>("odds-list");
            FillList(list);
            var scroll = Q<ScrollView>("odds-scroll");
            scroll.verticalScroller.slider.focusable = false;
            KeepFocusedInView(scroll, list);
            list.RegisterCallback<NavigationMoveEvent>(OnNavigate);
            list.RegisterCallback<NavigationSubmitEvent>(OnSubmit);
            Q<VisualElement>("footer").RegisterCallback<NavigationMoveEvent>(OnFooterNavigate);
            OnClick("reset", ResetToDefaults);
            footerTarget = OnClick("all-off", AllOff);
            FocusTarget = rows.Count > 0 ? rows[0].element : null;
            Refresh();
        }

        protected override void Unbind()
        {
            base.Unbind();
            rows.Clear();
            groups.Clear();
            footerTarget = null;
            lastRow = null;
        }

        void FillList(VisualElement list)
        {
            list.Clear();
            rows.Clear();
            groups.Clear();
            using var _ = ListPool<ContentPack>.Get(out var packs);
            CollectPacks(packs);
            foreach (var pack in packs)
            {
                var head = UiTree.Spawn<VisualElement>(groupTemplate, list);
                if (head == null) return;
                head.SetText("odds-group-name", pack.title);
                groups.Add((pack, head.Q<Label>("odds-group-total")));
                for (var i = 0; i < odds.Count; i++)
                {
                    var p = Content.Get<PowerupDefinition>(odds[i].id);
                    if (p != null && p.OwnerPack == pack && !SpawnRow(list, p, i)) return;
                }
            }
            UiAnim.Stagger(list, rowStep, rowFirst, "from-left");
        }

        // Packs of the listed powerups in load order, the track's family first.
        void CollectPacks(List<ContentPack> packs)
        {
            for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < odds.Count; i++)
            {
                var p = Content.Get<PowerupDefinition>(odds[i].id);
                if (p == null || p.OwnerPack == null || packs.Contains(p.OwnerPack)) continue;
                if ((Content.RootOf(p.OwnerPack) == family) == (pass == 0)) packs.Add(p.OwnerPack);
            }
        }

        bool SpawnRow(VisualElement list, PowerupDefinition p, int index)
        {
            var element = UiTree.Spawn<VisualElement>(rowTemplate, list);
            if (element == null) return false;
            var row = new Row { index = index, order = rows.Count, pack = p.OwnerPack, element = element, value = element.Q<Label>("odds-value") ?? new Label() };
            element.focusable = true;
            element.userData = row;
            element.EnableInClassList("alt", rows.Count % 2 == 1);
            element.Q("odds-icon")?.SetImage(p.Icon);
            element.SetText("odds-name", p.DisplayName);
            var shared = family != null && ItemOdds.FamilyOf(Content, p) != family && ItemOdds.InFamily(Content, p, family);
            var tag = element.Q<Label>("odds-tag");
            tag?.SetVisible(shared);
            if (shared && tag != null) tag.text = string.Format(sharedFormat, family.title);
            BindArrow(element.Q<Button>("odds-minus"), row, -1);
            BindArrow(element.Q<Button>("odds-plus"), row, 1);
            element.RegisterCallback<PointerDownEvent>(_ => element.Focus());
            element.RegisterCallback<FocusInEvent>(_ => lastRow = row);
            rows.Add(row);
            return true;
        }

        void BindArrow(Button button, Row row, int direction)
        {
            if (button == null) return;
            button.focusable = false;
            button.clickable = new Clickable(() => Step(row, direction), repeatMs.x, repeatMs.y);
        }

        // Rows handle all four directions: Left / Right change the odds, Up / Down walk the rows (the panel's own
        // navigation jumps to rows scrolled out of view), Down from the last row goes to the footer.
        void OnNavigate(NavigationMoveEvent e)
        {
            if (e.target is not VisualElement element || element.userData is not Row row) return;
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Left: Step(row, -1); break;
                case NavigationMoveEvent.Direction.Right: Step(row, 1); break;
                case NavigationMoveEvent.Direction.Up: if (row.order > 0) rows[row.order - 1].element.Focus(); break;
                case NavigationMoveEvent.Direction.Down: (row.order + 1 < rows.Count ? rows[row.order + 1].element : footerTarget)?.Focus(); break;
                default: return;
            }
            e.StopPropagation();
            element.focusController?.IgnoreEvent(e);
        }

        void OnFooterNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Up || rows.Count == 0) return;
            (lastRow ?? rows[^1]).element.Focus();
            e.StopPropagation();
            if (e.target is VisualElement from) from.focusController?.IgnoreEvent(e);
        }

        void OnSubmit(NavigationSubmitEvent e)
        {
            if (e.target is not VisualElement element || element.userData is not Row row) return;
            var fallback = defaults[row.index].percent > 0f ? defaults[row.index].percent : step;
            Click();
            SetOdds(row.index, odds[row.index].percent > 0f ? 0f : fallback);
            e.StopPropagation();
        }

        // Steps snap to whole multiples of the step: 4.7 goes up to 5 and down to 4.
        void Step(Row row, int direction)
        {
            var v = odds[row.index].percent;
            var next = direction > 0
                ? Mathf.Min(MaxPercent, (Mathf.Floor(v / step + SnapTolerance) + 1f) * step)
                : Mathf.Max(0f, (Mathf.Ceil(v / step - SnapTolerance) - 1f) * step);
            if (Mathf.Abs(next - v) < SnapTolerance) return;
            Click();
            SetOdds(row.index, next);
        }

        void SetOdds(int index, float percent)
        {
            var id = odds[index].id;
            OwnSet().Set(id, percent);
            odds[index] = new PowerupOdds(id, percent);
            Refresh();
        }

        // The family's own set, created from the odds shown (so later pack changes do not shift them).
        ItemOddsSet OwnSet()
        {
            var own = Settings.ItemOddsFor(FamilyKey);
            if (own != null) return own;
            own = new ItemOddsSet { family = FamilyKey };
            own.odds.AddRange(odds);
            Settings.itemOdds.Add(own);
            return own;
        }

        void AllOff()
        {
            var own = OwnSet();
            for (var i = 0; i < odds.Count; i++)
            {
                own.Set(odds[i].id, 0f);
                odds[i] = new PowerupOdds(odds[i].id, 0f);
            }
            Refresh();
        }

        void ResetToDefaults()
        {
            var key = FamilyKey;
            Settings.itemOdds.RemoveAll(s => s == null || s.family == key);
            ItemOdds.PlayerOdds(Content, Settings, Selection.track, odds);
            Refresh();
        }

        void Refresh()
        {
            foreach (var row in rows)
            {
                var percent = odds[row.index].percent;
                row.value.text = UiText.Percent(percent);
                row.element.EnableInClassList("zero", percent <= 0f);
            }
            foreach (var (pack, label) in groups)
                if (label != null) label.text = UiText.Percent(PackTotal(pack));
            var sum = ItemOdds.Total(odds);
            total.SetVisible(sum > 0f);
            off.SetVisible(sum <= 0f);
            total.text = SumText(sum);
        }

        string SumText(float sum)
        {
            var text = string.Format(totalFormat, UiText.Percent(sum));
            if (Mathf.Abs(sum - MaxPercent) >= HalfShownDecimal) text += scaledText;
            if (family == null) return text;
            var own = FamilyShare(sum);
            return text + string.Format(familyFormat, family.title, UiText.Percent(own), UiText.Percent(MaxPercent - own));
        }

        float PackTotal(ContentPack pack)
        {
            var sum = 0f;
            foreach (var row in rows)
                if (row.pack == pack) sum += odds[row.index].percent;
            return sum;
        }

        // Percent of all rolls (sum scaled to 100) that give a powerup of the track's family.
        float FamilyShare(float sum)
        {
            if (sum <= 0f) return 0f;
            var own = 0f;
            for (var i = 0; i < odds.Count; i++)
            {
                var p = Content.Get<PowerupDefinition>(odds[i].id);
                if (p != null && ItemOdds.InFamily(Content, p, family)) own += odds[i].percent;
            }
            return own * MaxPercent / sum;
        }
    }
}
