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
    }
}
