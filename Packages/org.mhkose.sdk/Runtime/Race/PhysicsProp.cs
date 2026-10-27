using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Names the material of a loose track prop for its mass (<see cref="PropMass"/>), instead of reading it from the
    /// object's names, or keeps the rigidbody's own mass (<see cref="PropMaterial.Authored"/>).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    public class PhysicsProp : MonoBehaviour
    {
        [Tooltip("Sets the prop's mass when the track loads: the bounding volume of its colliders × this material's " +
                 "density (GameConfig.propMass). Auto reads it from the object's, its meshes' or its materials' names, " +
                 "Authored keeps the rigidbody's mass.")]
        public PropMaterial material = PropMaterial.Auto;
    }
}
