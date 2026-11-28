using System.Collections.Generic;
using UnityEngine;

namespace MHK.Cosmetics
{
    /// <summary>
    /// What a cape (<see cref="CosmeticCloth"/>) stays out of, in the space of the kart's frame. The driver's back is a
    /// depth per height band below the neck. The kart is a height field that holds the highest surface of its meshes in
    /// each cell of a grid, so a cape lies on the engine and over the backrest. A mesh that is not readable counts as the
    /// top of its bounds, except the hull the driver sits in, whose top would cover the seat. Measured once in the pose
    /// of the spawn, for the first cape a driver wears (<see cref="CosmeticAnchors.Obstacles"/>).
    /// </summary>
    public sealed class ClothObstacles
    {
        /// <summary>The rearmost depth of the driver's back in equal bands from top down to bottom, within left to right (frame space).</summary>
        internal readonly struct BackProfile
        {
            public readonly float top, bottom, left, right;
            /// <summary>Depth (z) per band, from the top.</summary>
            public readonly float[] depths;

            public BackProfile(float top, float bottom, float left, float right, float[] depths)
            {
                this.top = top;
                this.bottom = bottom;
                this.left = left;
                this.right = right;
                this.depths = depths;
            }

            public bool IsSet => depths is { Length: > 0 } && top > bottom && right > left;
        }

        // Cells of the height field along the kart's longest side.
        const int Cells = 32;
        // Distance (m in the frame) the cloth keeps from the back and the kart.
        const float Gap = 0.015f;
        // Slack of the inside test, so a cell centre on an edge shared by two triangles counts for both.
        const float EdgeSlack = 1e-4f;
        const float MinArea = 1e-8f, MinCell = 1e-3f;

        /// <summary>The kart's frame (+y up, +z forward) the obstacles are measured in.</summary>
        public Transform Frame { get; }

        readonly BackProfile back;
        readonly Vector2 origin;
        readonly float cell;
        readonly int columns, rows;
        readonly float[] heights;

        ClothObstacles(Transform frame, BackProfile back, Vector2 origin, float cell, int columns, int rows, float[] heights)
        {
            Frame = frame;
            this.back = back;
            this.origin = origin;
            this.cell = cell;
            this.columns = columns;
            this.rows = rows;
            this.heights = heights;
        }

        /// <summary>
        /// Moves <paramref name="point"/> (frame space) behind the driver's back and up onto the kart. True when the kart
        /// holds it up.
        /// </summary>
        public bool Collide(ref Vector3 point)
        {
            if (back.IsSet && point.x >= back.left && point.x <= back.right && point.y <= back.top && point.y >= back.bottom)
            {
                var band = Mathf.Min(back.depths.Length - 1, (int)((back.top - point.y) / (back.top - back.bottom) * back.depths.Length));
                var limit = back.depths[band] - Gap;
                if (point.z > limit) point.z = limit;
            }
            if (heights == null) return false;
            var x = Mathf.FloorToInt((point.x - origin.x) / cell);
            var z = Mathf.FloorToInt((point.z - origin.y) / cell);
            if (x < 0 || z < 0 || x >= columns || z >= rows) return false;
            var floor = heights[z * columns + x] + Gap;
            if (point.y >= floor) return false;
            point.y = floor;
            return true;
        }

        /// <summary>
        /// Measures the kart's renderers below <paramref name="frame"/> but outside <paramref name="driver"/> and its
        /// cosmetics. <paramref name="driverCentre"/> (frame space) finds the hull the driver sits in.
        /// </summary>
        internal static ClothObstacles Measure(Transform frame, Transform driver, BackProfile back, Vector3 driverCentre)
        {
            var corners = new List<Vector3>();
            CollectKart(frame, driver, driverCentre, corners);
            if (corners.Count == 0) return new ClothObstacles(frame, back, default, 1f, 0, 0, null);
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in corners)
            {
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
            }
            var cell = Mathf.Max(MinCell, Mathf.Max(maxX - minX, maxZ - minZ) / Cells);
            var columns = Mathf.FloorToInt((maxX - minX) / cell) + 1;
            var rows = Mathf.FloorToInt((maxZ - minZ) / cell) + 1;
            var heights = new float[columns * rows];
            for (var i = 0; i < heights.Length; i++) heights[i] = float.NegativeInfinity;
            var field = new ClothObstacles(frame, back, new Vector2(minX, minZ), cell, columns, rows, heights);
            for (var t = 0; t + 2 < corners.Count; t += 3) field.Rasterize(corners[t], corners[t + 1], corners[t + 2]);
            return field;
        }

