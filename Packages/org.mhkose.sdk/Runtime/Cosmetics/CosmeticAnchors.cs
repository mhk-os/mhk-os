using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;

namespace MHK.Cosmetics
{
    /// <summary>
    /// Where cosmetics sit on one seated driver, measured once from its meshes in the pose it has when it is spawned, so
    /// every character wears the same hat without settings of its own. The head is the bone that carries most of the
    /// top quarter of the driver (with the bones below it, such as eyes, ears or a comb). A hat sits on top of the head
    /// and is scaled to its width. Body items hang from the back of the neck, at the head bone's joint, on the bone that
    /// carries the back there. Drivers without a readable skinned mesh fall back to their bounds. The depth of the back
    /// below the neck is measured too, and what a cape stays out of (<see cref="Obstacles"/>) once a cape asks for it.
    /// </summary>
    public sealed class CosmeticAnchors
    {
        /// <summary>
        /// A place on the driver, stored in the space of the bone that carries it so it follows the animation. The
        /// rotation is upright in the kart's frame of the measured pose, the width is the head's or the back's in
        /// metres of that frame.
        /// </summary>
        public readonly struct Anchor
        {
            public readonly Transform bone;
            public readonly Vector3 position;
            public readonly Quaternion rotation;
            public readonly float width;
            /// <summary>Length in bone space of one metre in the kart's frame.</summary>
            public readonly float frameToBone;

            public Anchor(Transform bone, Vector3 position, Quaternion rotation, float width, float frameToBone)
            {
                this.bone = bone;
                this.position = position;
                this.rotation = rotation;
                this.width = width;
                this.frameToBone = frameToBone;
            }

            public bool IsSet => bone != null;
        }

        // Share of the driver's height whose vertices pick the head bone, the share of them it must carry, the share of
        // the area they cover from above its own vertices must cover, and the cells across the grid that measures it.
        const float HeadBand = 0.25f, HeadShare = 0.5f, FootprintShare = 0.6f;
        const int FootprintCells = 16;
        // A hat sits at this percentile of the head's heights, centred on the top part of the head (share of its height),
        // between these low and high percentiles of that part's sideways and front to back extent.
        const float TopPercentile = 0.95f, CapShare = 0.25f, CapEdge = 0.05f;
        // Neck height of a driver whose head is no bone of its own (share of the driver's height), and the band around the
        // neck that measures the back (share of the height).
        const float NeckHeight = 0.68f, NeckBand = 0.06f;
        // The middle column (half width as a share of the head's width), the percentile of its depths that is the back,
        // the depth of the slab at the back (share of the head's width) and the range of body widths (shares of it).
        const float ColumnShare = 0.5f, BackPercentile = 0.1f, SlabDepthShare = 0.3f, MinBodyShare = 0.8f, MaxBodyShare = 1.3f;
        // Height bands of the back's depth below the neck, which a cape keeps behind, and how far behind the neck (share of
        // the head's width) the lowest band may reach. The bands above reach less, so a cape hangs from the neck through a
        // big tail or a shell rather than far behind it.
        const int BackBands = 10;
        const float MaxBackShare = 0.4f;
        // Fallbacks of a driver without skinned meshes, as shares of its bounds.
        const float FallbackHeadWidth = 0.5f, FallbackBackWidth = 0.6f;
        const float MinScale = 1e-6f;

        public Anchor Head { get; }
        public Anchor Back { get; }

        readonly Transform frame, driver;
        readonly ClothObstacles.BackProfile backProfile;
        readonly Vector3 centre;
        ClothObstacles obstacles;

        CosmeticAnchors(Transform frame, Transform driver, Anchor head, Anchor back, ClothObstacles.BackProfile backProfile, Vector3 centre)
        {
            this.frame = frame;
            this.driver = driver;
            Head = head;
            Back = back;
            this.backProfile = backProfile;
            this.centre = centre;
        }

        /// <summary>What a cape on this driver stays out of, its back and the kart, measured at the first call.</summary>
        public ClothObstacles Obstacles => obstacles ??= ClothObstacles.Measure(frame, driver, backProfile, centre);

        /// <summary>The kart's frame the anchors were measured in.</summary>
        public Transform Frame => frame;

