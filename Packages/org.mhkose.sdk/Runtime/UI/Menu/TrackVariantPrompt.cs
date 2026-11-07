using System;
using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The menu's track variant question, built like <see cref="ConfirmPrompt"/>: the track's name, one setting row per
    /// modifier the track offers a choice of (<see cref="TrackVariantRows"/>, focused first) and Cancel / the confirm
    /// button on a glass panel over a dimmed screen. Up and Down walk the rows and the buttons, Left and Right change a
    /// row's option or walk the buttons; Back, Escape or a tap outside cancel. Closing gives the focus back to the
    /// element that opened it (when it is still shown), then runs the choice with the variant key.
    /// </summary>
    public sealed class TrackVariantPrompt
    {
        const string OverlayClass = "confirm-overlay";
        const string PanelClass = "confirm-panel";
        const string MessageClass = "confirm-message";
        const string ButtonsClass = "confirm-buttons";
        const string ShownClass = "shown";

        readonly VisualElement from, host, overlay;
        readonly TrackVariantRows rows;
        // Focus order: the rows' value buttons, then Cancel and the confirm button.
        readonly List<Button> items = new();
        readonly List<Button> buttons = new();
        readonly Action onCancel;
        readonly Action<string> onConfirm;
        bool closed;

        /// <summary>
        /// Asks for a variant of <paramref name="track"/> over the screen of <paramref name="from"/>, starting at its default.
        /// <paramref name="rowTemplate"/> is the setting row (Templates/SettingRow.uxml), <paramref name="confirmLabel"/> the
        /// confirm button's text. <paramref name="onConfirm"/> gets the chosen variant key, <paramref name="onCancel"/> runs on
        /// Cancel, Back or a tap outside. Null when <paramref name="from"/> is not shown.
        /// </summary>
        public static TrackVariantPrompt Show(VisualElement from, TrackDefinition track, VisualTreeAsset rowTemplate, string confirmLabel,
            Action<string> onConfirm, Action onCancel = null)
        {
            var host = ScreenOf(from);
            return host == null || track == null ? null : new TrackVariantPrompt(from, host, track, rowTemplate, confirmLabel, onConfirm, onCancel);
        }

        TrackVariantPrompt(VisualElement from, VisualElement host, TrackDefinition track, VisualTreeAsset rowTemplate, string confirmLabel,
            Action<string> onConfirm, Action onCancel)
        {
            this.from = from;
            this.host = host;
            this.onConfirm = onConfirm;
            this.onCancel = onCancel;
            overlay = new VisualElement();
            overlay.AddToClassList(OverlayClass);
            var panel = new VisualElement();
            panel.AddToClassList("panel");
            panel.AddToClassList(PanelClass);
            var text = new Label(track.DisplayName);
            text.AddToClassList(MessageClass);
            panel.Add(text);
            var list = new VisualElement();
            panel.Add(list);
            var row = new VisualElement();
            row.AddToClassList(ButtonsClass);
            panel.Add(row);
            overlay.Add(panel);
            rows = new TrackVariantRows(list, host, rowTemplate, Click);
            rows.Show(track, "");
            list.Query<Button>().ForEach(items.Add);
            AddButton(row, ConfirmPrompt.CancelText, "secondary", Cancel);
            AddButton(row, confirmLabel, "go", Confirm);
            Register();
            host.Add(overlay);
            UiGlass.Apply(host);
            var first = items[0];
            first.Focus();
            overlay.schedule.Execute(() =>
            {
                overlay.AddToClassList(ShownClass);
                if (!closed) first.Focus();
            });
        }

        // The screen root the overlay covers: the topmost element below the panel's root.
        static VisualElement ScreenOf(VisualElement from)
        {
            var top = from?.panel?.visualTree;
            if (top == null) return null;
            var e = from;
            while (e.parent != null && e.parent != top) e = e.parent;
            return e;
        }

        static void Click() => UnityEngine.Object.FindAnyObjectByType<MenuFlow>()?.Click();

        void AddButton(VisualElement row, string label, string kind, Action action)
        {
            var b = new Button();
            b.AddToClassList("mhk-button");
            b.AddToClassList(kind);
            b.Add(new Label(label) { pickingMode = PickingMode.Ignore });
            b[0].AddToClassList("button-label");
            b.clicked += action;
            row.Add(b);
            buttons.Add(b);
            items.Add(b);
        }

        void Register()
        {
            overlay.RegisterCallback<PointerDownEvent>(OnOverlayPointerDown);
            overlay.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            // On the screen root, so the prompt keeps navigation, submit and cancel wherever the focus is while it is open.
            host.RegisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            host.RegisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            host.RegisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
        }

        void OnDetach(DetachFromPanelEvent e)
        {
            closed = true;
            host.UnregisterCallback<NavigationMoveEvent>(OnNavigate, TrickleDown.TrickleDown);
            host.UnregisterCallback<NavigationCancelEvent>(OnCancel, TrickleDown.TrickleDown);
            host.UnregisterCallback<NavigationSubmitEvent>(OnSubmit, TrickleDown.TrickleDown);
        }

        void Confirm()
        {
            var key = rows.Variant?.Key ?? "";
            Close(() => onConfirm?.Invoke(key));
        }

        void Cancel() => Close(onCancel);

        void Close(Action action)
        {
            if (closed) return;
            closed = true;
            Click();
            overlay.RemoveFromHierarchy();
            if (from.panel != null) from.Focus();
            action?.Invoke();
        }

        // A row's option list (ChoiceList) lies outside the overlay: its own events are left to it.
        bool Outside(EventBase e) => e.target is not VisualElement v || !overlay.Contains(v);

        void OnOverlayPointerDown(PointerDownEvent e)
        {
            if (e.target != overlay) return;
            e.StopPropagation();
            Cancel();
        }

        void OnCancel(NavigationCancelEvent e)
        {
            if (Outside(e)) return;
            e.StopPropagation();
            Cancel();
        }

        // Submit on a row or a button presses it.
        void OnSubmit(NavigationSubmitEvent e)
        {
            if (Outside(e) || e.target is Button b && items.Contains(b)) return;
            e.StopPropagation();
        }

        void OnNavigate(NavigationMoveEvent e)
        {
            if (Outside(e)) return;
            var at = e.target is Button b ? items.IndexOf(b) : -1;
            var firstButton = items.Count - buttons.Count;
            var onButtons = at >= firstButton;
            // Left / Right on a row step its option (SettingChoice).
            if (at >= 0 && !onButtons && e.direction is NavigationMoveEvent.Direction.Left or NavigationMoveEvent.Direction.Right) return;
            var next = e.direction switch
            {
                NavigationMoveEvent.Direction.Up => onButtons ? firstButton - 1 : at - 1,
                NavigationMoveEvent.Direction.Down => onButtons ? at : at + 1,
                NavigationMoveEvent.Direction.Left => Mathf.Max(firstButton, at - 1),
                NavigationMoveEvent.Direction.Previous => at - 1,
                NavigationMoveEvent.Direction.Right or NavigationMoveEvent.Direction.Next => at + 1,
                _ => at,
            };
            items[at < 0 ? 0 : Mathf.Clamp(next, 0, items.Count - 1)].Focus();
            e.StopPropagation();
            (e.target as Focusable)?.focusController?.IgnoreEvent(e);
        }
    }
}
