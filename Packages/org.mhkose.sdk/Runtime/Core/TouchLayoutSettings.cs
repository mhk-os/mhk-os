using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>What an on-screen touch control does: a button for one kart action, a virtual joystick or a virtual steering wheel.</summary>
    public enum TouchControlType { Throttle, Brake, SteerLeft, SteerRight, Item, LookBack, Camera, Respawn, Pause, Horn, Joystick, Wheel }

    /// <summary>One on-screen touch control: type, place (kept at a distance from a screen corner), size and opacity.</summary>
    [Serializable]
    public class TouchControlSetup
    {
        public TouchControlType type;
        [Tooltip("Screen corner the control keeps its distance to: x 0 = left, 1 = right, y 0 = top, 1 = bottom.")]
        public Vector2 anchor;
        [Tooltip("Control centre relative to the anchor corner, in UI reference pixels (1920x1080 layout, y down).")]
        public Vector2 offset;
        [Tooltip("Size relative to the control's default size.")]
        public float scale = 1f;
        [Tooltip("0 = invisible but still active, 1 = fully visible.")]
        public float opacity = 1f;
        [Tooltip("Joystick and wheel: share of the travel around the centre that counts as no input.")]
        public float deadZone = TouchLayoutSettings.DefaultDeadZone;

        public TouchControlSetup Clone() => (TouchControlSetup)MemberwiseClone();
    }

    /// <summary>The player's touch control layout (Touch Layout screen). Until the player saves one, the default layout applies.</summary>
    [Serializable]
    public class TouchLayoutSettings
    {
        public const float MinScale = 0.5f, MaxScale = 2f;
        public const float DefaultDeadZone = 0.15f, MaxDeadZone = 0.6f;

        [Tooltip("True once the player saved a layout. False = the default layout (it follows changes of the default).")]
        public bool custom;
        [Tooltip("The saved layout, used while custom is true.")]
        public List<TouchControlSetup> controls = new();

        /// <summary>The layout in use: the saved one, else the default.</summary>
        public IReadOnlyList<TouchControlSetup> Controls => custom ? controls : DefaultControls;

        /// <summary>Stores a copy of <paramref name="layout"/> as the player's layout.</summary>
        public void Save(IReadOnlyList<TouchControlSetup> layout)
        {
            controls = Copy(layout);
            custom = true;
        }

        /// <summary>Back to the default layout.</summary>
        public void Reset()
        {
            controls = new List<TouchControlSetup>();
            custom = false;
        }

        /// <summary>Deep copy of <paramref name="layout"/>.</summary>
        public static List<TouchControlSetup> Copy(IReadOnlyList<TouchControlSetup> layout)
        {
            var copy = new List<TouchControlSetup>(layout.Count);
            for (var i = 0; i < layout.Count; i++)
                if (layout[i] != null) copy.Add(layout[i].Clone());
            return copy;
        }

        /// <summary>Default layout: steering buttons bottom left, throttle, brake, item and look back bottom right, respawn, camera and pause top right.</summary>
        public static readonly IReadOnlyList<TouchControlSetup> DefaultControls = new[]
        {
            Control(TouchControlType.SteerLeft, 0f, 1f, 105f, -115f),
            Control(TouchControlType.SteerRight, 0f, 1f, 275f, -115f),
            Control(TouchControlType.Throttle, 1f, 1f, -105f, -225f),
            Control(TouchControlType.Brake, 1f, 1f, -85f, -65f),
            Control(TouchControlType.Item, 1f, 1f, -275f, -135f),
            Control(TouchControlType.LookBack, 1f, 1f, -255f, -275f),
            Control(TouchControlType.Pause, 1f, 0f, -380f, 58f),
            Control(TouchControlType.Camera, 1f, 0f, -480f, 58f),
            Control(TouchControlType.Respawn, 1f, 0f, -580f, 58f),
        };

        static TouchControlSetup Control(TouchControlType type, float anchorX, float anchorY, float x, float y) =>
            new() { type = type, anchor = new Vector2(anchorX, anchorY), offset = new Vector2(x, y) };
    }
}
