using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The menu's text question, built like <see cref="ConfirmPrompt"/>: a message, one text field (focused first) and
    /// Cancel / the confirm button on a glass panel over a dimmed screen. Enter in the field confirms, Down leaves the
    /// field for the buttons, Up returns to it; Back, Escape or a tap outside cancel. Closing gives the focus back to the
    /// element that opened it (when it is still shown), then runs the choice with the trimmed text.
    /// </summary>
    public sealed class TextPrompt
    {
        const string OverlayClass = "confirm-overlay";
        const string PanelClass = "confirm-panel";
        const string MessageClass = "confirm-message";
        const string ButtonsClass = "confirm-buttons";
        const string FieldClass = "acc-input";
        const string PromptFieldClass = "prompt-input";
        const string ShownClass = "shown";

        readonly VisualElement from, host, overlay;
        readonly TextField field;
        readonly List<Button> buttons = new();
        readonly Action onCancel;
        readonly Action<string> onConfirm;
        readonly bool allowEmpty;
        bool closed;

        /// <summary>
        /// Asks <paramref name="message"/> over the screen of <paramref name="from"/> with a text field of at most
        /// <paramref name="maxLength"/> characters starting with <paramref name="text"/> (<paramref name="hint"/> shows while it
        /// is empty). <paramref name="confirmLabel"/> is the confirm button's text, <paramref name="danger"/> makes it red.
        /// <paramref name="onConfirm"/> gets the trimmed text (confirming an empty field does nothing unless
        /// <paramref name="allowEmpty"/>), <paramref name="onCancel"/> runs on Cancel, Back or a tap outside. Null when
        /// <paramref name="from"/> is not shown.
        /// </summary>
        public static TextPrompt Show(VisualElement from, string message, string confirmLabel, Action<string> onConfirm,
            int maxLength = 256, string text = "", bool danger = true, bool allowEmpty = false, Action onCancel = null, string hint = "")
        {
            var host = ScreenOf(from);
            return host == null ? null : new TextPrompt(from, host, message, confirmLabel, onConfirm, maxLength, text, danger, allowEmpty, onCancel, hint);
        }

        TextPrompt(VisualElement from, VisualElement host, string message, string confirmLabel, Action<string> onConfirm,
            int maxLength, string text, bool danger, bool allowEmpty, Action onCancel, string hint)
        {
            this.from = from;
            this.host = host;
            this.onConfirm = onConfirm;
            this.onCancel = onCancel;
            this.allowEmpty = allowEmpty;
            overlay = new VisualElement();
            overlay.AddToClassList(OverlayClass);
            var panel = new VisualElement();
            panel.AddToClassList("panel");
            panel.AddToClassList(PanelClass);
            var label = new Label(message);
            label.AddToClassList(MessageClass);
            panel.Add(label);
            field = new TextField { maxLength = maxLength };
            field.AddToClassList(FieldClass);
            field.AddToClassList(PromptFieldClass);
            field.SetValueWithoutNotify(text ?? "");
            field.textEdition.placeholder = hint ?? "";
            panel.Add(field);
            var row = new VisualElement();
            row.AddToClassList(ButtonsClass);
            panel.Add(row);
            overlay.Add(panel);
            AddButton(row, ConfirmPrompt.CancelText, "secondary", Cancel);
            AddButton(row, confirmLabel, danger ? "danger" : "go", Confirm);
            Register();
            host.Add(overlay);
            UiGlass.Apply(host);
            field.Focus();
            overlay.schedule.Execute(() =>
            {
                overlay.AddToClassList(ShownClass);
                if (!closed) field.Focus();
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
        }

        void Register()
        {
            overlay.RegisterCallback<PointerDownEvent>(OnOverlayPointerDown);
            overlay.RegisterCallback<DetachFromPanelEvent>(OnDetach);
            field.RegisterCallback<KeyDownEvent>(OnFieldKey, TrickleDown.TrickleDown);
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
            var text = field.value.Trim();
            if (text.Length == 0 && !allowEmpty)
            {
                field.Focus();
                return;
            }
            Close(() => onConfirm?.Invoke(text));
        }

        void Cancel() => Close(onCancel);

        void Close(Action action)
        {
            if (closed) return;
            closed = true;
            UnityEngine.Object.FindAnyObjectByType<MenuFlow>()?.Click();
            overlay.RemoveFromHierarchy();
            if (from.panel != null) from.Focus();
            action?.Invoke();
        }

        void OnFieldKey(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            e.StopPropagation();
            Confirm();
        }

        void OnOverlayPointerDown(PointerDownEvent e)
        {
            if (e.target != overlay) return;
            e.StopPropagation();
            Cancel();
        }

        void OnCancel(NavigationCancelEvent e)
        {
            e.StopPropagation();
            Cancel();
        }

        // Submit on a button presses it; in the field it is typing (Enter is handled as a key).
        void OnSubmit(NavigationSubmitEvent e)
        {
            if (e.target is Button b && buttons.Contains(b)) return;
            e.StopPropagation();
        }

        void OnNavigate(NavigationMoveEvent e)
        {
            var at = e.target is Button b ? buttons.IndexOf(b) : -1;
            Focusable next;
            if (at < 0)
            {
                // In the field Left / Right move the caret; Down or Next go to the buttons.
                if (e.direction is NavigationMoveEvent.Direction.Left or NavigationMoveEvent.Direction.Right) return;
                next = e.direction is NavigationMoveEvent.Direction.Down or NavigationMoveEvent.Direction.Next ? buttons[0] : field;
            }
            else
                next = e.direction switch
                {
                    NavigationMoveEvent.Direction.Up => field,
                    NavigationMoveEvent.Direction.Previous => at == 0 ? field : buttons[at - 1],
                    NavigationMoveEvent.Direction.Left => buttons[Mathf.Max(0, at - 1)],
                    NavigationMoveEvent.Direction.Right or NavigationMoveEvent.Direction.Next => buttons[Mathf.Min(buttons.Count - 1, at + 1)],
                    _ => buttons[at],
                };
            next.Focus();
            e.StopPropagation();
            (e.target as Focusable)?.focusController?.IgnoreEvent(e);
        }
    }
}
