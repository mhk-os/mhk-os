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
    /// <see cref="UserSettings.keyBindings"/>) and merges the on-screen touch controls. Analog touch steering
    /// (joystick, wheel) is turned into steering key presses that hold the wished share of full lock.
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

        /// <summary>The Camera key or the touch camera button was pressed (consumes the touch press).</summary>
        public bool CameraPressed() => VirtualKartInput.TakeCameraPress(playerIndex) | Camera.WasPressedThisFrame();

        public void UpdateInput(Kart kart, ref KartInput input)
        {
            var v = VirtualKartInput.ForPlayer(playerIndex);
            var touchSteer = v.steer;
            if (VirtualKartInput.TryGetSteerWish(playerIndex, out var wish)) touchSteer += AnalogSteerKey(kart, wish);
            input.steer = Mathf.Clamp(steer.ReadValue<float>() + touchSteer, -1f, 1f);
            input.throttle = Mathf.Clamp01(throttle.ReadValue<float>() + v.throttle);
            input.brake = Mathf.Clamp01(brake.ReadValue<float>() + v.brake);
            input.fire = fire.IsPressed() || v.fire;
            input.lookBack = lookBack.IsPressed() || v.lookBack;
            input.horn = horn.IsPressed() || v.horn;
            input.respawn = respawn.IsPressed() || v.respawn;
        }

        // MHK2 steers with keys, so analog touch steering picks each tick the key (right, left or none) that leaves the
        // commanded wheel closest to the wish. Compared in input space: inverted steering mirrors the wheel.
        static float AnalogSteerKey(Kart kart, float wish)
        {
            var physics = kart.Physics;
            var p = physics.Profile;
            if (p == null || p.maxSteeringAngle <= 0f) return 0f;
            var wheel = kart.Effects.SteeringInverted ? -physics.SteerCommand01 : physics.SteerCommand01;
            var dt = Time.fixedDeltaTime;
            var press = dt * p.steeringSpeed / p.maxSteeringAngle;
            var release = Mathf.Abs(Mathf.MoveTowards(wheel, 0f, dt * p.steeringRestSpeed / p.maxSteeringAngle) - wish);
            var right = Mathf.Abs(Mathf.Min(1f, wheel + press) - wish);
            var left = Mathf.Abs(Mathf.Max(-1f, wheel - press) - wish);
            if (release <= right && release <= left) return 0f;
            return right < left ? 1f : -1f;
        }
    }
}
