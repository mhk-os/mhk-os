using UnityEngine;

namespace MHK.Karts
{
    /// <summary>Marks the animated parts of a kart (or character-with-kart) model prefab. All references are optional.</summary>
    public class KartModel : MonoBehaviour
    {
        [Tooltip("Front wheels, rotated for steering and spin.")]
        public Transform[] frontWheels = System.Array.Empty<Transform>();
        [Tooltip("Rear wheels, spin only.")]
        public Transform[] rearWheels = System.Array.Empty<Transform>();
        [Tooltip("Optional separate steering pivots (e.g. steering wheel).")]
        public Transform[] steeringPivots = System.Array.Empty<Transform>();
        [Tooltip("Steering pivot turn per degree of front wheel angle.")]
        public float steeringPivotScale = 1f;
        [Tooltip("Wheel radius (m) the wheel spin is computed from.")]
        public float wheelRadius = 0.2f;
        [Tooltip("Axis the wheels spin around, in wheel-local space.")]
        public Vector3 wheelSpinAxis = Vector3.right;
        [Tooltip("Where a separately modelled driver sits.")]
        public Transform seat;
        [Tooltip("Centre of the steering wheel, where drivers hold it. Drivers slide along the seat so their hands reach " +
                 "it (CharacterDefinition.handReach). Empty = the first steering pivot.")]
        public Transform steeringWheel;
        [Tooltip("Optional engine part, pivot at its mount. It shakes while the kart runs and is drawn at the race's engine " +
                 "class size (DifficultySettings.engineModelScale): full size is 150cc, smaller for 100cc and 50cc.")]
        public Transform engine;

        /// <summary>Where drivers hold the steering wheel, null when the model marks none.</summary>
        public Transform SteeringWheel => steeringWheel != null ? steeringWheel : steeringPivots.Length > 0 ? steeringPivots[0] : null;
    }
}
