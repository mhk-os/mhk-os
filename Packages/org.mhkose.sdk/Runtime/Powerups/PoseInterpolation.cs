using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Draws a powerup object that moves in physics steps between its last two simulated poses, like rigidbody
    /// interpolation, so it glides at any frame rate while its flight stays tick-exact. Before every physics step the
    /// simulated pose is put back (execution order -1000), so all simulation code reads and writes the real pose.
    /// Added by <see cref="PowerupBehaviour.InterpolatePose"/>.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class PoseInterpolation : MonoBehaviour
    {
        Vector3 previousPosition, simulatedPosition;
        Quaternion previousRotation = Quaternion.identity, simulatedRotation = Quaternion.identity;
        bool valid;

        /// <summary>Position of the last simulated step (the transform may show an interpolated one during a frame).</summary>
        public Vector3 SimulatedPosition => valid ? simulatedPosition : transform.position;

        /// <summary>
        /// Share (0..1) of the next physics step the frame drawn now lies in, as rigidbody interpolation uses it: visuals
        /// of step-driven state show their time as the last step's plus this share of a step.
        /// </summary>
        public static float StepShare
        {
            get
            {
                var dt = (double)Time.fixedDeltaTime;
                return dt > 0.0 ? (float)System.Math.Min(System.Math.Max((Time.timeAsDouble - Time.fixedTimeAsDouble) / dt, 0.0), 1.0) : 1f;
            }
        }

        /// <summary>Takes the current transform as the simulated pose without blending from the previous one (spawn, teleport).</summary>
        public void Snap()
        {
            transform.GetPositionAndRotation(out simulatedPosition, out simulatedRotation);
            previousPosition = simulatedPosition;
            previousRotation = simulatedRotation;
            valid = true;
        }

        /// <summary>Records the pose the step just simulated; the frames until the next step blend towards it.</summary>
        public void Capture()
        {
            if (!valid)
            {
                Snap();
                return;
            }
            previousPosition = simulatedPosition;
            previousRotation = simulatedRotation;
            transform.GetPositionAndRotation(out simulatedPosition, out simulatedRotation);
        }

        void FixedUpdate()
        {
            if (valid) transform.SetPositionAndRotation(simulatedPosition, simulatedRotation);
        }

        void Update()
        {
            if (!valid) return;
            var share = StepShare;
            transform.SetPositionAndRotation(Vector3.LerpUnclamped(previousPosition, simulatedPosition, share),
                Quaternion.SlerpUnclamped(previousRotation, simulatedRotation, share));
        }
    }
}
