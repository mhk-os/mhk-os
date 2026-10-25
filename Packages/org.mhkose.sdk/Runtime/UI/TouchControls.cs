using System.Collections.Generic;
using MHK.Core;
using MHK.Input;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// On-screen touch controls (TouchControls.uxml) built from the player's layout (<see cref="UserSettings.uiLayout"/>),
    /// multi-touch aware, writing <see cref="VirtualKartInput"/> for the configured player. Replays keep only pause and camera.
    /// </summary>
    public class TouchControls : RaceLayer
    {
        sealed class Control
        {
            public TouchControlElement element;
            public readonly HashSet<int> pointers = new();
            // Joystick: deflection per axis after the dead zone (-1..1, y up). Wheel: turn and the finger's last angle.
            public Vector2 stick;
            public float wheelDegrees, fingerDegrees;
            public bool fingerAngleKnown;
            public bool Pressed => pointers.Count > 0;
            public TouchControlType Type => element.Setup.type;
            public bool Analog => Type is TouchControlType.Joystick or TouchControlType.Wheel;
        }

        [SerializeField, Tooltip("Local player whose virtual kart input the controls drive.")]
        int playerIndex;
        [SerializeField, Tooltip("Steering wheel turn either way (degrees) that gives full steering lock.")]
        float wheelLockDegrees = 90f;
        [SerializeField, Tooltip("Joystick knob travel from the centre, as a share of the joystick's size.")]
        float stickTravel = 0.3f;
        [SerializeField, Tooltip("Wheel: finger distance from the centre (share of the size) below which the wheel ignores the finger's angle.")]
        float wheelHubShare = 0.12f;

        readonly List<Control> controls = new();

        protected override bool InsetBySafeArea => true;

        protected override void Bind()
        {
            controls.Clear();
            var root = Q<VisualElement>("touch");
            root.Clear();
            var replay = Race.Session != null && Race.Session.IsReplay;
            var layout = Settings.uiLayout.Controls;
            for (var i = 0; i < layout.Count; i++)
            {
                if (layout[i] == null || (replay && !InReplay(layout[i].type))) continue;
                var c = new Control { element = new TouchControlElement(layout[i]) };
                root.Add(c.element);
                Wire(c);
                controls.Add(c);
            }
            VirtualKartInput.Clear(playerIndex);
        }

        protected override void Unbind() => Release();

        static bool InReplay(TouchControlType type) => type is TouchControlType.Pause or TouchControlType.Camera;

        void Wire(Control c)
        {
            var e = c.element;
            e.RegisterCallback<PointerDownEvent>(ev => OnDown(c, ev.pointerId, ev.localPosition));
            e.RegisterCallback<PointerMoveEvent>(ev =>
            {
                if (!c.Analog || !c.pointers.Contains(ev.pointerId)) return;
                Track(c, ev.localPosition);
                Apply();
            });
            e.RegisterCallback<PointerUpEvent>(ev => OnRelease(c, ev.pointerId));
            e.RegisterCallback<PointerCancelEvent>(ev => OnRelease(c, ev.pointerId));
            e.RegisterCallback<PointerCaptureOutEvent>(ev => OnRelease(c, ev.pointerId));
        }

        void OnDown(Control c, int pointerId, Vector2 local)
        {
            // A joystick or wheel follows one finger.
            if (c.Analog && c.Pressed) return;
            c.pointers.Add(pointerId);
            c.element.CapturePointer(pointerId);
            c.element.AddToClassList(TouchControlElement.PressedClass);
            if (c.Type == TouchControlType.Pause) Race.SetPaused(!Race.Paused);
            else if (c.Type == TouchControlType.Camera) VirtualKartInput.PressCamera(playerIndex);
            if (c.Analog)
            {
                c.fingerAngleKnown = false;
                Track(c, local);
            }
            Apply();
        }

        void OnRelease(Control c, int pointerId)
        {
            if (!c.pointers.Remove(pointerId)) return;
            if (c.element.HasPointerCapture(pointerId)) c.element.ReleasePointer(pointerId);
            if (!c.Pressed) Reset(c);
            Apply();
        }

        static void Reset(Control c)
        {
            c.pointers.Clear();
            c.element.RemoveFromClassList(TouchControlElement.PressedClass);
            c.stick = Vector2.zero;
            c.wheelDegrees = 0f;
            c.element.ShowStick(Vector2.zero);
            c.element.ShowWheel(0f);
        }

        void Track(Control c, Vector2 local)
        {
            var size = c.element.layout.width;
            if (float.IsNaN(size) || size <= 0f) size = c.element.Size;
            var fromCentre = local - new Vector2(size * 0.5f, size * 0.5f);
            if (c.Type == TouchControlType.Joystick) TrackStick(c, fromCentre, size * stickTravel);
            else TrackWheel(c, fromCentre, size * wheelHubShare);
        }

        void TrackStick(Control c, Vector2 fromCentre, float travel)
        {
            var knob = Vector2.ClampMagnitude(fromCentre, travel);
            c.element.ShowStick(knob);
            var deadZone = c.element.Setup.deadZone;
            c.stick = new Vector2(DeadZone(knob.x / travel, deadZone), DeadZone(-knob.y / travel, deadZone));
        }

        // Panel y points down, so the angle grows clockwise: turning the wheel clockwise steers right.
        void TrackWheel(Control c, Vector2 fromCentre, float hub)
        {
            if (fromCentre.sqrMagnitude < hub * hub) return;
            var degrees = Mathf.Atan2(fromCentre.y, fromCentre.x) * Mathf.Rad2Deg;
            if (c.fingerAngleKnown)
                c.wheelDegrees = Mathf.Clamp(c.wheelDegrees + Mathf.DeltaAngle(c.fingerDegrees, degrees), -wheelLockDegrees, wheelLockDegrees);
            c.fingerDegrees = degrees;
            c.fingerAngleKnown = true;
            c.element.ShowWheel(c.wheelDegrees);
        }

        static float DeadZone(float value, float deadZone)
        {
            var size = Mathf.Abs(value);
            if (size <= deadZone) return 0f;
            return Mathf.Sign(value) * Mathf.Min(1f, (size - deadZone) / Mathf.Max(0.01f, 1f - deadZone));
        }

        void Apply()
        {
            ref var v = ref VirtualKartInput.ForPlayer(playerIndex);
            v = default;
            var analog = false;
            var wish = 0f;
            for (var i = 0; i < controls.Count; i++)
            {
                var c = controls[i];
                if (!c.Pressed) continue;
                switch (c.Type)
                {
                    case TouchControlType.SteerLeft: v.steer -= 1f; break;
                    case TouchControlType.SteerRight: v.steer += 1f; break;
                    case TouchControlType.Throttle: v.throttle = 1f; break;
                    case TouchControlType.Brake: v.brake = 1f; break;
                    case TouchControlType.Item: v.fire = true; break;
                    case TouchControlType.LookBack: v.lookBack = true; break;
                    case TouchControlType.Horn: v.horn = true; break;
                    case TouchControlType.Respawn: v.respawn = true; break;
                    case TouchControlType.Joystick:
                        analog = true;
                        wish += c.stick.x;
                        if (c.stick.y > 0f) v.throttle = 1f;
                        else if (c.stick.y < 0f) v.brake = 1f;
                        break;
                    case TouchControlType.Wheel:
                        analog = true;
                        wish += DeadZone(c.wheelDegrees / wheelLockDegrees, c.element.Setup.deadZone);
                        break;
                }
            }
            v.steer = Mathf.Clamp(v.steer, -1f, 1f);
            VirtualKartInput.SetSteerWish(playerIndex, analog ? wish : null);
        }

        void Release()
        {
            for (var i = 0; i < controls.Count; i++) Reset(controls[i]);
            VirtualKartInput.Clear(playerIndex);
        }
    }
}
