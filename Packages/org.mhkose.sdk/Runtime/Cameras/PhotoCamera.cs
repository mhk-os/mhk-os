using MHK.Effects;
using MHK.Karts;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Cameras
{
    /// <summary>
    /// Free-fly (noclip) control of the race camera for the photo mode. It takes the race camera itself over at its current
    /// pose, so the pictures keep every setting it renders with (culling mask, clip planes, clear flags, URP data and
    /// post-processing) and it stays the main camera that weather effects and sky domes follow. Its driver (the chase
    /// camera) and the speed lines are held off meanwhile, and a kart the first-person view hides is shown.
    /// <see cref="Release"/> puts pose, driver, effects and kart back exactly as they were.
    /// </summary>
    public sealed class PhotoCamera
    {
        const float PitchLimit = 89f;

        readonly Camera cam;
        readonly Transform t;
        readonly ChaseCamera chase;
        readonly SpeedLines speedLines;
        readonly Vector3 savedPosition;
        readonly Quaternion savedRotation;
        readonly bool chaseWasEnabled, linesWereEnabled;
        readonly KartVisual shownKart;
        float yaw, pitch;
        bool released;

        /// <summary>The camera flown (the race camera).</summary>
        public Camera Camera => cam;

        /// <summary>The camera's transform, for directions relative to the view.</summary>
        public Transform Transform => t;

        /// <summary>Takes <paramref name="camera"/> over, driven until now by <paramref name="chase"/> (may be null).</summary>
        public PhotoCamera(Camera camera, ChaseCamera chase)
        {
            cam = camera;
            t = camera.transform;
            this.chase = chase;
            savedPosition = t.localPosition;
            savedRotation = t.localRotation;
            var forward = t.forward;
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg, -PitchLimit, PitchLimit);
            if (chase != null)
            {
                chaseWasEnabled = chase.enabled;
                chase.enabled = false;
                shownKart = HiddenKart(chase.Target);
                if (shownKart != null) shownKart.SetRenderersVisible(true);
            }
            speedLines = camera.GetComponent<SpeedLines>();
            if (speedLines != null)
            {
                linesWereEnabled = speedLines.enabled;
                speedLines.enabled = false;
            }
        }

        // The followed kart when the chase camera hides it (first-person view: every renderer off), else null.
        static KartVisual HiddenKart(IKartView target)
        {
            if (target is Object o ? o == null : target == null) return null;
            var visual = target.Visual;
            if (visual == null) return null;
            var renderers = visual.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return null;
            foreach (var r in renderers)
                if (r.enabled) return null;
            return visual;
        }

        /// <summary>Moves the camera by <paramref name="delta"/> (world space, nothing collides).</summary>
        public void Move(Vector3 delta) => t.position += delta;

        /// <summary>
        /// Turns the view by <paramref name="yawDegrees"/> (right) and <paramref name="pitchDegrees"/> (down), kept level
        /// and short of straight up or down. The first turn drops the roll the race view may have had.
        /// </summary>
        public void Look(float yawDegrees, float pitchDegrees)
        {
            if (yawDegrees == 0f && pitchDegrees == 0f) return;
            yaw = Mathf.Repeat(yaw + yawDegrees, 360f);
            pitch = Mathf.Clamp(pitch + pitchDegrees, -PitchLimit, PitchLimit);
            t.rotation = Quaternion.Euler(pitch, yaw, 0f);
        }

        /// <summary>
        /// Renders the camera view (no UI) at <paramref name="width"/> x <paramref name="height"/> into a new readable
        /// RGB24 texture (sRGB values, row 0 at the bottom). The caller owns and destroys it.
        /// </summary>
        public Texture2D Capture(int width, int height)
        {
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24)
            {
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
            };
            var target = RenderTexture.GetTemporary(desc);
            var active = RenderTexture.active;
            try
            {
                Render(target);
                RenderTexture.active = target;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false) { name = "Photo" };
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return tex;
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        void Render(RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
            {
                RenderPipeline.SubmitRenderRequest(cam, request);
                return;
            }
            var previous = cam.targetTexture;
            cam.targetTexture = target;
            try { cam.Render(); }
            finally { cam.targetTexture = previous; }
        }

        /// <summary>Gives the camera back: the pose it was taken over at, its driver, the speed lines and the kart's hiding.</summary>
        public void Release()
        {
            if (released) return;
            released = true;
            if (t != null) t.SetLocalPositionAndRotation(savedPosition, savedRotation);
            if (chase != null) chase.enabled = chaseWasEnabled;
            if (speedLines != null) speedLines.enabled = linesWereEnabled;
            if (shownKart != null) shownKart.SetRenderersVisible(false);
        }
    }
}
