using MHK.Core;
using MHK.Race;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// MHK2 rigid-body kart (RwpVehicle 0x42c270): four ray-probed wheel contacts solved by a sequential-impulse solver on
    /// top of a PhysX chassis. Remake deviations are documented on the fields and in work/mhk2/physics.md.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public partial class KartPhysics : MonoBehaviour
    {
        const float MaxAngularVelocity = 30f;
        const int WheelCount = 4;

        public KartPhysicsProfile Profile
        {
            get => profile;
            set { profile = value; ApplyProfile(); }
        }

        [SerializeField] KartPhysicsProfile profile;
        [Tooltip("Layers the wheels can drive on.")]
        [SerializeField] LayerMask groundMask = ~0;
        [Tooltip("Physics material of the chassis box (MHK2: bounce 0.1, slide 0.5, combine minimum).")]
        [SerializeField] PhysicsMaterial chassisMaterial;
        [Tooltip("Velocity-solver iterations for the wheel contacts.")]
        [SerializeField] int solverIterations = 8;

        [Header("Handling (deliberate deviations from the decoded MHK2 vehicle code)")]
        [Tooltip("Wheels within this distance of the ground get a speculative contact (no push, only stops the approach). " +
                 "Prevents contact chatter of the very stiff MHK2 wheel springs at 60 Hz, which dropped grip on every bump.")]
        [SerializeField] float contactMargin = 0.05f;
        [Tooltip("Height (0 = ground contact, 1 = centre of mass) at which lateral tyre friction acts. The decoded model " +
                 "applies it at the ground, so the grip (μ 2.0 to 2.2) exceeds the static roll-over limit (1.53) and karts " +
                 "two-wheel in every fast corner. Raising the force point removes the roll moment (like WheelCollider.forceAppPointDistance).")]
        [Range(0f, 1f)] [SerializeField] float lateralForceHeight = 1f;
        [Tooltip("Static faces steeper than this (|normal.y| below it, 0.6 ≈ 53°) are walls for the chassis: it slides along " +
                 "them without friction, as along MHK2's invisible walls, so a shallow hit turns the kart parallel instead of " +
                 "dragging it round. Head-on hits still stop it. 0 = off.")]
        [Range(0f, 1f)] [SerializeField] float wallMaxNormalY = 0.6f;

        [Header("Ghost (intangible effects) / run over (flattened)")]
        [Tooltip("Layers a ghost kart's chassis passes through entirely (other karts, dropped items).")]
        [SerializeField] LayerMask ghostExcludeLayers = GameLayers.KartAndItemMask;
        [Tooltip("Layers the chassis of a kart that can be run over (StatusEffectDefinition.runOver, flattened) passes " +
                 "through: other karts.")]
        [SerializeField] LayerMask runOverExcludeLayers = GameLayers.KartMask;
        [Tooltip("After a ghost or run-over effect ends the kart stays passable while it still overlaps something it let " +
                 "through, at most this long (s), so it never ends up stuck inside another kart or prop.")]
        [SerializeField] float ghostExitTimeout = 3f;

        public LayerMask GroundMask => groundMask;
        public PhysicsMaterial ChassisMaterial => chassisMaterial;
        public int SolverIterations => solverIterations;
        public float ContactMargin => contactMargin;
        public float LateralForceHeight => lateralForceHeight;
        public float WallMaxNormalY => wallMaxNormalY;
        public LayerMask GhostExcludeLayers => ghostExcludeLayers;
        public LayerMask RunOverExcludeLayers => runOverExcludeLayers;
        public float GhostExitTimeout => ghostExitTimeout;

        public Rigidbody Body { get; private set; }
        /// <summary>The chassis box (its footprint is what touches hazards that hit by model footprint).</summary>
        public BoxCollider Chassis => chassis;
        public bool IsGrounded { get; private set; }
        public bool JustLanded { get; private set; }
        public float ForwardSpeed { get; private set; }
        /// <summary>Visual front-wheel angle in Unity convention (positive = right).</summary>
        public float SteerAngle { get; private set; }
        /// <summary>Commanded steering before wash-out, −1 (full left) … +1 (full right).</summary>
        public float SteerCommand01 => profile != null && profile.maxSteeringAngle > 0f ? Mathf.Clamp(-steerCommandDeg / profile.maxSteeringAngle, -1f, 1f) : 0f;
        public float Rpm { get; private set; }
        public int Gear { get; private set; }
        public bool Reversing { get; private set; }
        public float Throttle { get; private set; }
        public float BrakeAmount { get; private set; }
        public float AirTime { get; private set; }
        public SurfaceKind Surface { get; private set; }
        public float LateralSlip { get; private set; }
        /// <summary>Wheels touching the ground in the last step.</summary>
        public int ContactCount { get; private set; }
        public float SpeedKmh => ForwardSpeed * Units.KmhPerMs;

        /// <summary>Engine class factor (MHK2 50/100/150cc = 0.9/1.1/1.3): diff ratio ÷ f, torque × f.</summary>
        public float ClassFactor { get; set; } = 1.1f;
        /// <summary>
        /// Velocity scale of this kart (default <see cref="GlobalSpeedScale"/>): top speed and accelerations × s, so the
        /// speed/time curve is the original one × s. Speed in original units = v ÷ s.
        /// </summary>
        public float SpeedScale { get; set; } = 1f;
        /// <summary>A world speed (m/s) in original MHK2 units (÷ <see cref="SpeedScale"/>), for MHK2 speed thresholds.</summary>
        public float ToOriginalSpeed(float worldSpeed) => worldSpeed / Mathf.Max(MinSpeedScale, SpeedScale);
        /// <summary>Forward speed in original MHK2 units (m/s ÷ <see cref="SpeedScale"/>), for MHK2 speed thresholds.</summary>
        public float ForwardSpeedOriginal => ToOriginalSpeed(ForwardSpeed);
        /// <summary>Largest rear-wheel lateral friction usage of the last step (0 = no side force, 1 = sliding).</summary>
        public float RearGripUsage { get; private set; }
        /// <summary>MHK2 skid energy: summed lateral slip speed of the touching wheels in original (unscaled) m/s.</summary>
        public float SkidEnergy => ToOriginalSpeed(LateralSlip);
        /// <summary>
        /// While true the body is moved externally (e.g. the molehill dig): <see cref="Step"/> applies no input, forces or
        /// wheel queries, it clears the ground contact state and only updates <see cref="ForwardSpeed"/>.
        /// </summary>
        public bool Suspended { get; set; }

        readonly Wheel[] wheels = new Wheel[WheelCount];
        KartEffects effects;
        Vector3 inertiaLocal = Vector3.one;
        float steerCommandDeg;
        float prevSteer;
        bool wasGrounded;
        bool boostOverspeed;
        BoxCollider chassis;
        // The scene the kart's queries run in: the default one in races, a local physics scene when the ghost validation
        // re-simulates a run (MHK.Validation). Taken at every step, as the kart can be moved between scenes.
        PhysicsScene physicsScene;

        void Awake()
        {
            Body = GetComponent<Rigidbody>();
            effects = GetComponent<KartEffects>();
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            Body.useGravity = false;
            Body.linearDamping = 0f;
            Body.angularDamping = 0f;
            Body.maxAngularVelocity = MaxAngularVelocity;
            SpeedScale = GlobalSpeedScale;
            if (profile != null) ApplyProfile();
        }

        static Vector3 FromOriginal(Vector3 v) => new(v.z, v.y, v.x);

        /// <summary>Chassis box of <paramref name="p"/> in kart space: centre and half extents (what <see cref="Chassis"/> gets).</summary>
        public static void ChassisBox(KartPhysicsProfile p, out Vector3 centre, out Vector3 halfExtents)
        {
            centre = FromOriginal(p.chassisCollOffset);
            halfExtents = FromOriginal(p.chassisSize);
        }

        void ApplyProfile()
        {
            if (profile == null || Body == null) return;
            var p = profile;
            Body.mass = p.mass;
            var half = FromOriginal(p.chassisSize);
            SetUpChassis(p, half);
            RegisterWallSlide();
            BuildWheels(p);
            ComputeInertia(p, half, chassis.center);
            Body.centerOfMass = FromOriginal(p.centerOfMass);
        }

        void SetUpChassis(KartPhysicsProfile p, Vector3 half)
        {
            chassis = GetComponent<BoxCollider>();
            if (chassis == null) chassis = gameObject.AddComponent<BoxCollider>();
            chassis.size = half * 2f;
            chassis.center = FromOriginal(p.chassisCollOffset);
            if (chassisMaterial != null) chassis.sharedMaterial = chassisMaterial;
        }

        void BuildWheels(KartPhysicsProfile p)
        {
            for (var i = 0; i < WheelCount; i++)
            {
                var front = i < 2;
                var offs = p.wheelCollOffsets != null && p.wheelCollOffsets.Length > i
                    ? p.wheelCollOffsets[i]
                    : KartPhysicsProfile.DefaultWheelOffset(i);
                wheels[i] = new Wheel
                {
                    local = FromOriginal(offs),
                    radius = front ? p.wheelsRadiusFront : p.wheelsRadiusRear,
                    front = front,
                };
            }
        }

        // Volume-weighted inertia of box + 4 wheel spheres about their volume centroid (0x4d5a40).
        void ComputeInertia(KartPhysicsProfile p, Vector3 half, Vector3 boxCentre)
        {
            var vols = new float[WheelCount + 1];
            var centres = new Vector3[WheelCount + 1];
            vols[0] = 8f * half.x * half.y * half.z;
            centres[0] = boxCentre;
            for (var i = 0; i < WheelCount; i++)
            {
                vols[i + 1] = 4f / 3f * Mathf.PI * Mathf.Pow(wheels[i].radius, 3);
                centres[i + 1] = wheels[i].local;
            }
            var total = 0f;
            var centroid = Vector3.zero;
            for (var i = 0; i < vols.Length; i++) { total += vols[i]; centroid += centres[i] * vols[i]; }
            centroid /= total;
            var inertia = Vector3.zero;
            for (var i = 0; i < vols.Length; i++)
            {
                var d = centres[i] - centroid;
                var parallel = new Vector3(d.y * d.y + d.z * d.z, d.x * d.x + d.z * d.z, d.x * d.x + d.y * d.y);
                inertia += (OwnInertia(i, half) + parallel) * (vols[i] / total);
            }
            inertiaLocal = inertia * p.mass;
            Body.inertiaTensor = inertiaLocal;
            Body.inertiaTensorRotation = Quaternion.identity;
        }

        Vector3 OwnInertia(int part, Vector3 half)
        {
            if (part == 0) return new Vector3(half.y * half.y + half.z * half.z, half.x * half.x + half.z * half.z, half.x * half.x + half.y * half.y) / 3f;
            var r2 = 0.4f * wheels[part - 1].radius * wheels[part - 1].radius;
            return new Vector3(r2, r2, r2);
        }

        /// <summary>
        /// Takes the inspector settings (ground mask, chassis material, solver and handling tweaks, pass-through layers) of
        /// <paramref name="source"/>, for a kart built in code that must step exactly like the race kart prefab.
        /// </summary>
        public void CopySettings(KartPhysics source)
        {
            groundMask = source.groundMask;
            chassisMaterial = source.chassisMaterial;
            solverIterations = source.solverIterations;
            contactMargin = source.contactMargin;
            lateralForceHeight = source.lateralForceHeight;
            wallMaxNormalY = source.wallMaxNormalY;
            ghostExcludeLayers = source.ghostExcludeLayers;
            runOverExcludeLayers = source.runOverExcludeLayers;
            ghostExitTimeout = source.ghostExitTimeout;
            if (profile != null) ApplyProfile();
        }

        /// <summary>
        /// Sets the input model's state after a <see cref="Teleport"/> to a recorded one (re-simulation): steering command
        /// (as <see cref="SteerCommand01"/>) and the pedals fully on or off.
        /// </summary>
        public void PrimeInput(float steerCommand01, bool throttle, bool brake)
        {
            if (profile == null) return;
            steerCommandDeg = -Mathf.Clamp(steerCommand01, -1f, 1f) * profile.maxSteeringAngle;
            prevSteer = steerCommandDeg;
            Throttle = throttle ? 1f : 0f;
            BrakeAmount = brake ? 1f : 0f;
        }

        public void Teleport(Vector3 position, Quaternion rotation)
        {
            Body.position = position;
            Body.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            Throttle = 0;
            BrakeAmount = 0;
            steerCommandDeg = 0;
            SteerAngle = 0;
            Gear = 0;
            Reversing = false;
            RearGripUsage = 0f;
            boostOverspeed = false;
        }

        /// <summary>Height of the kart origin above flat ground when it stands on its wheels (lowest wheel bottom).</summary>
        public float RestHeight
        {
            get
            {
                var h = 0f;
                for (var i = 0; i < wheels.Length; i++) h = Mathf.Max(h, wheels[i].radius - wheels[i].local.y);
                return h;
            }
        }

        /// <summary>
        /// Teleports the kart onto the ground at <paramref name="point"/>: standing on its wheels on the surface with
        /// <paramref name="normal"/>, facing <paramref name="forward"/> along it (start grid).
        /// </summary>
        public void PlaceOnGround(Vector3 point, Vector3 normal, Vector3 forward)
        {
            var along = Vector3.ProjectOnPlane(forward, normal);
            if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(transform.forward, normal);
            // 1 cm above rest height: the wheels are in contact range at once, nothing starts inside the road.
            Teleport(point + normal * (RestHeight + 0.01f), Quaternion.LookRotation(along, normal));
        }

        /// <summary>
        /// One physics tick: input model, engine, forces, wheel contacts and the contact solver, then the post-step wheel
        /// state. Called by <see cref="Kart"/> from FixedUpdate.
        /// </summary>
        public void Step(in KartInput input, float h)
        {
            if (profile == null) return;
            physicsScene = gameObject.scene.GetPhysicsScene();
            UpdateIntangible(effects != null && effects.GhostActive, h);
            UpdateRunOver(effects != null && effects.RunOverActive, h);
            if (Suspended)
            {
                StepSuspended();
                return;
            }
            var s = BeginStep(h);
            UpdateInput(input, h);
            s.steer = UpdateSteering(s.p, s.fx);
            s.driveTorque = UpdateGearbox(ref s);
            BeginForces(ref s);
            DetectContacts(s.rot, s.up);
            GatherContacts(ref s);
            ApplySlopeGravity(ref s);
            ApplySteeringTorque(ref s);
            ApplyDrag(ref s);
            ApplyBoostOverspeed(ref s);
            ApplyDriveAndLimits(ref s);
            Integrate(ref s);
            UpdateWheelState(ref s);
        }

        void StepSuspended()
        {
            ForwardSpeed = Vector3.Dot(Body.linearVelocity, Body.rotation * Vector3.forward);
            IsGrounded = false;
            JustLanded = false;
            wasGrounded = false;
            ContactCount = 0;
            AirTime = 0f;
            for (var i = 0; i < wheels.Length; i++) wheels[i].onGround = false;
        }

        // Per-wheel ground state of the last step, for tyre effects (skid marks, dust). 0 = FL, 1 = FR, 2 = RL, 3 = RR.
        public bool WheelOnGround(int i) => wheels[i].onGround;
        public Vector3 WheelContactPoint(int i) => wheels[i].point;
        public Vector3 WheelContactNormal(int i) => wheels[i].normal;
        public TrackSurface WheelSurface(int i) => wheels[i].onGround ? wheels[i].surface : null;

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            for (var i = 0; i < WheelCount; i++)
            {
                var w = wheels[i];
                Gizmos.color = w.contact ? Color.green : Color.red;
                Gizmos.DrawWireSphere(transform.TransformPoint(w.local), w.radius);
            }
        }
#endif
    }
}
