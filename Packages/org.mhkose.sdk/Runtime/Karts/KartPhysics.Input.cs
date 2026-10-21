using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        /// <summary>Steering input beyond ±this counts as a pressed steering key.</summary>
        public const float SteerDeadzone = 0.01f;
        const float ReverseEngageSpeed = 0.1f;

        // MHK2's key input model, the same for players and computer drivers (AIKeys): steering, throttle and brake are
        // keys. A held steering key turns the wheel towards full lock at steeringSpeed, a released one returns at
        // steeringRestSpeed, throttle and brake ramp at accelerationSpeed / brakeSpeed.
        void UpdateInput(in KartInput input, float dt)
        {
            var p = profile;
            if (input.parked)
            {
                steerCommandDeg = Mathf.MoveTowards(steerCommandDeg, 0f, dt * p.steeringRestSpeed);
                Throttle = 0f;
                BrakeAmount = 1f;
                return;
            }
            UpdateSteerCommand(p, input.steer, dt);
            UpdatePedals(p, input.throttle, input.brake, ForwardSpeed, dt);
        }

        void UpdateSteerCommand(KartPhysicsProfile p, float steerIn, float dt)
        {
            if (steerIn < -SteerDeadzone) steerCommandDeg = Mathf.Min(p.maxSteeringAngle, steerCommandDeg + dt * p.steeringSpeed);
            else if (steerIn > SteerDeadzone) steerCommandDeg = Mathf.Max(-p.maxSteeringAngle, steerCommandDeg - dt * p.steeringSpeed);
            else steerCommandDeg = Mathf.MoveTowards(steerCommandDeg, 0f, dt * p.steeringRestSpeed);
        }

        void UpdatePedals(KartPhysicsProfile p, float accel, float brake, float vFwd, float dt)
        {
            var throttle = Throttle;
            var brakeOut = BrakeAmount;
            if (accel <= 0f && brake <= 0f)
            {
                throttle = Mathf.Max(0f, throttle - dt * p.accelerationSpeed);
                brakeOut = Mathf.Max(0f, brakeOut - dt * p.brakeSpeed);
            }
            else if (accel <= 0f) PressBrakeKey(p, vFwd, dt, ref throttle, ref brakeOut);
            else PressThrottleKey(p, vFwd, dt, ref throttle, ref brakeOut);
            Throttle = Mathf.Clamp01(throttle);
            BrakeAmount = Mathf.Clamp01(brakeOut);
        }

        void PressBrakeKey(KartPhysicsProfile p, float vFwd, float dt, ref float throttle, ref float brakeOut)
        {
            if (Reversing)
            {
                throttle = Mathf.Min(1f, throttle + dt * p.accelerationSpeed);
                brakeOut = 0f;
                return;
            }
            brakeOut = Mathf.Min(1f, brakeOut + dt * p.brakeSpeed);
            throttle = 0f;
            // MHK2 requires 0 ≤ v < 0.1: a kart pushed back by a wall (v slightly < 0) never engaged reverse.
            if (vFwd < ReverseEngageSpeed) { Reversing = true; Gear = 0; }
        }

        void PressThrottleKey(KartPhysicsProfile p, float vFwd, float dt, ref float throttle, ref float brakeOut)
        {
            if (!Reversing)
            {
                throttle = Mathf.Min(1f, throttle + dt * p.accelerationSpeed);
                brakeOut = 0f;
                return;
            }
            brakeOut = Mathf.Min(1f, brakeOut + dt * p.brakeSpeed);
            throttle = 0f;
            if (vFwd > -ReverseEngageSpeed) { Reversing = false; Gear = 0; }
        }
    }
}
