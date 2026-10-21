using System;
using System.Collections.Generic;
using MHK.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Keyboard keys (Controls.uxml): one row per gameplay action with two key slots, rebound with Input System
    /// interactive rebinding. A key already used elsewhere swaps places. Saved as overrides in <see cref="UserSettings.keyBindings"/>.
    /// </summary>
    public class ControlsScreen : MenuScreen
    {
        /// <summary>One listed action: its keyboard bindings (plain ones, or the parts of one composite side).</summary>
        [Serializable]
        public class ActionRow
        {
            [Tooltip("Row caption.")]
            public string label;
            [Tooltip("Action in the 'Kart' map of the controls asset.")]
            public string action;
            [Tooltip("Composite part to list (negative / positive of the steering axis). Empty = the action's plain bindings.")]
            public string part;

            public ActionRow(string label, string action, string part = "")
            {
                this.label = label;
                this.action = action;
                this.part = part;
            }
        }

        const string KeyboardGroup = "Keyboard";
        const string ActionMap = "Kart";
        const int SlotsPerRow = 2;

        [SerializeField, Tooltip("Gameplay actions asset (MHKControls) whose keyboard bindings are listed. Each listed action needs two " +
                                 "keyboard bindings (an empty path = a free slot).")]
        InputActionAsset controls;
        [SerializeField, Tooltip("One action row (Templates/ControlRow.uxml: label ctl-action, key buttons ctl-key-0 / ctl-key-1).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Listed actions, top to bottom.")]
        ActionRow[] rows =
        {
            new("Steer left", "Steer", "negative"),
            new("Steer right", "Steer", "positive"),
            new("Throttle", "Throttle"),
            new("Brake", "Brake"),
            new("Fire", "Fire"),
            new("Look back", "LookBack"),
            new("Horn", "Horn"),
            new("Respawn", "Respawn"),
            new("Pause", "Pause"),
            new("Camera", "Camera"),
        };
        [SerializeField, Tooltip("Key slot text while it waits for a key.")]
        string listeningText = "Press a key…";
        [SerializeField, Tooltip("Key slot text without a key.")]
        string emptyText = "—";
        [SerializeField, Tooltip("Put before numpad key names when the keyboard layout names them like main keys (\"-\" → \"Num -\").")]
        string numpadPrefix = "Num ";
        [SerializeField, Tooltip("Seconds after a key was taken during which menu keys (Enter, Esc, arrows) are ignored.")]
        float menuKeyCooldown = 0.3f;

        sealed class Slot
        {
            public ActionRow row;
            public InputAction action;
            public int binding;
            public Button button;
        }

        readonly List<Slot> slots = new();
        InputActionAsset actions;
        InputActionRebindingExtensions.RebindingOperation rebind;
        float rebindEnd = float.NegativeInfinity;

        void Reset() => showcaseVisible = false;

        /// <summary>A key is being captured, or was just captured (its key press must not drive the menu).</summary>
        bool CapturingKeys => rebind != null || Time.unscaledTime < rebindEnd + menuKeyCooldown;

        protected override void Bind()
        {
            slots.Clear();
            actions = Instantiate(controls);
            actions.LoadBindingOverridesFromJson(Settings.keyBindings);
            FillRows(actions.FindActionMap(ActionMap, true));
            RefreshSlots();
            OnClick("reset", ResetToDefaults);
            RegisterCaptureGuards();
        }

        protected override void Unbind()
        {
            base.Unbind();
            StopRebind();
            slots.Clear();
            if (actions != null) Destroy(actions);
            actions = null;
        }

        void FillRows(InputActionMap map)
        {
            var list = Q<VisualElement>("ctl-list");
            list.Clear();
            var index = 0;
            foreach (var r in rows)
            {
                var action = map.FindAction(r.action);
                if (action == null)
                {
                    Debug.LogWarning($"[Controls] No action '{r.action}' in {controls.name}.", this);
                    continue;
                }
                if (!AddRow(list, r, action, index++ % 2 == 1)) break;
            }
        }

        bool AddRow(VisualElement list, ActionRow r, InputAction action, bool alt)
        {
            var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
            if (row == null) return false;
            row.EnableInClassList("alt", alt);
            row.SetText("ctl-action", r.label);
            var bindings = KeyboardBindings(action, r.part);
            for (var k = 0; k < SlotsPerRow; k++)
            {
                var button = row.Q<Button>($"ctl-key-{k}");
                if (button == null) continue;
                if (k >= bindings.Count)
                {
                    button.SetVisible(false);
                    continue;
                }
                var slot = new Slot { row = r, action = action, binding = bindings[k], button = button };
                slots.Add(slot);
                button.clicked += () => StartRebind(slot);
            }
            return true;
        }

        void RegisterCaptureGuards()
        {
            // Trickle-down on the root runs before MenuScreen's bubble-up cancel handler, so stopping here keeps the screen open.
            Root.RegisterCallback<KeyDownEvent>(SwallowWhileCapturing, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationCancelEvent>(SwallowWhileCapturing, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationSubmitEvent>(SwallowWhileCapturing, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationMoveEvent>(SwallowWhileCapturing, TrickleDown.TrickleDown);
        }

        void SwallowWhileCapturing(EventBase e)
        {
            if (CapturingKeys) e.StopImmediatePropagation();
        }

        static List<int> KeyboardBindings(InputAction action, string part)
        {
            var result = new List<int>();
            var bindings = action.bindings;
            for (var i = 0; i < bindings.Count; i++)
            {
                var b = bindings[i];
                if (b.isComposite || b.groups == null || !b.groups.Contains(KeyboardGroup)) continue;
                var wanted = string.IsNullOrEmpty(part)
                    ? !b.isPartOfComposite
                    : b.isPartOfComposite && string.Equals(b.name, part, StringComparison.OrdinalIgnoreCase);
                if (wanted) result.Add(i);
            }
            return result;
        }

        void StartRebind(Slot slot)
        {
            if (CapturingKeys && rebind == null) return; // the key that just finished a rebind clicked the slot again
            Click();
            StopRebind();
            var oldPath = slot.action.bindings[slot.binding].effectivePath;
            slot.button.text = listeningText;
            slot.button.AddToClassList("listening");
            rebind = slot.action.PerformInteractiveRebinding(slot.binding)
                .WithControlsHavingToMatchPath("<Keyboard>")
                .WithControlsExcluding("<Keyboard>/anyKey")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnComplete(op =>
                {
                    if (op != rebind) return;
                    FinishRebind(op);
                    ResolveConflicts(slot, oldPath);
                    Save();
                })
                .OnCancel(FinishRebind)
                .Start();
        }

        void FinishRebind(InputActionRebindingExtensions.RebindingOperation op)
        {
            if (op != rebind) return;
            op.Dispose();
            rebind = null;
            rebindEnd = Time.unscaledTime;
            RefreshSlots();
        }

        void StopRebind()
        {
            if (rebind == null) return;
            var op = rebind;
            rebind = null;
            op.Cancel();
            op.Dispose();
            RefreshSlots();
        }

        void ResolveConflicts(Slot slot, string oldPath)
        {
            var newPath = slot.action.bindings[slot.binding].effectivePath;
            SetPath(slot, newPath); // drops the override when the new key is the default one
            foreach (var other in slots)
            {
                if (other == slot) continue;
                var path = other.action.bindings[other.binding].effectivePath;
                if (SameKey(path, newPath)) SetPath(other, SameKey(oldPath, newPath) ? "" : oldPath ?? "");
            }
            RefreshSlots();
        }

        static void SetPath(Slot slot, string path)
        {
            var binding = slot.action.bindings[slot.binding];
            if (string.Equals(binding.path ?? "", path ?? "", StringComparison.OrdinalIgnoreCase)) slot.action.RemoveBindingOverride(slot.binding);
            else slot.action.ApplyBindingOverride(slot.binding, path ?? "");
        }

        // Equal paths, the same key (paths may name a key by its label), or an either-side modifier and one of its sides.
        static bool SameKey(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            var ka = InputSystem.FindControl(a);
            var kb = InputSystem.FindControl(b);
            if (ka == null || kb == null) return false;
            return ka == kb || EitherSide(ka, kb) || EitherSide(kb, ka);
        }

        static bool EitherSide(InputControl modifier, InputControl key)
        {
            if (modifier.device is not Keyboard k) return false;
            return modifier == k.shiftKey && (key == k.leftShiftKey || key == k.rightShiftKey)
                || modifier == k.ctrlKey && (key == k.leftCtrlKey || key == k.rightCtrlKey)
                || modifier == k.altKey && (key == k.leftAltKey || key == k.rightAltKey);
        }

        void ResetToDefaults()
        {
            StopRebind();
            actions.RemoveAllBindingOverrides();
            Save();
            RefreshSlots();
        }

        void Save()
        {
            Settings.keyBindings = actions.SaveBindingOverridesAsJson();
            GameRoot.Instance.SaveSettings();
        }

        void RefreshSlots()
        {
            foreach (var s in slots)
            {
                var text = s.action.GetBindingDisplayString(s.binding);
                var empty = string.IsNullOrEmpty(text);
                if (!empty && IsNumpad(s) && !text.StartsWith(numpadPrefix.Trim(), StringComparison.OrdinalIgnoreCase))
                    text = numpadPrefix + text;
                s.button.text = empty ? emptyText : text;
                s.button.EnableInClassList("empty", empty);
                s.button.RemoveFromClassList("listening");
            }
        }

        // Keyboard layouts name numpad keys like the main ones ("-"), so they get a prefix.
        static bool IsNumpad(Slot s) => s.action.bindings[s.binding].effectivePath.Contains("/numpad", StringComparison.OrdinalIgnoreCase);
    }
}
