using System.Collections.Generic;
using MHK.Input;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>On-screen touch buttons (TouchControls.uxml), multi-touch aware, writing <see cref="VirtualKartInput"/> for the configured player.</summary>
    public class TouchControls : RaceLayer
    {
        sealed class TouchButton
        {
            public VisualElement element;
            public readonly HashSet<int> pointers = new();
            public bool Pressed => pointers.Count > 0;
        }

        [SerializeField, Tooltip("Local player whose virtual kart input the buttons drive.")]
        int playerIndex;

        readonly TouchButton left = new(), right = new(), gas = new(), brake = new(), fire = new(), look = new();
        TouchButton[] all;

        protected override bool InsetBySafeArea => true;

        protected override void Awake()
        {
            base.Awake();
            all = new[] { left, right, gas, brake, fire, look };
        }

        protected override void Bind()
        {
            Wire(left, "steer-left");
            Wire(right, "steer-right");
            Wire(gas, "gas");
            Wire(brake, "brake");
            Wire(fire, "fire");
            Wire(look, "look-back");
            Q<Button>("pause").clicked += () => Race.SetPaused(!Race.Paused);
        }

        protected override void Unbind() => Release();

        void Wire(TouchButton b, string name)
        {
            b.pointers.Clear();
            var e = Q<VisualElement>(name);
            b.element = e;
            e.RemoveFromClassList("pressed");
            e.RegisterCallback<PointerDownEvent>(ev =>
            {
                b.pointers.Add(ev.pointerId);
                e.CapturePointer(ev.pointerId);
                e.AddToClassList("pressed");
                Apply();
            });
            void Release(int id)
            {
                b.pointers.Remove(id);
                if (e.HasPointerCapture(id)) e.ReleasePointer(id);
                if (!b.Pressed) e.RemoveFromClassList("pressed");
                Apply();
            }
            e.RegisterCallback<PointerUpEvent>(ev => Release(ev.pointerId));
            e.RegisterCallback<PointerCancelEvent>(ev => Release(ev.pointerId));
            e.RegisterCallback<PointerCaptureOutEvent>(ev => Release(ev.pointerId));
        }

        void Apply()
        {
            ref var v = ref VirtualKartInput.ForPlayer(playerIndex);
            v.steer = (right.Pressed ? 1f : 0f) - (left.Pressed ? 1f : 0f);
            v.throttle = gas.Pressed ? 1f : 0f;
            v.brake = brake.Pressed ? 1f : 0f;
            v.fire = fire.Pressed;
            v.lookBack = look.Pressed;
        }

        void Release()
        {
            foreach (var b in all)
            {
                b.pointers.Clear();
                b.element?.RemoveFromClassList("pressed");
            }
            VirtualKartInput.ForPlayer(playerIndex) = default;
        }
    }
}