        /// <summary>
        /// The deepest point of the driver's back (frame z) between the heights <paramref name="bottom"/> and
        /// <paramref name="top"/> (frame y), as the back depth below the neck measures it. Infinity where it is not measured.
        /// </summary>
        public float BackDepth(float bottom, float top)
        {
            var p = backProfile;
            if (!p.IsSet || top < p.bottom || bottom > p.top) return float.PositiveInfinity;
            var deepest = float.PositiveInfinity;
            for (var b = 0; b < p.depths.Length; b++)
            {
                var bandTop = p.top - (p.top - p.bottom) * b / p.depths.Length;
                var bandBottom = p.top - (p.top - p.bottom) * (b + 1) / p.depths.Length;
                if (bandBottom <= top && bandTop >= bottom) deepest = Mathf.Min(deepest, p.depths[b]);
            }
            return deepest;
        }

        /// <summary>The anchor of <paramref name="slot"/>.</summary>
        public Anchor Of(CosmeticSlot slot) => slot == CosmeticSlot.Hat ? Head : Back;

        /// <summary>
        /// Measures the driver below <paramref name="driver"/> (the instantiated driver prefab) in the space of
        /// <paramref name="frame"/> (the kart, whose +y is up and +z forward). Null when it draws nothing.
        /// </summary>
        public static CosmeticAnchors Measure(Transform frame, Transform driver)
        {
            if (frame == null || driver == null) return null;
            var points = new List<Vector3>();
            var bones = new List<Transform>();
            CollectSkinned(frame, driver, points, bones);
            if (points.Count > 0) return FromSkin(frame, driver, points, bones);
            return TryRendererBounds(frame, driver, out var b) ? FromBounds(frame, driver, b) : null;
        }

        // Baked vertices of the readable skinned meshes in the frame's space, each with the bone that moves it most.
        static void CollectSkinned(Transform frame, Transform driver, List<Vector3> points, List<Transform> bones)
        {
            var baked = new Mesh { name = "Cosmetic anchors" };
            var vertices = new List<Vector3>();
            try
            {
                foreach (var skin in driver.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var mesh = skin.sharedMesh;
                    if (!skin.enabled || mesh == null || !mesh.isReadable) continue;
                    var skinBones = skin.bones;
                    var weights = mesh.boneWeights;
                    if (skinBones.Length == 0 || weights.Length != mesh.vertexCount) continue;
                    // Baked with the renderer's scale, the renderer's matrix brings it to world space (as AfterimageTrail does).
                    skin.BakeMesh(baked, true);
                    baked.GetVertices(vertices);
                    var toFrame = frame.worldToLocalMatrix * skin.transform.localToWorldMatrix;
                    for (var i = 0; i < vertices.Count && i < weights.Length; i++)
                    {
                        var index = weights[i].boneIndex0;
                        var bone = index >= 0 && index < skinBones.Length ? skinBones[index] : null;
                        if (bone == null) continue;
                        points.Add(toFrame.MultiplyPoint3x4(vertices[i]));
                        bones.Add(bone);
                    }
                }
            }
            finally
            {
                if (Application.isPlaying) Object.Destroy(baked);
                else Object.DestroyImmediate(baked);
            }
        }

        static CosmeticAnchors FromSkin(Transform frame, Transform driver, List<Vector3> points, List<Transform> bones)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            var sum = Vector3.zero;
            foreach (var p in points)
            {
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
                sum += p;
            }
            var height = maxY - minY;
            var head = HeadBone(driver, points, bones, maxY - HeadBand * height);
            MeasureHead(points, bones, head, out var top, out var headWidth);
            // Rigs put the head bone's joint at the neck, where a cape is tied.
            var neck = head != driver ? frame.InverseTransformPoint(head.position).y : minY + NeckHeight * height;
            var back = BackAnchor(frame, points, bones, Spine(driver, head), top.x, headWidth, neck, NeckBand * height);
            var profile = default(ClothObstacles.BackProfile);
            if (back.IsSet)
            {
                var neckDepth = frame.InverseTransformPoint(back.bone.TransformPoint(back.position)).z;
                profile = BackProfile(points, top.x, 0.5f * back.width, minY, neck, neckDepth, MaxBackShare * headWidth);
            }
            return new CosmeticAnchors(frame, driver, ToBone(frame, head, top, headWidth), back, profile, sum / points.Count);
        }

