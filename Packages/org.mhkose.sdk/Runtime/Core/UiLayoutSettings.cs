using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>What an on-screen touch control does: a button for one kart action, a virtual joystick or a virtual steering wheel.</summary>
    public enum TouchControlType { Throttle, Brake, SteerLeft, SteerRight, Item, LookBack, Camera, Respawn, Pause, Horn, Joystick, Wheel }

    /// <summary>A race HUD element the UI Layout screen places. Stored as numbers in settings.json: only append new values.</summary>
    public enum HudElementType
    {
        Position, Item, Lap, Time, BestLap, Split, Health, Speedometer, Minimap, Replay, Countdown, FinalLap, WrongWay, Messages,
    }

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
        [Tooltip("False = not drawn but still active (an invisible touch area).")]
        public bool visible = true;
        [Tooltip("Joystick and wheel: share of the travel around the centre that counts as no input.")]
        public float deadZone = UiLayoutSettings.DefaultDeadZone;

        /// <summary>Opacity it is drawn with in races.</summary>
        public float ShownOpacity => visible ? Mathf.Clamp01(opacity) : 0f;

        public TouchControlSetup Clone() => (TouchControlSetup)MemberwiseClone();
    }

    /// <summary>
    /// One race HUD element: on the screen or not, drawn or hidden, its place, scale and opacity. Until it is placed it
    /// keeps the default place of the HUD stylesheet (RaceHud.uxml).
    /// </summary>
    [Serializable]
    public class HudElementSetup
    {
        public HudElementType type;
        [Tooltip("On the race screen. False = taken off in the UI Layout screen (it waits in the add list).")]
        public bool shown = true;
        [Tooltip("Drawn in races. A hidden element keeps its place and shows faded in the UI Layout screen.")]
        public bool visible = true;
        [Tooltip("False = the default place from the HUD stylesheet. True = anchor and offset.")]
        public bool placed;
        [Tooltip("Screen point the element keeps its distance to, and the same point of the element: 0, 0.5 or 1 per axis " +
                 "(x 0 = left, y 0 = top).")]
        public Vector2 anchor;
        [Tooltip("Element anchor point relative to the screen anchor point, in UI reference pixels (y down).")]
        public Vector2 offset;
        [Tooltip("Size relative to the default size, per axis.")]
        public Vector2 scale = Vector2.one;
        [Tooltip("0 = invisible, 1 = fully visible.")]
        public float opacity = 1f;

        /// <summary>Drawn in races.</summary>
        public bool Drawn => shown && visible && opacity > 0f;

        public HudElementSetup Clone() => (HudElementSetup)MemberwiseClone();
    }

    /// <summary>
    /// The player's race screen layout (UI Layout screen): the HUD elements and the touch controls. Until the player saves
    /// one, the default layout applies (the HUD as the stylesheet places it and the default touch controls).
    /// </summary>
    [Serializable]
    public class UiLayoutSettings
    {
        public const float MinScale = 0.5f, MaxScale = 2f;
        public const float DefaultDeadZone = 0.15f, MaxDeadZone = 0.6f;
        const int CurrentVersion = 1;
        const float LegacyMinimapOff = 0.05f;

        [Tooltip("Layout of the stored data, 0 = from before the UI Layout screen (Upgrade reads the old touch layout).")]
        public int version;
        [Tooltip("True once the player saved a layout. False = the default layout (it follows changes of the default).")]
        public bool custom;
        [Tooltip("The saved HUD elements, used while custom is true. Element types missing here use their default.")]
        public List<HudElementSetup> hud = new();
        [Tooltip("The saved touch controls, used while custom is true.")]
        public List<TouchControlSetup> controls = new();

        /// <summary>Settings of before the UI Layout screen, read once by <see cref="Upgrade"/>.</summary>
        [Serializable]
        public class Legacy
        {
            [Serializable]
            public class TouchLayout
            {
                public bool custom;
                public List<TouchControlSetup> controls = new();
            }

            public TouchLayout touchLayout = new();
            public float minimapScale = 1f;
        }

        /// <summary>The touch controls in use: the saved ones, else the default.</summary>
        public IReadOnlyList<TouchControlSetup> Controls => custom ? controls : DefaultControls;

        /// <summary>Whether <see cref="Upgrade"/> still has to read the old settings.</summary>
        public bool NeedsUpgrade => version < CurrentVersion;

        /// <summary>The setup of the HUD element <paramref name="type"/> in use (never null).</summary>
        public HudElementSetup Hud(HudElementType type)
        {
            if (custom)
                for (var i = 0; i < hud.Count; i++)
                    if (hud[i] != null && hud[i].type == type) return hud[i];
            return DefaultHud[(int)type];
        }

        /// <summary>Every HUD element's setup in use, in <see cref="HudElementType"/> order (copies).</summary>
        public List<HudElementSetup> CopyHud()
        {
            var copy = new List<HudElementSetup>(HudTypes.Length);
            for (var i = 0; i < HudTypes.Length; i++) copy.Add(Hud(HudTypes[i]).Clone());
            return copy;
        }

        /// <summary>Stores copies of <paramref name="hudLayout"/> and <paramref name="touchLayout"/> as the player's layout.</summary>
        public void Save(IReadOnlyList<HudElementSetup> hudLayout, IReadOnlyList<TouchControlSetup> touchLayout)
        {
            hud = new List<HudElementSetup>(hudLayout.Count);
            for (var i = 0; i < hudLayout.Count; i++)
                if (hudLayout[i] != null) hud.Add(hudLayout[i].Clone());
            controls = Copy(touchLayout);
            custom = true;
        }

        /// <summary>Back to the default layout.</summary>
        public void Reset()
        {
            hud = new List<HudElementSetup>();
            controls = new List<TouchControlSetup>();
            custom = false;
        }

        /// <summary>
        /// Takes over the custom touch layout and the minimap size of older settings (<paramref name="old"/>, the same file
        /// read as <see cref="Legacy"/>): a minimap size other than 100 % becomes the minimap's scale, below 5 % it is hidden.
        /// </summary>
        public void Upgrade(Legacy old)
        {
            if (!NeedsUpgrade) return;
            version = CurrentVersion;
            if (custom || old == null) return;
            var minimap = old.minimapScale;
            var touch = old.touchLayout != null && old.touchLayout.custom;
            if (!touch && Mathf.Approximately(minimap, 1f)) return;
            var hudLayout = CopyHud();
            var map = hudLayout[(int)HudElementType.Minimap];
            if (minimap < LegacyMinimapOff) map.visible = false;
            else map.scale = Vector2.one * Mathf.Clamp(minimap, MinScale, MaxScale);
            Save(hudLayout, touch ? old.touchLayout.controls : DefaultControls);
        }

        /// <summary>Deep copy of <paramref name="layout"/>.</summary>
        public static List<TouchControlSetup> Copy(IReadOnlyList<TouchControlSetup> layout)
        {
            var copy = new List<TouchControlSetup>(layout.Count);
            for (var i = 0; i < layout.Count; i++)
                if (layout[i] != null) copy.Add(layout[i].Clone());
            return copy;
        }

        /// <summary>Every HUD element type in declaration order.</summary>
        public static readonly HudElementType[] HudTypes = (HudElementType[])Enum.GetValues(typeof(HudElementType));

        /// <summary>Default HUD: every element shown at its stylesheet place, indexed by <see cref="HudElementType"/>.</summary>
        public static readonly IReadOnlyList<HudElementSetup> DefaultHud = Array.ConvertAll(HudTypes, t => new HudElementSetup { type = t });

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
