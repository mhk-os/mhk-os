using System;
using MHK.Core;
using MHK.Karts;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace MHK.Input
{
    /// <summary>
    /// Human driver: reads the Kart map of a per-player copy of the controls (with the saved rebinds,
    /// <see cref="UserSettings.keyBindings"/>) and merges the on-screen touch controls.
    /// </summary>
    public sealed class PlayerKartDriver : IKartDriver, IDisposable
    {
        readonly InputActionAsset actions;
        readonly InputAction steer, throttle, brake, fire, lookBack, horn, respawn;
        readonly int playerIndex;

        public InputAction Pause { get; }
        /// <summary>Cycles the race camera view (<see cref="Cameras.ChaseCamera.CycleView"/>).</summary>
        public InputAction Camera { get; }

        /// <summary>Copies <paramref name="template"/> (the project-wide MHKControls) for player <paramref name="playerIndex"/> and enables its Kart map.</summary>
        public PlayerKartDriver(InputActionAsset template, int playerIndex)
        {
            this.playerIndex = playerIndex;
            actions = Object.Instantiate(template);
            // Binding overrides are not serialized, so the copy gets the rebinds itself.
            actions.LoadBindingOverridesFromJson(GameRoot.Instance.Settings.keyBindings);
            var map = actions.FindActionMap(ProjectActions.KartMap, true);
            steer = map.FindAction("Steer", true);
            throttle = map.FindAction("Throttle", true);
            brake = map.FindAction("Brake", true);
            fire = map.FindAction("Fire", true);
            lookBack = map.FindAction("LookBack", true);
            horn = map.FindAction("Horn", true);
            respawn = map.FindAction("Respawn", true);
            Pause = map.FindAction("Pause", true);
            Camera = map.FindAction("Camera", true);
            map.Enable();
        }

        public void Dispose()
        {
            actions.Disable();
            Object.Destroy(actions);
        }

        public void UpdateInput(Kart kart, ref KartInput input)
        {
            var v = VirtualKartInput.ForPlayer(playerIndex);
            input.steer = Mathf.Clamp(steer.ReadValue<float>() + v.steer, -1f, 1f);
            input.throttle = Mathf.Clamp01(throttle.ReadValue<float>() + v.throttle);
            input.brake = Mathf.Clamp01(brake.ReadValue<float>() + v.brake);
            input.fire = fire.IsPressed() || v.fire;
            input.lookBack = lookBack.IsPressed() || v.lookBack;
            input.horn = horn.IsPressed() || v.horn;
            input.respawn = respawn.IsPressed() || v.respawn;
        }
    }
}
