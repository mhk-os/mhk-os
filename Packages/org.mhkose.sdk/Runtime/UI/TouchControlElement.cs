using MHK.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// One on-screen touch control, placed and sized from its <see cref="TouchControlSetup"/>: a disc with an icon for a
    /// button, a ring with a knob for the joystick, a turning wheel. Shared by the race touch layer and the Touch Layout screen.
    /// </summary>
    public class TouchControlElement : VisualElement
    {
        public const string PressedClass = "pressed";
        const float IconShare = 0.48f, WheelIconShare = 0.86f, KnobShare = 0.4f;

        readonly VisualElement icon, knob;

        public TouchControlSetup Setup { get; }

        /// <summary>Side length in UI reference pixels.</summary>
        public float Size => BaseSize(Setup.type) * Setup.scale;

        public TouchControlElement(TouchControlSetup setup)
        {
            Setup = setup;
            usageHints = UsageHints.DynamicTransform;
            AddToClassList("touch-button");
            AddToClassList("touch-" + setup.type.ToString().ToLowerInvariant());
            if (setup.type is TouchControlType.Throttle or TouchControlType.Item) AddToClassList("accent");
            if (setup.type == TouchControlType.Joystick)
            {
                knob = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                knob.AddToClassList("touch-knob");
                Add(knob);
            }
            else
            {
                icon = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                icon.AddToClassList("icon");
                icon.AddToClassList("touch-icon");
                icon.AddToClassList(IconClass(setup.type));
                if (setup.type == TouchControlType.Throttle) icon.AddToClassList("up");
                if (setup.type == TouchControlType.Brake) icon.AddToClassList("down");
                Add(icon);
            }
            Apply();
        }

        /// <summary>Default side length of a control of <paramref name="type"/> in UI reference pixels.</summary>
        public static float BaseSize(TouchControlType type) => type switch
        {
            TouchControlType.Throttle or TouchControlType.Item or TouchControlType.SteerLeft or TouchControlType.SteerRight => 150f,
            TouchControlType.Brake or TouchControlType.LookBack or TouchControlType.Horn => 110f,
            TouchControlType.Joystick or TouchControlType.Wheel => 280f,
            _ => 88f,
        };

        /// <summary>Icon class (MHKTheme.uss) of a control of <paramref name="type"/>.</summary>
        public static string IconClass(TouchControlType type) => type switch
        {
            TouchControlType.Throttle or TouchControlType.Brake => "i-arrow_forward",
            TouchControlType.SteerLeft => "i-chevron_left",
            TouchControlType.SteerRight => "i-chevron_right",
            TouchControlType.Item => "i-bolt",
            TouchControlType.LookBack => "i-replay",
            TouchControlType.Camera => "i-videocam",
            TouchControlType.Respawn => "i-location_on",
            TouchControlType.Pause => "i-pause",
            TouchControlType.Horn => "i-campaign",
            TouchControlType.Joystick => "i-joystick",
            _ => "i-search_hands_free",
        };

        /// <summary>
        /// Places, sizes and fades the element from <see cref="Setup"/> (after a change). <paramref name="minOpacity"/>
        /// keeps invisible controls findable in the editor.
        /// </summary>
        public void Apply(float minOpacity = 0f)
        {
            var s = Setup;
            var size = Size;
            var half = size * 0.5f;
            style.width = size;
            style.height = size;
            style.borderTopLeftRadius = style.borderTopRightRadius = style.borderBottomLeftRadius = style.borderBottomRightRadius = half;
            var left = s.anchor.x < 0.5f;
            var top = s.anchor.y < 0.5f;
            style.left = Edge(left, s.offset.x - half);
            style.right = Edge(!left, -s.offset.x - half);
            style.top = Edge(top, s.offset.y - half);
            style.bottom = Edge(!top, -s.offset.y - half);
            style.opacity = Mathf.Max(Mathf.Clamp01(s.opacity), minOpacity);
            SizeChild(icon, size * (s.type == TouchControlType.Wheel ? WheelIconShare : IconShare));
            SizeChild(knob, size * KnobShare);
        }

        static StyleLength Edge(bool used, float distance) => used ? new StyleLength(distance) : new StyleLength(StyleKeyword.Auto);

        /// <summary>Moves the joystick knob by <paramref name="offset"/> (local pixels) from the centre.</summary>
        public void ShowStick(Vector2 offset)
        {
            if (knob != null) knob.style.translate = new Translate(offset.x, offset.y);
        }

        /// <summary>Turns the wheel to <paramref name="degrees"/> (clockwise positive). Other controls ignore it.</summary>
        public void ShowWheel(float degrees)
        {
            if (Setup.type == TouchControlType.Wheel) icon.style.rotate = new Rotate(degrees);
        }

        static void SizeChild(VisualElement child, float size)
        {
            if (child == null) return;
            child.style.width = size;
            child.style.height = size;
            child.style.borderTopLeftRadius = child.style.borderTopRightRadius =
                child.style.borderBottomLeftRadius = child.style.borderBottomRightRadius = size * 0.5f;
        }
    }
}
