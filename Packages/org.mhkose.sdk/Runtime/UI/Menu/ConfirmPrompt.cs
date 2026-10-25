using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The menu's confirmation prompt: a message on a glass panel over a dimmed screen with Cancel (focused first) and the
    /// red confirm button. Left and Right walk the buttons, Back, Escape or a tap outside cancel. Closing gives the focus
    /// back to the element that opened it (when it is still shown), then runs the choice.
    /// </summary>
    public sealed class ConfirmPrompt
    {
        const string OverlayClass = "confirm-overlay";
        const string PanelClass = "confirm-panel";
        const string MessageClass = "confirm-message";
        const string ButtonsClass = "confirm-buttons";
        const string ShownClass = "shown";

        /// <summary>Text of the cancel button.</summary>
        public static string CancelText { get; set; } = "Cancel";

        readonly VisualElement from, host, overlay, panel;
        readonly List<Button> buttons = new();
        readonly Action onCancel;
        bool closed;

        /// <summary>The red confirm button (for a tooltip).</summary>
        public Button ConfirmButton { get; }

        /// <summary>
        /// Asks <paramref name="message"/> over the screen of <paramref name="from"/>. <paramref name="confirmLabel"/> is the
        /// red button's text (Delete, Reset, ...), <paramref name="onConfirm"/> runs when it is chosen, <paramref name="onCancel"/>
        /// on Cancel, Back or a tap outside. Null when <paramref name="from"/> is not shown.
        /// </summary>
        public static ConfirmPrompt Show(VisualElement from, string message, string confirmLabel, Action onConfirm, Action onCancel = null)
        {
            var host = ScreenOf(from);
            return host == null ? null : new ConfirmPrompt(from, host, message, confirmLabel, onConfirm, onCancel);
        }

        ConfirmPrompt(VisualElement from, VisualElement host, string message, string confirmLabel, Action onConfirm, Action onCancel)
        {
            this.from = from;
            this.host = host;
            this.onCancel = onCancel;
            overlay = new VisualElement();
            overlay.AddToClassList(OverlayClass);
            panel = new VisualElement();
            panel.AddToClassList("panel");
            panel.AddToClassList(PanelClass);
            var text = new Label(message);
            text.AddToClassList(MessageClass);
            panel.Add(text);
            var row = new VisualElement();
            row.AddToClassList(ButtonsClass);
            panel.Add(row);
            overlay.Add(panel);
            var cancel = AddButton(row, CancelText, "secondary", onCancel);
            ConfirmButton = AddButton(row, confirmLabel, "danger", onConfirm);
            Register();
            host.Add(overlay);
            UiGlass.Apply(host);
            cancel.Focus();
            overlay.schedule.Execute(() =>
            {
                overlay.AddToClassList(ShownClass);
                if (!closed) cancel.Focus();
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

        Button AddButton(VisualElement row, string label, string kind, Action action)
        {
            var b = new Button();
            b.AddToClassList("mhk-button");
            b.AddToClassList(kind);
            b.Add(new Label(label) { pickingMode = PickingMode.Ignore });
            b[0].AddToClassList("button-label");
            b.clicked += () => Close(action, true);
            row.Add(b);
            buttons.Add(b);
            return b;
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

        void Close(Action action, bool click)
        {
            if (closed) return;
            closed = true;
            if (click) UnityEngine.Object.FindAnyObjectByType<MenuFlow>()?.Click();
            overlay.RemoveFromHierarchy();
            if (from.panel != null) from.Focus();
            action?.Invoke();
        }

        void OnOverlayPointerDown(PointerDownEvent e)
        {
            if (e.target != overlay) return;
            e.StopPropagation();
            Close(onCancel, true);
        }

        void OnCancel(NavigationCancelEvent e)
        {
            e.StopPropagation();
            Close(onCancel, true);
        }

        void OnSubmit(NavigationSubmitEvent e)
        {
            if (e.target is Button b && buttons.Contains(b)) return;
            e.StopPropagation();
        }

        void OnNavigate(NavigationMoveEvent e)
        {
            var at = e.target is Button b ? buttons.IndexOf(b) : -1;
            var next = e.direction switch
            {
                NavigationMoveEvent.Direction.Left or NavigationMoveEvent.Direction.Previous => at - 1,
                NavigationMoveEvent.Direction.Right or NavigationMoveEvent.Direction.Next => at + 1,
                _ => at,
            };
            buttons[at < 0 ? 0 : Mathf.Clamp(next, 0, buttons.Count - 1)].Focus();
            e.StopPropagation();
            (e.target as Focusable)?.focusController?.IgnoreEvent(e);
        }
    }
}