        // Frame-space corners of the kart's triangles, three per triangle.
        static void CollectKart(Transform frame, Transform driver, Vector3 driverCentre, List<Vector3> corners)
        {
            var baked = new Mesh { name = "Cloth obstacles" };
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            try
            {
                foreach (var r in frame.GetComponentsInChildren<Renderer>())
                {
                    if (!r.enabled || r is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                    if (r.transform.IsChildOf(driver) || r.GetComponentInParent<CosmeticItem>() != null) continue;
                    var mesh = ReadableMesh(r, baked);
                    if (mesh == null)
                    {
                        AddTop(frame, r.bounds, driverCentre, corners);
                        continue;
                    }
                    var toFrame = frame.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    mesh.GetVertices(vertices);
                    for (var s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                        mesh.GetIndices(indices, s);
                        foreach (var index in indices) corners.Add(toFrame.MultiplyPoint3x4(vertices[index]));
                    }
                }
            }
            finally
            {
                if (Application.isPlaying) Object.Destroy(baked);
                else Object.DestroyImmediate(baked);
            }
        }

        // The mesh of r with readable vertices, baked into baked for a skinned one (as CosmeticAnchors bakes them).
        static Mesh ReadableMesh(Renderer r, Mesh baked)
        {
            if (r is SkinnedMeshRenderer skin)
            {
                if (skin.sharedMesh == null || !skin.sharedMesh.isReadable) return null;
                skin.BakeMesh(baked, true);
                return baked;
            }
            var mesh = r.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
            return mesh != null && mesh.isReadable ? mesh : null;
        }

        // The top of the bounds as two triangles, left out when they hold the driver's middle.
        static void AddTop(Transform frame, Bounds world, Vector3 driverCentre, List<Vector3> corners)
        {
            var box = new Bounds(frame.InverseTransformPoint(Core.BoundsUtil.Corner(world, 0)), Vector3.zero);
            for (var k = 1; k < 8; k++) box.Encapsulate(frame.InverseTransformPoint(Core.BoundsUtil.Corner(world, k)));
            if (box.Contains(driverCentre)) return;
            var y = box.max.y;
            Vector3 a = new(box.min.x, y, box.min.z), b = new(box.max.x, y, box.min.z), c = new(box.max.x, y, box.max.z), d = new(box.min.x, y, box.max.z);
            corners.Add(a);
            corners.Add(b);
            corners.Add(c);
            corners.Add(a);
            corners.Add(c);
            corners.Add(d);
        }

        // Raises the cells whose centres the triangle covers to its height there. Its corners raise their own cells, so
        // small and upright triangles count too.
        void Rasterize(Vector3 a, Vector3 b, Vector3 c)
        {
            Raise(a.x, a.z, a.y);
            Raise(b.x, b.z, b.y);
            Raise(c.x, c.z, c.y);
            var area = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
            if (Mathf.Abs(area) < MinArea) return;
            var x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - origin.x) / cell));
            var x1 = Mathf.Min(columns - 1, Mathf.FloorToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - origin.x) / cell));
            var z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - origin.y) / cell));
            var z1 = Mathf.Min(rows - 1, Mathf.FloorToInt((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - origin.y) / cell));
            for (var z = z0; z <= z1; z++)
            for (var x = x0; x <= x1; x++)
            {
                var px = origin.x + (x + 0.5f) * cell;
                var pz = origin.y + (z + 0.5f) * cell;
                var wb = ((px - a.x) * (c.z - a.z) - (c.x - a.x) * (pz - a.z)) / area;
                var wc = ((b.x - a.x) * (pz - a.z) - (px - a.x) * (b.z - a.z)) / area;
                var wa = 1f - wb - wc;
                if (wa < -EdgeSlack || wb < -EdgeSlack || wc < -EdgeSlack) continue;
                var i = z * columns + x;
                heights[i] = Mathf.Max(heights[i], wa * a.y + wb * b.y + wc * c.y);
            }
        }

        void Raise(float x, float z, float y)
        {
            var cx = Mathf.Clamp(Mathf.FloorToInt((x - origin.x) / cell), 0, columns - 1);
            var cz = Mathf.Clamp(Mathf.FloorToInt((z - origin.y) / cell), 0, rows - 1);
            var i = cz * columns + cx;
            heights[i] = Mathf.Max(heights[i], y);
        }
    }
}
