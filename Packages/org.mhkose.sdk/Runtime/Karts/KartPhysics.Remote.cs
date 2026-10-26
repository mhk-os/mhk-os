using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        /// <summary>
        /// One tick of a remote racer's kinematic stand-in (<see cref="Kart.IsRemote"/>): moves the body to the
        /// interpolated pose and sets the state the visuals, sounds, the camera and item targeting read, derived from the
        /// received state instead of simulated. <paramref name="intangible"/> is the sender's pass-through state, so local
        /// karts drive through it exactly while it is a ghost there.
        /// </summary>
        internal void StepRemote(Vector3 position, Quaternion rotation, Vector3 velocity, float steer01, bool throttle, bool brake, bool airborne,
            bool intangible, bool snap, float h)
        {
            physicsScene = gameObject.scene.GetPhysicsScene();
            UpdateIntangible(intangible, h);
            UpdateRunOver(effects != null && effects.RunOverActive, h);
            if (snap)
            {
                Body.position = position;
                Body.rotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
            }
            else
            {
                Body.MovePosition(position);
                Body.MoveRotation(rotation);
            }
            var forward = rotation * Vector3.forward;
            ForwardSpeed = Vector3.Dot(velocity, forward);
            SteerAngle = profile != null ? steer01 * profile.maxSteeringAngle : 0f;
            steerCommandDeg = -SteerAngle;
            JustLanded = !wasGrounded && !airborne && AirTime > 0f;
            IsGrounded = !airborne;
            wasGrounded = IsGrounded;
            AirTime = airborne ? AirTime + h : 0f;
            ContactCount = airborne ? 0 : WheelCount;
            Throttle = throttle ? 1f : 0f;
            BrakeAmount = brake ? 1f : 0f;
            Reversing = ForwardSpeed < -0.1f;
            // Engine sound: one gear's worth of rpm over the speed range.
            var top = profile != null ? RedlineSpeed(1f) : FallbackMaxSpeed;
            Rpm = profile != null ? Mathf.Clamp01(Mathf.Abs(ForwardSpeed) / Mathf.Max(1f, top)) * profile.changeUpRpm : 0f;
            // Tyre marks and dust: wheels on the ground under their rest positions, slipping by the sideways speed.
            var up = rotation * Vector3.up;
            var lateral = Mathf.Abs(Vector3.Dot(velocity, rotation * Vector3.right));
            LateralSlip = airborne ? 0f : lateral * 2f;
            for (var i = 0; i < wheels.Length; i++)
            {
                wheels[i].onGround = !airborne;
                wheels[i].normal = up;
                wheels[i].point = position + rotation * wheels[i].local - up * wheels[i].radius;
                wheels[i].lateralSlipSpeed = airborne ? 0f : lateral;
            }
        }
    }
}
