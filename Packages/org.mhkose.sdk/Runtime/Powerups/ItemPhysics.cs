using System.Collections.Generic;
using MHK.Karts;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// How thrown and placed items treat the track's special collision. Fall zones (<see cref="SurfaceKind.Drop"/>) only
    /// exist for karts, so items pass through them. Walls and out-of-bounds borders stay solid. Queries skip karts,
    /// triggers and dynamic bodies so they only report static level geometry.
    /// </summary>
    public static partial class ItemPhysics
    {
        [AutoStaticsCleanup] static TrackRuntime cachedTrack;
        [AutoStaticsCleanup] static bool cached;
        [AutoStaticsCleanup] static readonly List<Collider> passThrough = new();
        [NoAutoStaticsCleanup] static readonly RaycastHit[] hits = new RaycastHit[32];

        /// <summary>True for track colliders items fly or fall through (fall zones).</summary>
        public static bool IsPassThrough(Collider c)
        {
            if (c == null) return false;
            var s = c.GetComponentInParent<TrackSurface>();
            return s != null && s.kind == SurfaceKind.Drop;
        }

        static List<Collider> PassThroughColliders()
        {
            var track = TrackRuntime.Current;
            if (cached && track == cachedTrack) return passThrough;
            cached = true;
            cachedTrack = track;
            passThrough.Clear();
            foreach (var s in Object.FindObjectsByType<TrackSurface>())
                if (s.kind == SurfaceKind.Drop)
                    passThrough.AddRange(s.GetComponentsInChildren<Collider>(true));
            return passThrough;
        }

        /// <summary>
        /// Makes the given item colliders ignore every pass-through track collider of their own physics scene. The
        /// validation's copies of the track (a separate local physics scene) are found too and must be left out:
        /// Unity refuses to pair colliders of different physics scenes.
        /// </summary>
        public static void IgnorePassThrough(IEnumerable<Collider> itemColliders)
        {
            var list = PassThroughColliders();
            foreach (var mine in itemColliders)
            {
                if (mine == null) continue;
                var physics = mine.gameObject.scene.GetPhysicsScene();
                foreach (var c in list)
                    if (c != null && c.gameObject.scene.GetPhysicsScene() == physics) Physics.IgnoreCollision(mine, c);
            }
        }

        /// <summary>Layers that collide with <paramref name="layer"/> according to the physics collision matrix.</summary>
        public static int MaskFor(int layer)
        {
            if (layer < 0) return Physics.DefaultRaycastLayers;
            var mask = 0;
            for (var i = 0; i < 32; i++)
                if (!Physics.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
            return mask;
        }

        /// <summary>Nearest static, solid level hit along a ray (fall zones, karts, triggers and rigidbodies skipped).</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float distance, out RaycastHit hit, int mask)
        {
            var n = Physics.RaycastNonAlloc(origin, direction, hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, out hit);
        }

        /// <summary>
        /// Solid ground under <paramref name="point"/>: a <see cref="Raycast"/> straight down from <paramref name="up"/> m
        /// above it to <paramref name="down"/> m below it.
        /// </summary>
        public static bool TryGroundBelow(Vector3 point, float up, float down, out RaycastHit hit, int mask) =>
            Raycast(point + Vector3.up * up, Vector3.down, up + down, out hit, mask);

        /// <summary>Like <see cref="TryGroundBelow"/> along <c>-up</c> for ground tilted to <paramref name="up"/>.</summary>
        public static bool TryGroundAlong(Vector3 point, Vector3 up, float above, float below, out RaycastHit hit, int mask) =>
            Raycast(point + up * above, -up, above + below, out hit, mask);

        /// <summary>Like <see cref="Raycast"/> between two points.</summary>
        public static bool Linecast(Vector3 from, Vector3 to, out RaycastHit hit, int mask)
        {
            var d = to - from;
            var len = d.magnitude;
            if (len < 1e-5f) { hit = default; return false; }
            return Raycast(from, d / len, len, out hit, mask);
        }

        /// <summary>Nearest static, solid level hit of a swept sphere.</summary>
        public static bool SphereCast(Vector3 origin, float radius, Vector3 direction, float distance, out RaycastHit hit, int mask)
        {
            var n = Physics.SphereCastNonAlloc(origin, radius, direction, hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, out hit);
        }

        static bool Nearest(int n, out RaycastHit best)
        {
            best = default;
            var found = false;
            for (var i = 0; i < n; i++)
            {
                var h = hits[i];
                var c = h.collider;
                if (c == null || c.attachedRigidbody != null) continue;
                // Sweeps starting inside a collider report distance 0 with no usable point.
                if (h.distance <= 0f && h.point == Vector3.zero) continue;
                if (Kart.FromCollider(c) != null || IsPassThrough(c)) continue;
                if (!found || h.distance < best.distance)
                {
                    best = h;
                    found = true;
                }
            }
            return found;
        }
    }
}
