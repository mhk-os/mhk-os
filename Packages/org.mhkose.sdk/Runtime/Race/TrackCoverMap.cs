using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Top-down height map of what covers a track (roofs, tunnels, bridges): the height of the topmost opaque render
    /// surface per texel over the track's ground plan, baked in the editor. Wet options use it so rain wets and loosens only
    /// what nothing covers, the same on every machine (the physics reads it, so it must not depend on the GPU).
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Track Cover Map", fileName = "TrackCoverMap")]
    public class TrackCoverMap : ScriptableObject
    {
        [Tooltip("World x/z of the map's corner at texel (0, 0).")]
        public Vector2 min;
        [Tooltip("World size (m) of the map along x and z.")]
        public Vector2 size = Vector2.one;
        [Tooltip("World height of the value 0 (lowest surface).")]
        public float minY;
        [Tooltip("World height range of the values: height = minY + value × range.")]
        public float range = 1f;
        [Tooltip("Heights (R16, readable), normalized over minY .. minY + range. 0 = nothing covers the texel.")]
        public Texture2D heights;

        ushort[] data;
        int width, height;

        /// <summary>The map has data to sample.</summary>
        public bool IsValid => heights != null && size.x > 0f && size.y > 0f;

        /// <summary>Replaces the map's data (the editor bake); heights read before are dropped.</summary>
        public void Assign(Vector2 corner, Vector2 extent, float lowest, float heightRange, Texture2D map)
        {
            min = corner;
            size = extent;
            minY = lowest;
            range = heightRange;
            heights = map;
            data = null;
        }

        /// <summary>
        /// Reads the heights for <see cref="Covered"/> now (once, later calls are free). False when the map has no
        /// readable R16 heights: then nothing counts as covered.
        /// </summary>
        public bool Prepare()
        {
            if (data != null) return true;
            if (!IsValid || !heights.isReadable || heights.format != TextureFormat.R16) return false;
            width = heights.width;
            height = heights.height;
            data = heights.GetPixelData<ushort>(0).ToArray();
            return true;
        }

        /// <summary>
        /// Whether something covers <paramref name="position"/>: the topmost surface at its texel (nearest) lies more than
        /// <paramref name="bias"/> metres above it. Outside the map nothing does.
        /// </summary>
        public bool Covered(Vector3 position, float bias)
        {
            if (!Prepare()) return false;
            var u = (position.x - min.x) / size.x;
            var v = (position.z - min.y) / size.y;
            if (u < 0f || v < 0f || u >= 1f || v >= 1f) return false;
            var x = Mathf.Min(width - 1, (int)(u * width));
            var y = Mathf.Min(height - 1, (int)(v * height));
            var value = data[y * width + x];
            return value != 0 && minY + value * (range / ushort.MaxValue) > position.y + bias;
        }

        void OnDisable() => data = null;
    }
}