        // The back's depth in bands from the neck down to the lowest vertex, each a low percentile of the column's depths
        // as BackAnchor measures it at the neck. Band b reaches at most (b + 1) / BackBands of reach behind the neck depth.
        // A band without vertices takes the depth of the nearest band with some.
        static ClothObstacles.BackProfile BackProfile(List<Vector3> points, float centre, float half, float bottom, float neck, float neckDepth,
            float reach)
        {
            if (neck <= bottom) return default;
            var bands = new List<float>[BackBands];
            for (var b = 0; b < BackBands; b++) bands[b] = new List<float>();
            var any = false;
            foreach (var p in points)
            {
                if (Mathf.Abs(p.x - centre) > half || p.y < bottom || p.y > neck) continue;
                bands[Mathf.Min(BackBands - 1, (int)((neck - p.y) / (neck - bottom) * BackBands))].Add(p.z);
                any = true;
            }
            if (!any) return default;
            var depths = new float[BackBands];
            for (var b = 0; b < BackBands; b++)
            {
                var band = bands[b];
                band.Sort();
                var deepest = neckDepth - reach * (b + 1) / BackBands;
                depths[b] = band.Count > 0 ? Mathf.Max(deepest, band[(int)(BackPercentile * (band.Count - 1))]) : float.NaN;
            }
            for (var b = 0; b < BackBands; b++)
                for (var d = 1; d < BackBands && float.IsNaN(depths[b]); d++)
                {
                    if (b - d >= 0 && bands[b - d].Count > 0) depths[b] = depths[b - d];
                    else if (b + d < BackBands && bands[b + d].Count > 0) depths[b] = depths[b + d];
                }
            return new ClothObstacles.BackProfile(neck, bottom, centre - half, centre + half, depths);
        }

