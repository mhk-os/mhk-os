using UnityEngine;

namespace MHK.Effects
{
    /// <summary>Constant rotation (item boxes, fake boxes, flashing lights).</summary>
    public class Spinner : MonoBehaviour
    {
        [Tooltip("Rotation speed per axis (degrees per second).")]
        [SerializeField] Vector3 degreesPerSecond = new(0f, 45f, 0f);
        [Tooltip("Rotate about the object's own axes or the world axes.")]
        [SerializeField] Space space = Space.Self;

        void Update() => transform.Rotate(degreesPerSecond * Time.deltaTime, space);
    }
}
