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
    /// Powerups (ItemOdds.uxml): single race and battle item odds per powerup, grouped by game or mod, the same on every
    /// track. Every odd at 0 % turns powerups off. Saved in <see cref="UserSettings.ownItemOdds"/>.
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
        [SerializeField, Tooltip("Percent step of Left / Right and the arrow buttons.")]
        [Min(0.1f)] float step = 1f;
        [SerializeField, Tooltip("Arrow buttons held down: milliseconds before they repeat, and between repeats.")]
        Vector2Int repeatMs = new(400, 70);
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.012f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.12f;
        [SerializeField, Tooltip("Question of Defaults, and its confirm button text.")]
        string resetQuestion = "Set the item odds back to the defaults?", resetText = "Reset";

        readonly List<PowerupOdds> odds = new();
        readonly List<PowerupOdds> defaults = new();
        readonly List<Row> rows = new();
        readonly List<(ContentPack pack, Label total)> groups = new();
        Label total, off;
        Button footerTarget;
        Row lastRow;

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            ItemOdds.Defaults(Content, null, defaults);
            ItemOdds.PlayerOdds(Content, Settings, odds);
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
            OnClick("reset", () => ConfirmPrompt.Show(Q<Button>("reset"), resetQuestion, resetText, ResetToDefaults));
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

        // Packs of the listed powerups in load order.
        void CollectPacks(List<ContentPack> packs)
        {
            for (var i = 0; i < odds.Count; i++)
            {
                var p = Content.Get<PowerupDefinition>(odds[i].id);
                if (p != null && p.OwnerPack != null && !packs.Contains(p.OwnerPack)) packs.Add(p.OwnerPack);
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
            SetOwn(OwnOdds(), id, percent);
            odds[index] = new PowerupOdds(id, percent);
            Refresh();
        }

        // The player's own odds, created from the odds shown (so later pack changes do not shift them).
        List<PowerupOdds> OwnOdds()
        {
            var own = Settings.ownItemOdds;
            if (own.Count == 0) own.AddRange(odds);
            return own;
        }

        static void SetOwn(List<PowerupOdds> own, string id, float percent)
        {
            for (var i = 0; i < own.Count; i++)
            {
                if (own[i].id != id) continue;
                own[i] = new PowerupOdds(id, percent);
                return;
            }
            own.Add(new PowerupOdds(id, percent));
        }

        void AllOff()
        {
            var own = OwnOdds();
            for (var i = 0; i < odds.Count; i++)
            {
                SetOwn(own, odds[i].id, 0f);
                odds[i] = new PowerupOdds(odds[i].id, 0f);
            }
            Refresh();
        }

        void ResetToDefaults()
        {
            Settings.ownItemOdds.Clear();
            ItemOdds.PlayerOdds(Content, Settings, odds);
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
            return Mathf.Abs(sum - MaxPercent) >= HalfShownDecimal ? text + scaledText : text;
        }

        float PackTotal(ContentPack pack)
        {
            var sum = 0f;
            foreach (var row in rows)
                if (row.pack == pack) sum += odds[row.index].percent;
            return sum;
        }
    }
}
