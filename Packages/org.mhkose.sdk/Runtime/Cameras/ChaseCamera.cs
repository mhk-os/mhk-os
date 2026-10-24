using System;
using MHK.Core;
using MHK.Effects;
using MHK.Input;
using MHK.Karts;
using UnityEngine;

namespace MHK.Cameras
{
    /// <summary>
    /// Race camera after MHK2's DriverCameraControl: three driving views (Close, Far, POV) cycled by the Camera action,
    /// plus look-back, intro orbit and finish views.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ChaseCamera : MonoBehaviour
    {
        /// <summary>One driving view (MHK2 "Player 1 Camera 1/2/3").</summary>
        [Serializable]
        public class ViewPreset
        {
            [Tooltip("Camera position relative to the focus point, in the kart's (smoothed) heading frame: x right, y up, z forward.")]
            public Vector3 offset;
            [Tooltip("Point the camera follows, relative to the kart origin (world axes, not rotated with the kart).")]
            public Vector3 focusPoint;
            [Tooltip("Downward tilt of the view in degrees (0 = level, like MHK2).")]
            public float pitch;
            [Tooltip("Backs off by the kart's forward speed / speedPullBackDivisor m, at most this far (MHK2 camera 2: 4 m, 0 = off).")]
            public float speedPullBack;
            [Tooltip("Off: the kart is hidden in this view (first-person view).")]
            public bool showKart = true;
            [Tooltip("Attached to the kart (first-person view): the camera sits at focusPoint + offset in the kart's own " +
                     "frame and copies the kart's rotation (heading, slope and roll) every frame, plus the tilt above. Off: " +
                     "it follows the smoothed heading and, lazily, the slope (pitchFollow, pitchSmoothTime).")]
            public bool attachedToKart;
            [Tooltip("Share of the grounded kart's pitch (uphill / downhill) the view follows: 1 = the full slope, 0 = always " +
                     "level (MHK2). The last slope is kept while the kart is in the air.")]
            [Range(0, 1)] public float pitchFollow = 0.75f;
            [Tooltip("Smoothing time (s) of the slope following: higher = lazier, so bumps and short ramps hardly tilt the view.")]
            [Min(0.01f)] public float pitchSmoothTime = 0.6f;
            [Tooltip("Extra horizontal FOV (degrees) at the kart's normal top speed (see speed feel). Boosts add their own on top.")]
            [Min(0)] public float speedFov;
            [Tooltip("Edge effect (SpeedLines) strength at the kart's normal top speed (0 = none, 1 = full). Boosts add more.")]
            [Min(0)] public float speedLines = 1f;
        }

        /// <summary>Chase = the driving <see cref="View"/>, Orbit = the race intro circling the kart, Finished = the race finished view.</summary>
        public enum Mode { Chase, Finished, Orbit }

        const float SmoothingFps = 60f;
        const float BoomUnset = -1f;
        const float TravelMinSpeed = 3f, TravelMaxSpeed = 150f, TravelMaxSlip = 90f;
        static readonly int ViewCount = Enum.GetValues(typeof(CameraMode)).Length;

        [Tooltip("Close (default) view: behind and above the kart, the whole kart and the track ahead in view.")]
        [SerializeField] private ViewPreset close = new() { offset = new Vector3(0f, 0.75f, -4.2f), focusPoint = new Vector3(0f, 0.6f, 0f), pitch = 7f, speedFov = 5f, speedLines = 0.6f };
        [Tooltip("Far view (MHK2 camera 2): further back and higher, more of the track. Backs off with speed.")]
        [SerializeField] private ViewPreset far = new() { offset = new Vector3(0f, 2.2f, -8f), focusPoint = new Vector3(0f, 1f, 0f), pitch = 10f, speedPullBack = 4f, pitchFollow = 0.6f, pitchSmoothTime = 0.8f, speedFov = 10f, speedLines = 1f };
        [Tooltip("POV view: in front of the kart looking forward, attached to it, the kart hidden.")]
        [SerializeField] private ViewPreset pov = new() { offset = new Vector3(0f, 0.1f, 1f), focusPoint = new Vector3(0f, 0.7f, 0f), pitch = 3f, showKart = false, attachedToKart = true, pitchFollow = 1f, speedFov = 4f, speedLines = 0.5f };
        [Tooltip("Per-frame (60 fps) yaw smoothing ('Orientation Smoothing').")]
        [Range(0, 1)] [SerializeField] private float orientationSmoothing = 0.8f;
        [Tooltip("Share of the kart's slip angle (between its heading and its direction of travel) by which the driving " +
                 "view turns towards the direction of travel, so a drifting kart shows its side. 0 = MHK2: the view " +
                 "follows the heading only.")]
        [Range(0, 1)] [SerializeField] private float travelFollow;
        [Tooltip("Rate (1/s) of the filter on the slip angle the view follows: lower = calmer, higher = quicker.")]
        [Min(0.1f)] [SerializeField] private float travelFollowRate = 6f;
        [Tooltip("Wheels on the ground before the kart's pitch is followed. With fewer (jumps, crests, landings) the last " +
                 "pitch is held.")]
        [Range(1, 4)] [SerializeField] private int groundPitchMinWheels = 3;
        [Tooltip("Layers that pull the camera in front of walls. Karts never block it.")]
        [SerializeField] private LayerMask obstacleMask = ~0;
        [Tooltip("Looking back: on = the current view turned round (in front of the kart in Close / Far), so the driver is " +
                 "seen turning round. Off = MHK2 'Rear View' from the driver's eye point with the kart hidden.")]
        [SerializeField] private bool rearViewShowsKart = true;
        [Tooltip("Eye height (m) over the kart of the MHK2 rear view (rearViewShowsKart off).")]
        [SerializeField] private float rearEyeHeight = 0.75f;

        [Header("Orbit")]
        [Tooltip("Race intro orbit speed (degrees/s).")]
        [SerializeField] private float orbitSpeed = 25f;
        [Tooltip("Orbit camera height (m) over the kart.")]
        [SerializeField] private float orbitHeight = 1.6f;
        [Tooltip("Orbit radius (m) around the kart.")]
        [SerializeField] private float orbitRadius = 5f;
        [Tooltip("Height (m) over the kart the orbit camera looks at.")]
        [SerializeField] private float orbitLookHeight = 0.6f;

        [Header("Finish view")]
        [Tooltip("Height (m) over the kart the finish offset starts from.")]
        [SerializeField] private float finishHeight = 1f;
        [Tooltip("Finish camera offset in the kart's heading frame (in front of the kart, looking back).")]
        [SerializeField] private Vector3 finishOffset = new(0f, 0.2f, 1.8f);
        [Tooltip("Height (m) over the kart the finish camera looks at.")]
        [SerializeField] private float finishLookHeight = 0.6f;

        [Header("Boom")]
        [Tooltip("Gap (m) kept between the camera and a wall it hits.")]
        [SerializeField] private float wallSkin = 0.1f;
        [Tooltip("A wall hit pulls the camera in to at most this distance (m) from the focus point.")]
        [SerializeField] private float wallPullIn = 0.5f;
        [Tooltip("Time (s) the camera stays pulled in after a wall hit before it grows back.")]
        [SerializeField] private float wallHoldTime = 0.2f;
        [Tooltip("Speed (m/s) at which the camera grows back to the view's distance.")]
        [SerializeField] private float boomRegrowSpeed = 4f;
        [Tooltip("Speed pull-back = forward speed (MHK2 units) / this, capped by ViewPreset.speedPullBack.")]
        [SerializeField] private float speedPullBackDivisor = 6f;

        [Header("Speed feel")]
        [SerializeField] private CameraSpeedFeel speedFeel = new();

        IKartView target;
        int obstacleRayMask;
        float yaw;
        float pitch, pitchVelocity, pitchGoal;
        float curDist, maxDist;
        float holdTimer;
        bool initialised;
        float orbitAngle;
        bool kartHidden;
        Vector3 lastKartPos;
        float travelSlip;
        bool travelTracked;

        /// <summary>Kart followed: the player's kart, or the ghost watched in a time-trial replay.</summary>
        public IKartView Target
        {
            get => target;
            set
            {
                if (kartHidden && HasTarget && target.Visual != null) target.Visual.SetRenderersVisible(true);
                kartHidden = false;
                target = value;
            }
        }

        /// <summary>Input of a viewer without a kart (replays): its Camera action cycles the view like a player's.</summary>
        public PlayerKartDriver Viewer { get; set; }

        /// <summary>Current mode. The race sets Orbit (intro) and Finished, Chase returns to the driving view.</summary>
        public Mode CurrentMode { get; set; } = Mode.Chase;

        /// <summary>Driving view. Starts with the player's choice (UserSettings.cameraMode), the Camera action cycles it.</summary>
        public CameraMode View { get; private set; } = CameraMode.Close;

        ViewPreset Preset => View switch
        {
            CameraMode.Far => far,
            CameraMode.Pov => pov,
            _ => close,
        };

        bool HasTarget => target is UnityEngine.Object o ? o != null : target != null;

        bool PitchGrounded => target.GroundContacts >= groundPitchMinWheels;

        void Awake()
        {
            speedFeel.Bind(GetComponent<Camera>(), GetComponent<SpeedLines>());
            obstacleRayMask = obstacleMask.value & ~GameLayers.KartMask;
            View = GameRoot.Instance.Settings.cameraMode;
        }

        /// <summary>Places the camera on the target at once, without smoothing (after a target or mode change).</summary>
        public void SnapToTarget()
        {
            initialised = false;
            speedFeel.ResetState();
            UpdatePose(Time.deltaTime);
        }

        /// <summary>Next driving view (Close, Far, POV, Close...), remembered in the user settings.</summary>
        public void CycleView()
        {
            SetView((CameraMode)(((int)View + 1) % ViewCount));
            var root = GameRoot.Instance;
            root.Settings.cameraMode = View;
            root.SaveSettings();
        }

        /// <summary>Switches to the driving view <paramref name="view"/> without remembering it (menu background race).</summary>
        public void SetView(CameraMode view)
        {
            View = view;
            maxDist = BoomUnset;
        }

        void LateUpdate()
        {
            if (HasTarget) PollViewInput();
            UpdatePose(Time.deltaTime);
        }

        void PollViewInput()
        {
            if (CurrentMode != Mode.Chase) return;
            var input = target is Kart k ? k.Driver as PlayerKartDriver : Viewer;
            if (input != null && input.Camera.WasPressedThisFrame()) CycleView();
        }

        void UpdatePose(float dt)
        {
            if (!HasTarget)
            {
                speedFeel.Hide();
                return;
            }
            var kt = target.transform;
            var preset = Preset;
            var driving = CurrentMode == Mode.Chase;
            var lookBack = driving && target.CurrentInput.lookBack && target.ControlsEnabled;
            SetKartHidden(driving && (lookBack ? !rearViewShowsKart || !preset.showKart : !preset.showKart));
            TickSpeedFeel(preset, driving, lookBack, dt);
            if (CurrentMode == Mode.Orbit)
            {
                PlaceOrbitView(kt, preset, dt);
                return;
            }
            var targetYaw = KartYaw(kt);
            var travelYaw = TravelYawOffset(kt, targetYaw, dt);
            UpdateHeading(kt, preset, driving ? targetYaw + travelYaw : targetYaw, dt);
            var attached = preset.attachedToKart && driving;
            var q = attached ? kt.rotation : Quaternion.Euler(-pitch, yaw, 0f);
            var focus = kt.position + (attached ? kt.rotation * preset.focusPoint : preset.focusPoint) + target.QuakeOffset;
            if (lookBack) PlaceRearView(kt, preset, focus, attached, targetYaw);
            else if (CurrentMode == Mode.Finished) PlaceFinishView(kt, q);
            else PlaceChaseView(preset, focus, q, dt);
        }

        void TickSpeedFeel(ViewPreset preset, bool driving, bool lookBack, float dt)
        {
            // The rear view, intro orbit and finish view show no speed feel (they cut in, so they switch at once).
            var boostGoal = driving ? target.CameraFovBoost : 0f;
            var speed01 = driving ? Mathf.Clamp01(target.SpeedFraction) : 0f;
            speedFeel.Tick(boostGoal, speed01, driving && !lookBack, preset.speedFov, preset.speedLines, dt);
        }

        void PlaceOrbitView(Transform kt, ViewPreset preset, float dt)
        {
            // Circles in the ground plane under the kart (as far as the view follows slopes) so it never dives into a slope.
            orbitAngle += orbitSpeed * dt;
            var up = Vector3.Slerp(Vector3.up, kt.up, preset.pitchFollow);
            var ahead = Vector3.ProjectOnPlane(kt.forward, up).normalized;
            var around = Quaternion.AngleAxis(orbitAngle, up) * ahead;
            transform.position = kt.position + up * orbitHeight - around * orbitRadius;
            transform.LookAt(kt.position + up * orbitLookHeight);
            initialised = false;
        }

        void UpdateHeading(Transform kt, ViewPreset preset, float targetYaw, float dt)
        {
            if (!initialised) ResetFollowState(kt, preset, targetYaw);
            var s = 1f - Mathf.Pow(orientationSmoothing, dt * SmoothingFps);
            yaw += Mathf.DeltaAngle(yaw, targetYaw) * s;
            // Lazy slope: a critically damped follow of the grounded kart's pitch, easing towards the last grounded slope
            // in the air. Also kept up to date in the attached view, so switching views doesn't jump.
            if (PitchGrounded) pitchGoal = KartPitch(kt) * preset.pitchFollow;
            pitch = Mathf.SmoothDamp(pitch, pitchGoal, ref pitchVelocity, preset.pitchSmoothTime, Mathf.Infinity, dt);
        }

        void ResetFollowState(Transform kt, ViewPreset preset, float targetYaw)
        {
            yaw = targetYaw;
            pitchGoal = PitchGrounded ? KartPitch(kt) * preset.pitchFollow : 0f;
            pitch = pitchGoal;
            pitchVelocity = 0f;
            maxDist = BoomUnset;
            holdTimer = 0f;
            travelSlip = 0f;
            travelTracked = false;
            initialised = true;
        }

        void PlaceRearView(Transform kt, ViewPreset preset, Vector3 focus, bool attached, float targetYaw)
        {
            // MHK2 'Rear View' has no orientation smoothing: the view flips at once (facing back, the slope is reversed).
            var back = attached ? kt.rotation * Quaternion.Euler(0f, 180f, 0f) : Quaternion.Euler(pitch, targetYaw + 180f, 0f);
            if (!rearViewShowsKart)
            {
                transform.SetPositionAndRotation(kt.position + (attached ? kt.up : Vector3.up) * rearEyeHeight, back);
                return;
            }
            var rear = focus + back * preset.offset;
            ResolveObstacle(focus, ref rear);
            transform.SetPositionAndRotation(rear, back * Quaternion.Euler(preset.pitch, 0f, 0f));
        }

        void PlaceFinishView(Transform kt, Quaternion q)
        {
            transform.position = kt.position + Vector3.up * finishHeight + q * finishOffset;
            transform.LookAt(kt.position + Vector3.up * finishLookHeight);
        }

        void PlaceChaseView(ViewPreset preset, Vector3 focus, Quaternion q, float dt)
        {
            var desired = focus + q * UpdateBoomLength(preset, dt);
            if (preset.speedPullBack > 0f)
            {
                var la = Mathf.Min(preset.speedPullBack, Mathf.Max(0f, target.ForwardSpeedOriginal) / speedPullBackDivisor);
                desired -= q * Vector3.forward * la;
            }
            if (ResolveObstacle(focus, ref desired))
            {
                curDist = Mathf.Min((desired - focus).magnitude, wallPullIn);
                holdTimer = wallHoldTime;
            }
            transform.SetPositionAndRotation(desired, q * Quaternion.Euler(preset.pitch, 0f, 0f));
        }

        Vector3 UpdateBoomLength(ViewPreset preset, float dt)
        {
            var offset = preset.offset;
            var full = offset.magnitude;
            if (maxDist != full)
            {
                curDist = maxDist < 0f ? full : Mathf.Min(curDist, full);
                maxDist = full;
            }
            if (holdTimer > 0f) holdTimer -= dt;
            else if (curDist < maxDist) curDist = Mathf.Min(maxDist, curDist + boomRegrowSpeed * dt);
            return curDist < maxDist ? offset.normalized * curDist : offset;
        }

        bool ResolveObstacle(Vector3 focus, ref Vector3 point)
        {
            var toPoint = point - focus;
            if (!Physics.Raycast(focus, toPoint.normalized, out var hit, toPoint.magnitude, obstacleRayMask, QueryTriggerInteraction.Ignore))
                return false;
            point = hit.point + hit.normal * wallSkin;
            return true;
        }

        void SetKartHidden(bool hidden)
        {
            if (hidden == kartHidden || target.Visual == null) return;
            kartHidden = hidden;
            target.Visual.SetRenderersVisible(!hidden);
        }

        static float KartYaw(Transform t)
        {
            var f = t.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        float TravelYawOffset(Transform kt, float headingYaw, float dt)
        {
            var step = Vector3.ProjectOnPlane(kt.position - lastKartPos, Vector3.up);
            lastKartPos = kt.position;
            var tracked = travelTracked;
            travelTracked = true;
            var goal = 0f;
            if (tracked && dt > 0f)
            {
                var speed = step.magnitude / dt;
                var slip = Mathf.DeltaAngle(headingYaw, Mathf.Atan2(step.x, step.z) * Mathf.Rad2Deg);
                if (speed > TravelMinSpeed && speed < TravelMaxSpeed && Mathf.Abs(slip) < TravelMaxSlip) goal = slip;
            }
            travelSlip = Mathf.Lerp(travelSlip, goal, Smoothing.Factor(travelFollowRate, dt));
            return travelFollow * travelSlip;
        }

        static float KartPitch(Transform t) => Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
