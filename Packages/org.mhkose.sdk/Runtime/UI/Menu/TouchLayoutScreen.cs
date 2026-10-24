using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Touch layout editor (TouchLayout.uxml): the race screen with the HUD and the touch controls, scaled to fit.
    /// Drag a control to move it, pinch or use the mouse wheel or the sliders to resize it, add controls from the palette.
    /// Save stores the layout in <see cref="UserSettings.touchLayout"/>, Back discards the changes.
    /// </summary>
    public class TouchLayoutScreen : MenuScreen
    {
        [SerializeField, Tooltip("Control names in TouchControlType order (palette tooltips, picked control caption).")]
        string[] controlNames =
        {
            "Throttle", "Brake", "Steer Left", "Steer Right", "Item", "Look Back", "Camera", "Respawn", "Pause", "Horn",
            "Joystick", "Steering Wheel",
        };
        [SerializeField, Tooltip("Race HUD (RaceHud.uxml) shown with its sample values behind the controls.")]
        VisualTreeAsset hudMock;
        [SerializeField, Tooltip("Opacity an invisible control (opacity 0) shows with here, so it can still be found.")]
        [Range(0f, 1f)] float hiddenOpacity = 0.3f;
        [SerializeField, Tooltip("Size change per mouse wheel step.")]
        float wheelScaleStep = 0.05f;

        readonly List<TouchControlSetup> layout = new();
        readonly List<TouchControlElement> elements = new();
        VisualElement frame, screen, controlsLayer, pickedGroup;
        Slider sizeSlider, opacitySlider, deadZoneSlider;
        TouchControlElement picked;
        Vector2 screenSize, grab, dragAt, pinchAt;
        int dragPointer = -1, pinchPointer = -1;
        float pinchDistance, pinchScale;
        bool isDefault;

        protected override void Bind()
        {
            frame = Q<VisualElement>("tl-frame");
            screen = Q<VisualElement>("tl-screen");
            controlsLayer = Q<VisualElement>("tl-controls");
            pickedGroup = Q<VisualElement>("tl-picked");
            frame.RegisterCallback<GeometryChangedEvent>(_ => FitScreen());
            BindBackdrop();
            BindHud();
            BindPalette();
            BindSliders();
            BindPointers();
            OnClick("tl-remove", RemovePicked);
            OnClick("reset", ResetLayout);
            OnClick("save", Save, click: false);
            var saved = Settings.touchLayout;
            isDefault = !saved.custom;
            Load(saved.Controls);
        }

        protected override void Unbind()
        {
            base.Unbind();
            layout.Clear();
            elements.Clear();
            picked = null;
            dragPointer = pinchPointer = -1;
        }

        void Load(IReadOnlyList<TouchControlSetup> controls)
        {
            layout.Clear();
            layout.AddRange(TouchLayoutSettings.Copy(controls));
            elements.Clear();
            controlsLayer.Clear();
            for (var i = 0; i < layout.Count; i++) AddElement(layout[i]);
            Pick(elements.Count > 0 ? elements[0] : null);
        }

        TouchControlElement AddElement(TouchControlSetup setup)
        {
            var e = new TouchControlElement(setup);
            e.Apply(hiddenOpacity);
            e.RegisterCallback<PointerDownEvent>(ev => OnControlDown(e, ev), TrickleDown.TrickleDown);
            controlsLayer.Add(e);
            elements.Add(e);
            return e;
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
            Q<VisualElement>("tl-backdrop").style.backgroundImage = picture;
        }

        void BindHud()
        {
            var host = Q<VisualElement>("tl-hud");
            host.Clear();
            if (hudMock == null) return;
            hudMock.CloneTree(host);
            host.Q<VisualElement>("hud")?.AddToClassList("touch");
            var minimap = host.Q<Minimap>("minimap");
            if (minimap != null) minimap.TouchLayout = true;
            var speedometer = host.Q<HudSpeedometer>("speedometer");
            if (speedometer != null) speedometer.TouchLayout = true;
            host.NoPick();
        }

        void BindPalette()
        {
            var palette = Q<VisualElement>("tl-palette");
            palette.Clear();
            var types = (TouchControlType[])Enum.GetValues(typeof(TouchControlType));
            foreach (var type in types)
            {
                var button = new Button { tooltip = NameOf(type) };
                button.AddToClassList("mhk-button");
                button.AddToClassList("small");
                button.AddToClassList("icon-only");
                button.AddToClassList("tl-add");
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("icon");
                icon.AddToClassList(TouchControlElement.IconClass(type));
                if (type == TouchControlType.Throttle) icon.style.rotate = new Rotate(-90f);
                if (type == TouchControlType.Brake) icon.style.rotate = new Rotate(90f);
                button.Add(icon);
                var t = type;
                button.clicked += () =>
                {
                    Click();
                    AddControl(t);
                };
                palette.Add(button);
            }
        }

        string NameOf(TouchControlType type)
        {
            var i = (int)type;
            return controlNames != null && i < controlNames.Length ? controlNames[i] : type.ToString();
        }

        void BindSliders()
        {
            sizeSlider = Q<Slider>("tl-size");
            sizeSlider.lowValue = TouchLayoutSettings.MinScale;
            sizeSlider.highValue = TouchLayoutSettings.MaxScale;
            sizeSlider.RegisterValueChangedCallback(e => EditPicked(s => s.scale = e.newValue));
            opacitySlider = Q<Slider>("tl-opacity");
            opacitySlider.RegisterValueChangedCallback(e => EditPicked(s => s.opacity = e.newValue));
            deadZoneSlider = Q<Slider>("tl-dead-zone");
            deadZoneSlider.highValue = TouchLayoutSettings.MaxDeadZone;
            deadZoneSlider.RegisterValueChangedCallback(e => EditPicked(s => s.deadZone = e.newValue));
        }

        void EditPicked(Action<TouchControlSetup> edit)
        {
            if (picked == null) return;
            edit(picked.Setup);
            picked.Apply(hiddenOpacity);
            isDefault = false;
        }

        void Pick(TouchControlElement e)
        {
            if (picked != null) picked.RemoveFromClassList("tl-picked-control");
            picked = e;
            pickedGroup.SetVisible(e != null);
            if (e == null) return;
            e.AddToClassList("tl-picked-control");
            e.BringToFront();
            var s = e.Setup;
            pickedGroup.SetText("tl-picked-name", NameOf(s.type));
            sizeSlider.SetValueWithoutNotify(s.scale);
            opacitySlider.SetValueWithoutNotify(s.opacity);
            deadZoneSlider.SetValueWithoutNotify(s.deadZone);
            deadZoneSlider.SetVisible(s.type is TouchControlType.Joystick or TouchControlType.Wheel);
        }

        void AddControl(TouchControlType type)
        {
            var setup = new TouchControlSetup { type = type };
            layout.Add(setup);
            Place(setup, screenSize * 0.5f);
            Pick(AddElement(setup));
            isDefault = false;
        }

        void RemovePicked()
        {
            if (picked == null) return;
            var i = elements.IndexOf(picked);
            layout.Remove(picked.Setup);
            elements.RemoveAt(i);
            picked.RemoveFromHierarchy();
            picked = null;
            Pick(elements.Count > 0 ? elements[Mathf.Min(i, elements.Count - 1)] : null);
            isDefault = false;
        }

        void ResetLayout()
        {
            Load(TouchLayoutSettings.DefaultControls);
            isDefault = true;
        }

        void Save()
        {
            if (isDefault) Settings.touchLayout.Reset();
            else Settings.touchLayout.Save(layout);
            Flow.Back();
        }

        // The race screen keeps its full UI size (so controls sit exactly where the race puts them) and is scaled down.
        void FitScreen()
        {
            var size = Root.panel != null ? Root.panel.visualTree.layout.size : Vector2.zero;
            var room = frame.layout.size;
            if (size.x <= 0f || size.y <= 0f || float.IsNaN(room.x) || room.x <= 0f) return;
            screenSize = size;
            var k = Mathf.Min(room.x / size.x, room.y / size.y);
            screen.style.width = size.x;
            screen.style.height = size.y;
            screen.style.scale = new Scale(new Vector3(k, k, 1f));
            screen.style.left = (room.x - size.x * k) * 0.5f;
            screen.style.top = (room.y - size.y * k) * 0.5f;
        }

        void Place(TouchControlSetup setup, Vector2 centre)
        {
            var half = TouchControlElement.BaseSize(setup.type) * setup.scale * 0.5f;
            centre.x = Mathf.Clamp(centre.x, Mathf.Min(half, screenSize.x * 0.5f), Mathf.Max(screenSize.x - half, screenSize.x * 0.5f));
            centre.y = Mathf.Clamp(centre.y, Mathf.Min(half, screenSize.y * 0.5f), Mathf.Max(screenSize.y - half, screenSize.y * 0.5f));
            setup.anchor = new Vector2(centre.x < screenSize.x * 0.5f ? 0f : 1f, centre.y < screenSize.y * 0.5f ? 0f : 1f);
            setup.offset = centre - Vector2.Scale(setup.anchor, screenSize);
        }

        static Vector2 CentreOf(TouchControlSetup setup, Vector2 screenSize) => setup.offset + Vector2.Scale(setup.anchor, screenSize);

        void BindPointers()
        {
            screen.RegisterCallback<PointerDownEvent>(OnScreenDown);
            screen.RegisterCallback<PointerMoveEvent>(OnScreenMove);
            screen.RegisterCallback<PointerUpEvent>(ev => EndPointer(ev.pointerId));
            screen.RegisterCallback<PointerCancelEvent>(ev => EndPointer(ev.pointerId));
            screen.RegisterCallback<WheelEvent>(OnWheel);
        }

        void OnControlDown(TouchControlElement e, PointerDownEvent ev)
        {
            if (dragPointer >= 0) return;
            Pick(e);
            var at = ScreenPoint(ev.position);
            dragPointer = ev.pointerId;
            dragAt = at;
            grab = CentreOf(e.Setup, screenSize) - at;
            screen.CapturePointer(ev.pointerId);
            ev.StopPropagation();
        }

        // A second finger while one drags a control starts a pinch that resizes it.
        void OnScreenDown(PointerDownEvent ev)
        {
            if (dragPointer < 0 || pinchPointer >= 0 || picked == null || ev.pointerId == dragPointer) return;
            pinchPointer = ev.pointerId;
            pinchAt = ScreenPoint(ev.position);
            pinchDistance = Mathf.Max(1f, Vector2.Distance(dragAt, pinchAt));
            pinchScale = picked.Setup.scale;
            screen.CapturePointer(ev.pointerId);
        }

        void OnScreenMove(PointerMoveEvent ev)
        {
            if (picked == null || (ev.pointerId != dragPointer && ev.pointerId != pinchPointer)) return;
            var at = ScreenPoint(ev.position);
            if (ev.pointerId == pinchPointer) pinchAt = at;
            else dragAt = at;
            if (pinchPointer >= 0) Resize(pinchScale * Vector2.Distance(dragAt, pinchAt) / pinchDistance);
            else
            {
                Place(picked.Setup, at + grab);
                picked.Apply(hiddenOpacity);
                isDefault = false;
            }
        }

        void EndPointer(int pointerId)
        {
            if (pointerId != dragPointer && pointerId != pinchPointer) return;
            if (screen.HasPointerCapture(pointerId)) screen.ReleasePointer(pointerId);
            if (pointerId == dragPointer) dragPointer = -1;
            pinchPointer = -1;
            if (dragPointer >= 0 && picked != null) grab = CentreOf(picked.Setup, screenSize) - dragAt;
        }

        void OnWheel(WheelEvent ev)
        {
            if (picked == null || ev.delta.y == 0f) return;
            Resize(picked.Setup.scale - Mathf.Sign(ev.delta.y) * wheelScaleStep);
            ev.StopPropagation();
        }

        void Resize(float scale)
        {
            var s = Mathf.Clamp(scale, TouchLayoutSettings.MinScale, TouchLayoutSettings.MaxScale);
            sizeSlider.SetValueWithoutNotify(s);
            EditPicked(setup => setup.scale = s);
            Place(picked.Setup, CentreOf(picked.Setup, screenSize));
            picked.Apply(hiddenOpacity);
        }

        Vector2 ScreenPoint(Vector2 panelPosition) => screen.WorldToLocal(panelPosition);
    }
}
