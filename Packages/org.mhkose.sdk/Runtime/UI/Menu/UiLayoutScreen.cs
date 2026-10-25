using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// UI Layout editor (UiLayout.uxml): the race screen with the HUD (sample values) and, while touch controls are on, the
    /// touch controls, scaled to fit. Drag an element to move it (it snaps to the screen and the other elements), drag a
    /// handle of the picked element to scale it, add elements from the add list. Save stores the layout in
    /// <see cref="UserSettings.uiLayout"/>, Back discards the changes.
    /// </summary>
    public partial class UiLayoutScreen : MenuScreen
    {
        sealed class Item
        {
            public HudElementSetup hud;
            public TouchControlSetup control;
            public TouchControlElement element;
        }

        [SerializeField, Tooltip("Control names in TouchControlType order (add list, picked element caption).")]
        string[] controlNames =
        {
            "Throttle", "Brake", "Steer Left", "Steer Right", "Item", "Look Back", "Camera", "Respawn", "Pause", "Horn",
            "Joystick", "Steering Wheel",
        };
        [SerializeField, Tooltip("HUD element names in HudElementType order (add list, picked element caption).")]
        string[] hudNames =
        {
            "Race Position", "Item Slot", "Lap", "Time", "Best Lap", "Split Time", "Health", "Speedometer", "Minimap",
            "Replay Info", "Countdown", "Final Lap", "Wrong Way", "Messages",
        };
        [SerializeField, Tooltip("Race HUD (RaceHud.uxml) shown with its sample values behind the controls.")]
        VisualTreeAsset hudMock;
        [SerializeField, Tooltip("Opacity a hidden element (or one at opacity 0) shows with here, so it can still be found.")]
        [Range(0f, 1f)] float hiddenOpacity = 0.3f;
        [SerializeField, Tooltip("Scale change per mouse wheel step.")]
        float wheelScaleStep = 0.05f;
        [SerializeField, Tooltip("Distance in UI reference pixels one direction key or D-pad press moves the picked element.")]
        float keyStep = 8f;
        [SerializeField, Tooltip("Distance in pixels of the shown (scaled) race screen within which an edge or centre snaps to a guide.")]
        float snapDistance = 8f;
        [SerializeField, Tooltip("Remove prompt ({0} = element name).")]
        string removeFormat = "Remove {0} from the race screen?";
        [SerializeField, Tooltip("Confirm button of the remove prompt.")]
        string removeLabel = "Remove";
        [SerializeField, Tooltip("Reset prompt.")]
        string resetMessage = "Put every element back to its default place and size?";
        [SerializeField, Tooltip("Confirm button of the reset prompt.")]
        string resetLabel = "Reset";
        [SerializeField, Tooltip("Visibility button text while the picked element is drawn.")]
        string shownText = "Shown";
        [SerializeField, Tooltip("Visibility button text while the picked element is hidden.")]
        string hiddenText = "Hidden";

        static readonly string[] PreviewClasses = { "preview-messages", "preview-battle", "preview-best-lap", "preview-item" };
        static readonly TouchControlType[] TouchTypes = (TouchControlType[])Enum.GetValues(typeof(TouchControlType));

        readonly List<HudElementSetup> hudLayout = new();
        readonly List<TouchControlSetup> controls = new();
        readonly Item[] hudItems = new Item[UiLayoutSettings.HudTypes.Length];
        readonly List<Item> controlItems = new();
        readonly List<Item> items = new();
        HudLayout hudView;
        Minimap minimap;
        VisualElement frame, screen, controlsLayer, pickedGroup, addList, addEmpty, visibleIcon, mockHud;
        Slider scaleXSlider, scaleYSlider, sizeSlider, opacitySlider, deadZoneSlider;
        Button visibleButton, moveButton, removeButton;
        Label visibleLabel;
        Item picked;
        IVisualElementScheduledItem ticker;
        bool touchMode, isDefault;

        protected override void Bind()
        {
            frame = Q<VisualElement>("ul-frame");
            screen = Q<VisualElement>("ul-screen");
            controlsLayer = Q<VisualElement>("ul-controls");
            pickedGroup = Q<VisualElement>("ul-picked");
            addList = Q<VisualElement>("ul-add-list");
            addEmpty = Q<VisualElement>("ul-add-empty");
            touchMode = Settings.TouchControlsActive;
            BindBackdrop();
            BindHud();
            BindPanel();
            BindCanvas();
            OnClick("reset", () => ConfirmPrompt.Show(Q<Button>("reset"), resetMessage, resetLabel, ResetLayout));
            OnClick("save", Save, click: false);
            var saved = Settings.uiLayout;
            Load(saved.CopyHud(), saved.Controls);
            isDefault = !saved.custom;
            FocusTarget = Q<Button>("ul-next");
            ticker = Root.schedule.Execute(Tick).Every(0);
        }

        protected override void Unbind()
        {
            base.Unbind();
            ticker?.Pause();
            ticker = null;
            hudLayout.Clear();
            controls.Clear();
            controlItems.Clear();
            items.Clear();
            Array.Clear(hudItems, 0, hudItems.Length);
            picked = null;
            hudView = null;
            mockHud = null;
            minimap = null;
            EndDrag();
        }

        void Load(List<HudElementSetup> hud, IReadOnlyList<TouchControlSetup> touch)
        {
            hudLayout.Clear();
            hudLayout.AddRange(hud);
            controls.Clear();
            controls.AddRange(UiLayoutSettings.Copy(touch));
            controlsLayer.Clear();
            controlItems.Clear();
            for (var i = 0; i < hudLayout.Count; i++)
            {
                hudItems[(int)hudLayout[i].type] = new Item { hud = hudLayout[i] };
                hudView?.Apply(hudLayout[i]);
            }
            if (touchMode)
                for (var i = 0; i < controls.Count; i++) controlItems.Add(AddControlElement(controls[i]));
            RebuildItems();
            FillAddList();
            Pick(items.Count > 0 ? items[0] : null);
        }

        void RebuildItems()
        {
            items.Clear();
            for (var i = 0; i < hudLayout.Count; i++)
                if (hudLayout[i].shown) items.Add(hudItems[(int)hudLayout[i].type]);
            items.AddRange(controlItems);
        }

        Item AddControlElement(TouchControlSetup setup)
        {
            var e = new TouchControlElement(setup) { pickingMode = PickingMode.Ignore };
            e.Apply(hiddenOpacity);
            controlsLayer.Add(e);
            return new Item { control = setup, element = e };
        }

        void BindBackdrop()
        {
            var track = Selection.track;
            if (track == null || track.previewImage == null)
            {
                var tracks = Content.All<TrackDefinition>();
                for (var i = 0; i < tracks.Count && (track == null || track.previewImage == null); i++) track = tracks[i];
            }
            var picture = track != null ? track.previewImage != null ? track.previewImage : track.Icon : null;
            Q<VisualElement>("ul-backdrop").style.backgroundImage = picture;
        }

        // The mock shows every element at once (best lap, split, health, replay plate, held item, the messages). The
        // preview classes go on after the entry effects, which remove them from fresh clones.
        protected override void AfterBind()
        {
            base.AfterBind();
            if (mockHud == null) return;
            foreach (var preview in PreviewClasses) mockHud.AddToClassList(preview);
        }

        void BindHud()
        {
            var host = Q<VisualElement>("ul-hud");
            host.Clear();
            hudView = null;
            minimap = null;
            mockHud = null;
            if (hudMock == null) return;
            hudMock.CloneTree(host);
            var hud = host.Q<VisualElement>("hud");
            if (hud == null) return;
            hud.EnableInClassList("touch", touchMode);
            mockHud = hud;
            host.Q<VisualElement>("split-row")?.SetVisible(true);
            host.Q<VisualElement>("replay-plate")?.SetVisible(true);
            minimap = host.Q<Minimap>("minimap");
            if (minimap != null) minimap.TouchMode = touchMode;
            var speedometer = host.Q<HudSpeedometer>("speedometer");
            if (speedometer != null) speedometer.TouchMode = touchMode;
            host.NoPick();
            hudView = new HudLayout(hud, hiddenOpacity);
        }

        void Tick()
        {
            var map = hudLayout.Count > 0 ? Hud(HudElementType.Minimap) : null;
            if (minimap != null && map != null && map.shown) minimap.TickSample(map.scale);
            UpdateGizmo();
        }

        HudElementSetup Hud(HudElementType type) => hudLayout[(int)type];

        void BindPanel()
        {
            OnClick("ul-prev", () => Step(-1));
            OnClick("ul-next", () => Step(1));
            scaleXSlider = BindScale("ul-scale-x", (s, v) => new Vector2(v, s.y));
            scaleYSlider = BindScale("ul-scale-y", (s, v) => new Vector2(s.x, v));
            sizeSlider = BindScale("ul-size", (_, v) => new Vector2(v, v));
            opacitySlider = Q<Slider>("ul-opacity");
            opacitySlider.RegisterValueChangedCallback(e => Edit(h => h.opacity = e.newValue, c => c.opacity = e.newValue));
            deadZoneSlider = Q<Slider>("ul-dead-zone");
            deadZoneSlider.highValue = UiLayoutSettings.MaxDeadZone;
            deadZoneSlider.RegisterValueChangedCallback(e => Edit(null, c => c.deadZone = e.newValue));
            visibleButton = OnClick("ul-visible", ToggleVisible);
            visibleIcon = Q<VisualElement>("ul-visible-icon");
            visibleLabel = Q<Label>("ul-visible-label");
            moveButton = OnClick("ul-move", () => frame.Focus());
            removeButton = OnClick("ul-remove", AskRemove);
        }

        Slider BindScale(string name, Func<Vector2, float, Vector2> scale)
        {
            var slider = Q<Slider>(name);
            slider.lowValue = UiLayoutSettings.MinScale;
            slider.highValue = UiLayoutSettings.MaxScale;
            slider.RegisterValueChangedCallback(e =>
            {
                if (picked != null) ScaleAround(picked, scale(ScaleOf(picked), e.newValue), AnchorOf(picked));
            });
            return slider;
        }

        void Edit(Action<HudElementSetup> hud, Action<TouchControlSetup> control)
        {
            if (picked == null) return;
            if (picked.hud != null)
            {
                if (hud == null) return;
                hud(picked.hud);
                hudView?.Apply(picked.hud);
            }
            else
            {
                if (control == null) return;
                control(picked.control);
                picked.element.Apply(hiddenOpacity);
            }
            isDefault = false;
        }

        void Pick(Item item)
        {
            picked = item;
            shownBox = Rect.zero;
            pickedGroup.SetEnabled(item != null);
            if (item == null)
            {
                pickedGroup.SetText("ul-picked-name", "");
                return;
            }
            pickedGroup.SetText("ul-picked-name", NameOf(item));
            var hud = item.hud != null;
            scaleXSlider.SetVisible(hud);
            scaleYSlider.SetVisible(hud);
            sizeSlider.SetVisible(!hud);
            deadZoneSlider.SetVisible(!hud && item.control.type is TouchControlType.Joystick or TouchControlType.Wheel);
            item.element?.BringToFront();
            RefreshPanel();
        }

        void RefreshPanel()
        {
            if (picked == null) return;
            var scale = ScaleOf(picked);
            scaleXSlider.SetValueWithoutNotify(scale.x);
            scaleYSlider.SetValueWithoutNotify(scale.y);
            sizeSlider.SetValueWithoutNotify(scale.x);
            opacitySlider.SetValueWithoutNotify(picked.hud != null ? picked.hud.opacity : picked.control.opacity);
            if (picked.control != null) deadZoneSlider.SetValueWithoutNotify(picked.control.deadZone);
            var visible = picked.hud != null ? picked.hud.visible : picked.control.visible;
            visibleIcon.EnableInClassList("i-visibility", visible);
            visibleIcon.EnableInClassList("i-visibility_off", !visible);
            visibleLabel.text = visible ? shownText : hiddenText;
        }

        void Step(int direction)
        {
            if (items.Count == 0) return;
            var i = picked != null ? items.IndexOf(picked) : -1;
            Pick(items[((i + direction) % items.Count + items.Count) % items.Count]);
        }

        void ToggleVisible()
        {
            Edit(h => h.visible = !h.visible, c => c.visible = !c.visible);
            RefreshPanel();
        }

        void AskRemove()
        {
            if (picked == null) return;
            var item = picked;
            ConfirmPrompt.Show(removeButton, string.Format(removeFormat, NameOf(item)), removeLabel, () => Remove(item));
        }

        void Remove(Item item)
        {
            var i = items.IndexOf(item);
            if (i < 0) return;
            if (item.hud != null)
            {
                item.hud.shown = false;
                hudView?.Apply(item.hud);
            }
            else
            {
                controls.Remove(item.control);
                controlItems.Remove(item);
                item.element.RemoveFromHierarchy();
            }
            isDefault = false;
            RebuildItems();
            FillAddList();
            Pick(items.Count > 0 ? items[Mathf.Min(i, items.Count - 1)] : null);
        }

        void FillAddList()
        {
            addList.Clear();
            for (var i = 0; i < hudLayout.Count; i++)
            {
                var setup = hudLayout[i];
                if (!setup.shown) addList.Add(AddRow(NameOf(setup.type), HudIcon(setup.type), 0f, () => ShowHud(setup)));
            }
            if (touchMode)
                foreach (var type in TouchTypes)
                    if (!HasControl(type)) addList.Add(AddRow(NameOf(type), TouchControlElement.IconClass(type), IconTurn(type), () => AddControl(type)));
            addEmpty.SetVisible(addList.childCount == 0);
        }

        Button AddRow(string name, string iconClass, float turn, Action add)
        {
            var row = new Button();
            row.AddToClassList("mhk-button");
            row.AddToClassList("small");
            row.AddToClassList("secondary");
            row.AddToClassList("ul-add-row");
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("icon");
            icon.AddToClassList(iconClass);
            if (turn != 0f) icon.style.rotate = new Rotate(turn);
            row.Add(icon);
            var label = new Label(name) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("button-label");
            row.Add(label);
            row.clicked += () =>
            {
                Click();
                var index = addList.IndexOf(row);
                add();
                FocusLater(addList.childCount > 0 ? addList[Mathf.Min(index, addList.childCount - 1)] : moveButton);
            };
            return row;
        }

        bool HasControl(TouchControlType type)
        {
            for (var i = 0; i < controls.Count; i++)
                if (controls[i].type == type) return true;
            return false;
        }

        void ShowHud(HudElementSetup setup)
        {
            setup.shown = true;
            setup.visible = true;
            hudView?.Apply(setup);
            isDefault = false;
            RebuildItems();
            FillAddList();
            Pick(hudItems[(int)setup.type]);
        }

        void AddControl(TouchControlType type)
        {
            var setup = new TouchControlSetup { type = type };
            controls.Add(setup);
            Place(setup, screenSize * 0.5f);
            var item = AddControlElement(setup);
            controlItems.Add(item);
            isDefault = false;
            RebuildItems();
            FillAddList();
            Pick(item);
        }

        void ResetLayout()
        {
            Load(new UiLayoutSettings().CopyHud(), UiLayoutSettings.DefaultControls);
            isDefault = true;
        }

        void Save()
        {
            if (isDefault) Settings.uiLayout.Reset();
            else Settings.uiLayout.Save(hudLayout, controls);
            Flow.Back();
        }

        string NameOf(Item item) => item.hud != null ? NameOf(item.hud.type) : NameOf(item.control.type);

        string NameOf(TouchControlType type)
        {
            var i = (int)type;
            return controlNames != null && i < controlNames.Length ? controlNames[i] : type.ToString();
        }

        string NameOf(HudElementType type)
        {
            var i = (int)type;
            return hudNames != null && i < hudNames.Length ? hudNames[i] : type.ToString();
        }

        static float IconTurn(TouchControlType type) => type switch
        {
            TouchControlType.Throttle => -90f,
            TouchControlType.Brake => 90f,
            _ => 0f,
        };

        static string HudIcon(HudElementType type) => type switch
        {
            HudElementType.Position => "i-leaderboard",
            HudElementType.Item => "i-inventory_2",
            HudElementType.Lap => "i-flag",
            HudElementType.Time => "i-timer",
            HudElementType.BestLap => "i-star",
            HudElementType.Split => "i-compare_arrows",
            HudElementType.Health => "i-favorite",
            HudElementType.Speedometer => "i-speed",
            HudElementType.Minimap => "i-map",
            HudElementType.Replay => "i-movie",
            HudElementType.Countdown => "i-counter_3",
            HudElementType.FinalLap => "i-sports_score",
            HudElementType.WrongWay => "i-warning",
            _ => "i-chat",
        };
    }
}