        // The deepest bone that carries (itself or through the bones below it) at least half of the vertices above
        // bandBottom and most of the area they cover seen from above (cells of a grid). That is the head rather than one
        // of its eyes or dense glasses on it, and rather than the spine it hangs from. Hands raised beside the head cover
        // little area, so they do not keep the head from being picked.
        static Transform HeadBone(Transform driver, List<Vector3> points, List<Transform> bones, float bandBottom)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in points)
            {
                if (p.y < bandBottom) continue;
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.z);
                maxZ = Mathf.Max(maxZ, p.z);
            }
            var cell = Mathf.Max(maxX - minX, maxZ - minZ, MinScale) / FootprintCells;
            var carried = new Dictionary<Transform, int>();
            var covered = new Dictionary<Transform, HashSet<int>>();
            var bandCells = new HashSet<int>();
            var band = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p.y < bandBottom) continue;
                band++;
                var index = Mathf.FloorToInt((p.x - minX) / cell) * (FootprintCells + 1) + Mathf.FloorToInt((p.z - minZ) / cell);
                bandCells.Add(index);
                for (var t = bones[i]; t != null && t != driver.parent; t = t.parent)
                {
                    carried[t] = carried.TryGetValue(t, out var n) ? n + 1 : 1;
                    if (!covered.TryGetValue(t, out var cells)) covered[t] = cells = new HashSet<int>();
                    cells.Add(index);
                }
            }
            Transform best = driver;
            var bestDepth = -1;
            foreach (var (bone, count) in carried)
            {
                if (count < band * HeadShare || covered[bone].Count < bandCells.Count * FootprintShare) continue;
                var depth = Depth(bone, driver);
                if (depth <= bestDepth) continue;
                best = bone;
                bestDepth = depth;
            }
            return best;
        }

        static int Depth(Transform t, Transform root)
        {
            var depth = 0;
            for (; t != null && t != root; t = t.parent) depth++;
            return depth;
        }

        // The hat sits at a high percentile of the head's heights rather than at its highest point, so a thin comb or
        // tuft does not lift it off the head. It is centred on the extent of the head's top part rather than on the mean
        // of its vertices, which a detailed face would pull forward, and it is as wide as the head's upper half.
        static void MeasureHead(List<Vector3> points, List<Transform> bones, Transform head, out Vector3 top, out float width)
        {
            var heights = new List<float>();
            for (var i = 0; i < points.Count; i++)
                if (bones[i].IsChildOf(head)) heights.Add(points[i].y);
            heights.Sort();
            var bottom = heights[0];
            var crown = heights[(int)(TopPercentile * (heights.Count - 1))];
            var capBottom = crown - CapShare * (crown - bottom);
            var middle = (crown + bottom) * 0.5f;
            var capX = new List<float>();
            var capZ = new List<float>();
            float left = float.MaxValue, right = float.MinValue;
            for (var i = 0; i < points.Count; i++)
            {
                if (!bones[i].IsChildOf(head)) continue;
                var p = points[i];
                if (p.y >= capBottom)
                {
                    capX.Add(p.x);
                    capZ.Add(p.z);
                }
                if (p.y < middle) continue;
                left = Mathf.Min(left, p.x);
                right = Mathf.Max(right, p.x);
            }
            top = new Vector3(MidExtent(capX), crown, MidExtent(capZ));
            width = right - left;
        }

        // Halfway between the CapEdge percentiles of values (sorted in place).
        static float MidExtent(List<float> values)
        {
            values.Sort();
            var last = values.Count - 1;
            return 0.5f * (values[(int)(CapEdge * last)] + values[(int)((1f - CapEdge) * last)]);
        }

        // The head bone and the bones it hangs from (neck, chest, spine, hips). Arms, legs, tails and wings branch off
        // this chain.
        static HashSet<Transform> Spine(Transform driver, Transform head)
        {
            var spine = new HashSet<Transform>();
            for (var t = head; t != null && t != driver.parent; t = t.parent) spine.Add(t);
            return spine;
        }

        // Body items hang from the back of the neck. Only vertices the spine chain moves count, so the arms reaching for
        // the wheel and any tail stay out of the measure, and a column below the head keeps out the rest of a wide body.
        // The back is a low percentile of the column's depths, so a spike behind it does not move it. The width is the
        // body's in a slab at the back, kept within a range of the head's width, and the slab's most common bone carries
        // them. A driver whose spine moves nothing there measures all its vertices.
        static Anchor BackAnchor(Transform frame, List<Vector3> points, List<Transform> bones, HashSet<Transform> spine, float centre,
            float headWidth, float neck, float band)
        {
            var half = ColumnShare * headWidth;
            bool InBand(int i, bool strict) => Mathf.Abs(points[i].y - neck) <= band && (!strict || spine.Contains(bones[i]));
            var spineOnly = false;
            for (var i = 0; i < points.Count && !spineOnly; i++) spineOnly = InBand(i, true) && Mathf.Abs(points[i].x - centre) <= half;
            var depths = new List<float>();
            for (var i = 0; i < points.Count; i++)
                if (InBand(i, spineOnly) && Mathf.Abs(points[i].x - centre) <= half) depths.Add(points[i].z);
            if (depths.Count == 0) return default;
            depths.Sort();
            var back = depths[(int)(BackPercentile * (depths.Count - 1))];
            var slabFront = back + SlabDepthShare * headWidth;
            float left = float.MaxValue, right = float.MinValue;
            var carriers = new Dictionary<Transform, int>();
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (!InBand(i, spineOnly) || p.z < back || p.z > slabFront) continue;
                left = Mathf.Min(left, p.x);
                right = Mathf.Max(right, p.x);
                if (Mathf.Abs(p.x - centre) <= half) carriers[bones[i]] = carriers.TryGetValue(bones[i], out var n) ? n + 1 : 1;
            }
            Transform carrier = null;
            var most = 0;
            foreach (var (bone, count) in carriers)
            {
                if (count <= most) continue;
                carrier = bone;
                most = count;
            }
            var width = Mathf.Clamp(right - left, MinBodyShare * headWidth, MaxBodyShare * headWidth);
            return ToBone(frame, carrier, new Vector3(centre, neck, back), width);
        }

        static bool TryRendererBounds(Transform frame, Transform driver, out Bounds bounds)
        {
            bounds = default;
            var found = false;
            foreach (var r in driver.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is not (MeshRenderer or SkinnedMeshRenderer)) continue;
                var world = r.bounds;
                for (var c = 0; c < 8; c++)
                {
                    var p = frame.InverseTransformPoint(Core.BoundsUtil.Corner(world, c));
                    if (found) bounds.Encapsulate(p);
                    else bounds = new Bounds(p, Vector3.zero);
                    found = true;
                }
            }
            return found;
        }

        static CosmeticAnchors FromBounds(Transform frame, Transform driver, Bounds b)
        {
            var neck = b.min.y + NeckHeight * b.size.y;
            var half = 0.5f * FallbackBackWidth * b.size.x;
            var head = ToBone(frame, driver, new Vector3(b.center.x, b.max.y, b.center.z), FallbackHeadWidth * b.size.x);
            var back = ToBone(frame, driver, new Vector3(b.center.x, neck, b.min.z), 2f * half);
            var profile = new ClothObstacles.BackProfile(neck, b.min.y, b.center.x - half, b.center.x + half, new[] { b.min.z });
            return new CosmeticAnchors(frame, driver, head, back, profile, b.center);
        }

        static Anchor ToBone(Transform frame, Transform bone, Vector3 framePoint, float width)
        {
            if (bone == null) return default;
            var frameToBone = MeanScale(frame) / Mathf.Max(MinScale, MeanScale(bone));
            return new Anchor(bone, bone.InverseTransformPoint(frame.TransformPoint(framePoint)), Quaternion.Inverse(bone.rotation) * frame.rotation,
                Mathf.Max(0f, width), frameToBone);
        }

        static float MeanScale(Transform t)
        {
            var s = t.lossyScale;
            return (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
        }
    }
}
