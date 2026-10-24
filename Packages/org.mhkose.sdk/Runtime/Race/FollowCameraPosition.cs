using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Keeps a sky dome centred on the camera without rotation, placing its painted horizon at eye level and shrinking
    /// it inside the far clip plane.
    /// </summary>
    [DefaultExecutionOrder(1000)] // after the cameras have moved (ChaseCamera.LateUpdate)
    public class FollowCameraPosition : MonoBehaviour
    {
        [Tooltip("Followed transform, empty = the main camera.")]
        [SerializeField] Transform target;
        [Tooltip("Follow vertical camera movement too. Off: the object keeps its own height and size.")]
        [SerializeField] bool followHeight = true;
        [Tooltip("Height of the dome's painted horizon as a fraction of its shell's height (0 = lowest point, 1 = top), " +
                 "placed at the camera's eye level. Negative = the dome's pivot at the eye, as modelled.")]
        [Range(-1f, 1f)]
        [SerializeField] float horizon = -1f;
        [Tooltip("The shell's lowest point is kept at least this many degrees below the camera's horizon, so no " +
                 "background colour shows between the dome and the horizon.")]
        [Range(0f, 30f)]
        [SerializeField] float lowerEdgeBelowHorizon = 2f;
        [Tooltip("Shrink the dome around the eye when its farthest point is beyond this fraction of the camera's far " +
                 "clip distance (0 = never). It looks the same from the eye, but is no longer clipped.")]
        [Range(0f, 1f)]
        [SerializeField] float farClipFill = 0.9f;
        [Tooltip("Parts at least this wide (fraction of the widest part) form the dome's shell, smaller parts (a sun " +
                 "flare) only count for the far-clip fit.")]
        [Range(0f, 1f)]
        [SerializeField] float shellMinWidth = 0.5f;

        Vector3 baseScale;
        Bounds shell, all; // renderers' bounds relative to the pivot at baseScale: dome shell, every part
        bool measured;
        Transform cameraOwner;
        Camera targetCamera;

        void Start() => Measure();

        void Measure()
        {
            measured = true;
            baseScale = transform.localScale;
            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var widest = 0f;
            foreach (var r in renderers) widest = Mathf.Max(widest, Width(r.bounds));
            var hasShell = false;
            for (var i = 0; i < renderers.Length; i++)
            {
                var b = renderers[i].bounds;
                b.center -= transform.position;
                if (i == 0) all = b;
                else all.Encapsulate(b);
                if (Width(b) < widest * shellMinWidth) continue;
                if (hasShell) shell.Encapsulate(b);
                else shell = b;
                hasShell = true;
            }
        }

        static float Width(Bounds b) => Mathf.Max(b.size.x, b.size.z);

        void LateUpdate()
        {
            var cam = target == null ? Camera.main : TargetCamera();
            var eye = target != null ? target : cam != null ? cam.transform : null;
            if (eye == null) return;
            Place(eye.position, cam != null ? cam.farClipPlane : 0f);
        }

        Camera TargetCamera()
        {
            if (cameraOwner != target)
            {
                cameraOwner = target;
                targetCamera = target.GetComponent<Camera>();
            }
            return targetCamera;
        }

        /// <summary>
        /// Places the object for an eye at <paramref name="eye"/>, <paramref name="farClip"/> &lt;= 0 skips the
        /// far-clip fit.
        /// </summary>
        public void Place(Vector3 eye, float farClip)
        {
            if (!measured) Measure();
            if (!followHeight)
            {
                eye.y = transform.position.y;
                transform.position = eye;
                return;
            }
            var offset = new Vector3(0f, PivotHeight(), 0f);
            var scale = 1f;
            if (farClip > 0f && farClipFill > 0f)
            {
                var far = MaxCornerDistance(all, offset);
                var limit = farClip * farClipFill;
                if (far > limit) scale = limit / far;
            }
            transform.localScale = baseScale * scale;
            transform.position = eye + offset * scale;
        }

        // Pivot height relative to the eye, in the dome's unshrunk units.
        float PivotHeight()
        {
            var min = shell.min;
            var max = shell.max;
            var height = max.y - min.y;
            var pivotY = horizon >= 0f ? -(min.y + horizon * height) : 0f;
            var radius = Mathf.Max(Mathf.Max(-min.x, max.x), Mathf.Max(-min.z, max.z));
            var edgeLimit = -radius * Mathf.Tan(lowerEdgeBelowHorizon * Mathf.Deg2Rad) - min.y;
            return height > 0f && pivotY > edgeLimit ? edgeLimit : pivotY;
        }

        static float MaxCornerDistance(Bounds b, Vector3 offset)
        {
            var far = 0f;
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                far = Mathf.Max(far, (corner + offset).magnitude);
            }
            return far;
        }
    }
}
