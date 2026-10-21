using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// What <see cref="KartVisual"/> and <see cref="Cameras.ChaseCamera"/> read from a live <see cref="Kart"/> or a
    /// replayed <see cref="Ghosts.GhostPlayer"/>, so a ghost looks exactly like the kart it was recorded from.
    /// </summary>
    public interface IKartView
    {
        /// <summary>The kart root: world pose of the chassis, with the visual as its child.</summary>
        Transform transform { get; }
        KartVisual Visual { get; }
        /// <summary>Driven by a player (turn poses follow the steering key, the 'start' pose before GO).</summary>
        bool IsHuman { get; }
        /// <summary>False before GO (and for knocked-out karts).</summary>
        bool ControlsEnabled { get; }
        /// <summary>Finished (or knocked out): no look-back pose any more.</summary>
        bool RaceOver { get; }
        /// <summary>The driver's input of the current physics tick.</summary>
        KartInput CurrentInput { get; }
        /// <summary>Front wheel angle (degrees).</summary>
        float SteerAngle { get; }
        /// <summary>Steering command, share of full lock (-1..1).</summary>
        float SteerCommand01 { get; }
        /// <summary>Speed along the kart's forward axis (m/s).</summary>
        float ForwardSpeed { get; }
        /// <summary><see cref="ForwardSpeed"/> in original MHK2 units (thresholds of the animation logic and the camera).</summary>
        float ForwardSpeedOriginal { get; }
        /// <summary>
        /// <see cref="ForwardSpeed"/> as a share of the kart's normal (unboosted) top speed: 0 standing, about 1 at full
        /// throttle on flat road, above 1 while boosted or downhill, negative when reversing (race camera speed effects).
        /// </summary>
        float SpeedFraction { get; }
        /// <summary>Touched down after a jump in the last physics tick.</summary>
        bool JustLanded { get; }
        /// <summary>Wheels on the ground (the camera follows the slope only when enough of them are).</summary>
        int GroundContacts { get; }
        /// <summary>Visual yaw offset (degrees) of spinning effects.</summary>
        float SpinAngle { get; }
        float ScaleMultiplier { get; }
        float VerticalScale { get; }
        /// <summary>1 = opaque. Lower draws the kart and driver see-through.</summary>
        float Opacity { get; }
        bool Burned { get; }
        /// <summary>Kart and driver are not drawn (vanish effects). Effect attachments stay visible.</summary>
        bool Hidden { get; }
        /// <summary>The kart model's wheels are not drawn (x-field).</summary>
        bool WheelsHidden { get; }
        /// <summary>Strongest body bob of the active effects (<see cref="Modding.StatusEffectDefinition.bodyBob"/>), null when none bobs.</summary>
        Modding.BodyBob BodyBob { get; }
        /// <summary>Extra horizontal field of view (degrees) of the active boost effects.</summary>
        float CameraFovBoost { get; }
        /// <summary>Visual-only shake offset of the kart model (the camera rides along).</summary>
        Vector3 QuakeOffset { get; }
        /// <summary>Whether <paramref name="t"/> belongs to an effect attachment (left opaque when the kart fades).</summary>
        bool IsEffectAttachment(Transform t);
    }
}
