using System.Collections.Generic;
using MHK.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Powerups
{
    /// <summary>
    /// The earth bulge over a digging kart (<see cref="MolehillPowerup"/>): a dome head and a settling ridge, projected
    /// onto the ground and textured with <see cref="GroundCapture"/>s of the area ahead. The mesh is in world space.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MolehillMound : MonoBehaviour
    {
        const int CapSteps = 3;
        const float CapTipWidth = 0.55f;
        const float MinHalfWidth = 0.05f;
        const float MinTime = 0.01f;

        [Header("Shape")]
        [Tooltip("Vertices across the mound (odd numbers keep a crest vertex).")]
        [SerializeField, Range(5, 21)] int crossVertices = 13;
        [Tooltip("Full width of the head (m), also the width sampled across every section.")]
        [SerializeField] float headWidth = 2.3f;
        [Tooltip("Height of the head (m).")]
        [SerializeField] float headHeight = 0.6f;
        [Tooltip("Length over which the head falls off behind its top (m).")]
        [SerializeField] float headLength = 1.6f;
        [Tooltip("Length of the rounded front of the head (m).")]
        [SerializeField] float capLength = 1f;
        [Tooltip("Width of the ridge left behind (m).")]
        [SerializeField] float ridgeWidth = 1.2f;
        [Tooltip("Height of the ridge left behind (m).")]
        [SerializeField] float ridgeHeight = 0.22f;
        [Tooltip("Random height variation of the ridge (share of its height), for a lumpy look.")]
        [SerializeField, Range(0f, 1f)] float lumpiness = 0.35f;
        [Tooltip("Edges sink this far into the ground so the mound meets it without a visible seam (m).")]
        [SerializeField] float edgeSink = 0.08f;
        [Tooltip("A new section is laid every this many metres of travel.")]
        [SerializeField] float sectionSpacing = 0.45f;

        [Header("Life")]
        [Tooltip("Seconds the ridge stays after a section was laid...")]
        [SerializeField] float settleDelay = 1.6f;
        [Tooltip("...then it sinks back into the ground over this many seconds.")]
        [SerializeField] float settleTime = 1.2f;
        [Tooltip("Seconds for the head to grow when digging starts / to collapse at the end.")]
        [SerializeField] float headGrowTime = 0.3f;
        [Tooltip("Most sections kept. The oldest go first.")]
        [SerializeField] int maxSections = 240;

        [Header("Ground look")]
        [Tooltip("Template of the mound's material (lit MHK/Lit). Its texture is replaced by the ground capture (kept as " +
                 "is, with its colour, where no capture could be made).")]
        [SerializeField] Material groundMaterial;
        [Tooltip("Captured area around the ground points of the area ahead (m).")]
        [SerializeField] float captureMargin = 4f;
        [Tooltip("Capture resolution (m per texel)...")]
        [SerializeField] float captureTexel = 0.04f;
        [Tooltip("...limited to this many texels per side.")]
        [SerializeField] int captureMaxSize = 1024;
        [Tooltip("Geometry higher than this above the highest ground point is left out of the capture (bridges, roofs, tree tops) (m).")]
        [SerializeField] float captureClearance = 2.2f;
        [Tooltip("When the head comes closer than this to the captured area's edge, the area ahead is captured anew (m).")]
        [SerializeField] float recaptureInset = 2.5f;
        [Tooltip("Layers left out of the capture (karts, items, UI).")]
        [SerializeField] LayerMask captureExclude = GameLayers.UIMask | GameLayers.KartMask | GameLayers.ItemMask;
        [Tooltip("Texture coordinates per metre where no capture exists.")]
        [SerializeField] float fallbackUvPerMetre = 0.5f;

        [Header("Ground")]
        [SerializeField] LayerMask groundMask = ~0;
        [Tooltip("Projection rays start this far above the section (m)...")]
        [SerializeField] float probeHeight = 1.2f;
        [Tooltip("...and reach this far below it (m).")]
        [SerializeField] float probeDepth = 2.5f;
        [Tooltip("Lateral vertices whose ground lies further than this off the section's ground plane (kerbs, walls, drops) are kept on the plane (m).")]
        [SerializeField] float maxGroundDeviation = 0.5f;

        struct Section
        {
            public Vector3 center, normal;
            public float time, lump;
            public int groundStart;
            public MoundLook look;
        }

        readonly List<Section> sections = new();
        readonly List<Vector3> groundPoints = new();
        readonly List<MoundRow> live = new();
        readonly List<Vector3> liveGround = new();
        readonly List<MoundLook> looks = new();

        MoundMesh moundMesh;
        MeshRenderer meshRenderer;
        Material rendererMaterial;
        Vector3 headPoint, headNormal, headForward;
        float headScale, headTarget, releaseTime;
        bool active, released;

        MoundLook Current => looks.Count > 0 ? looks[looks.Count - 1] : null;
        /// <summary>The mound's current material (the latest ground capture), also for debris thrown out of the ground.</summary>
        public Material Material => Current != null && Current.material != null ? Current.material : groundMaterial;
        /// <summary>True once released and fully sunk back into the ground.</summary>
        public bool Finished => released && headScale <= 0f && sections.Count == 0;
        /// <summary>Extent (m, forward and across) of the ground points one capture takes at full resolution.</summary>
        public float CaptureReach => Mathf.Max(1f, captureMaxSize * captureTexel - 2f * captureMargin);
        /// <summary>True when the head point has come within <see cref="recaptureInset"/> of the current capture's edge.</summary>
        public bool NeedsCapture(Vector3 groundPoint) =>
            active && !released && Current?.capture != null && !Current.capture.Contains(groundPoint, recaptureInset);

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            rendererMaterial = meshRenderer.sharedMaterial;
            meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            moundMesh = new MoundMesh("Molehill");
            GetComponent<MeshFilter>().sharedMesh = moundMesh.Mesh;
            meshRenderer.enabled = false;
        }

        void OnDestroy()
        {
            moundMesh?.Destroy();
            foreach (var look in looks) look.Release();
            looks.Clear();
        }

        /// <summary>
        /// Starts the mound at a ground point and grows the head. <paramref name="area"/> are ground points of the area
        /// the dig can reach next, captured in a box aligned with <paramref name="axis"/> (see <see cref="Capture"/>).
        /// </summary>
        public void Begin(Vector3 groundPoint, Vector3 normal, Vector3 forward, IReadOnlyList<Vector3> area, Vector3 axis)
        {
            transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            transform.localScale = Vector3.one;
            active = true;
            released = false;
            headTarget = 1f;
            headPoint = groundPoint;
            headNormal = normal;
            headForward = forward;
            Capture(area, axis);
            AddSection(groundPoint, normal, forward);
            meshRenderer.enabled = true;
        }

        /// <summary>
        /// Captures the ground under <paramref name="area"/> (plus <see cref="captureMargin"/>) in a box aligned with the
        /// horizontal direction <paramref name="axis"/>. The head and the sections laid from now on use it.
        /// </summary>
        public void Capture(IReadOnlyList<Vector3> area, Vector3 axis)
        {
            var look = new MoundLook();
            if (groundMaterial != null)
            {
                look.capture = GroundCapture.Render(area, axis, captureMargin, captureTexel, captureMaxSize, ~captureExclude.value,
                    captureClearance, groundMaterial.color);
                look.material = new Material(groundMaterial) { name = "Molehill (ground)" };
                if (look.capture != null)
                {
                    // The capture is the ground's surface colour: no extra tint.
                    look.material.mainTexture = look.capture.Texture;
                    look.material.color = Color.white;
                }
            }
            looks.Add(look);
        }

        /// <summary>Moves the head to a new ground point, laying ridge sections along the way.</summary>
        public void MoveHead(Vector3 groundPoint, Vector3 normal, Vector3 forward)
        {
            if (!active) return;
            headPoint = groundPoint;
            headNormal = normal;
            headForward = forward;
            var last = sections.Count > 0 ? sections[sections.Count - 1].center : groundPoint;
            if (Vector3.Distance(last, groundPoint) >= sectionSpacing) AddSection(groundPoint, normal, forward);
        }

        /// <summary>The digger has surfaced: the head collapses, the ridge settles, then the mound is finished.</summary>
        public void Release()
        {
            if (!active || released) return;
            released = true;
            releaseTime = Time.time;
            headTarget = 0f;
        }

        void AddSection(Vector3 center, Vector3 normal, Vector3 forward)
        {
            if (sections.Count >= maxSections) RemoveOldest();
            var right = Vector3.Cross(normal, forward).normalized;
            if (right.sqrMagnitude < 0.5f) right = Vector3.Cross(Vector3.up, forward).normalized;
            sections.Add(new Section
            {
                center = center,
                normal = normal,
                time = Time.time,
                lump = 1f + (Random.value * 2f - 1f) * lumpiness,
                groundStart = groundPoints.Count,
                look = Current,
            });
            ProjectCross(center, normal, right, groundPoints);
        }

        void RemoveOldest()
        {
            sections.RemoveAt(0);
            var n = crossVertices;
            groundPoints.RemoveRange(0, n);
            for (var i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                s.groundStart -= n;
                sections[i] = s;
            }
        }

        void PruneLooks()
        {
            for (var i = looks.Count - 2; i >= 0; i--)
            {
                var look = looks[i];
                var used = false;
                for (var s = 0; s < sections.Count && !used; s++) used = sections[s].look == look;
                if (used) continue;
                look.Release();
                looks.RemoveAt(i);
            }
        }

        bool TryGround(Vector3 planar, Vector3 normal, out Vector3 ground)
        {
            ground = planar;
            if (!ItemPhysics.TryGroundAlong(planar, normal, probeHeight, probeDepth, out var hit, groundMask)
                || Mathf.Abs(Vector3.Dot(hit.point - planar, normal)) > maxGroundDeviation) return false;
            ground = hit.point;
            return true;
        }

        void ProjectCross(Vector3 center, Vector3 normal, Vector3 right, List<Vector3> into)
        {
            var n = crossVertices;
            for (var i = 0; i < n; i++)
            {
                TryGround(center + right * ((i / (float)(n - 1) - 0.5f) * headWidth), normal, out var p);
                into.Add(p);
            }
        }

        void LateUpdate()
        {
            if (!active) return;
            headScale = Mathf.MoveTowards(headScale, headTarget, Time.deltaTime / Mathf.Max(MinTime, headGrowTime));
            var now = Time.time;
            while (sections.Count > 0 && (released || sections.Count > 1) && now - sections[0].time > settleDelay + settleTime)
                RemoveOldest();
            PruneLooks();
            if (released && headScale <= 0f && sections.Count == 0)
            {
                active = false;
                meshRenderer.enabled = false;
                return;
            }
            Rebuild(now);
        }

        float Settle(float age) => 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((age - settleDelay) / Mathf.Max(MinTime, settleTime)));

        void Rebuild(float now)
        {
            // After surfacing the head's own ridge settles like the rest.
            var headLump = released ? Settle(now - releaseTime) : 1f;
            BuildLiveRows(now, headLump);
            ComputeGrid(headLump);
            moundMesh.Build(live, liveGround, crossVertices, looks, fallbackUvPerMetre);
            moundMesh.SyncMaterials(meshRenderer, looks, rendererMaterial);
            meshRenderer.enabled = true;
        }

        // Front to back: rounded front cap (projected each frame), the head itself, then the laid sections.
        void BuildLiveRows(float now, float headLump)
        {
            live.Clear();
            liveGround.Clear();
            var right = Vector3.Cross(headNormal, headForward).normalized;
            for (var c = CapSteps; c >= 1; c--)
            {
                TryGround(headPoint + headForward * (capLength * c / CapSteps), headNormal, out var center);
                AddLive(center, headNormal, right, -capLength * c / CapSteps, 1f);
            }
            AddLive(headPoint, headNormal, right, 0f, headLump);
            var behind = 0f;
            var prev = headPoint;
            for (var i = sections.Count - 1; i >= 0; i--)
            {
                var s = sections[i];
                behind += Vector3.Distance(prev, s.center);
                prev = s.center;
                live.Add(new MoundRow { normal = s.normal, behind = behind, lump = s.lump * Settle(now - s.time), groundStart = liveGround.Count, look = s.look });
                for (var v = 0; v < crossVertices; v++) liveGround.Add(groundPoints[s.groundStart + v]);
            }
        }

        void AddLive(Vector3 center, Vector3 normal, Vector3 right, float behind, float lump)
        {
            live.Add(new MoundRow { normal = normal, behind = behind, lump = lump, groundStart = liveGround.Count, look = Current });
            ProjectCross(center, normal, right, liveGround);
        }

        void ComputeGrid(float headLump)
        {
            var n = crossVertices;
            var grid = moundMesh.Grid(live.Count * n);
            var total = live[live.Count - 1].behind;
            var top = Mathf.Lerp(ridgeHeight * headLump, headHeight, headScale);
            for (var si = 0; si < live.Count; si++)
            {
                var s = live[si];
                float height, width;
                if (s.behind < 0f) CapProfile(s.behind, top, out height, out width);
                else RidgeProfile(s, total, out height, out width);
                var half = Mathf.Max(MinHalfWidth, width * 0.5f);
                for (var v = 0; v < n; v++)
                {
                    var x = (v / (float)(n - 1) - 0.5f) * headWidth;
                    var u = Mathf.Abs(x) / half;
                    var bump = u < 1f ? (1f - u * u) * (1f - u * u) : 0f;
                    var h = height * bump - edgeSink * (1f - bump);
                    grid[si * n + v] = liveGround[s.groundStart + v] + s.normal * h;
                }
            }
        }

        // Quarter-ellipse falling off from the head's top.
        void CapProfile(float behind, float top, out float height, out float width)
        {
            var u = Mathf.Clamp01(-behind / Mathf.Max(MinTime, capLength));
            height = top * Mathf.Sqrt(Mathf.Max(0f, 1f - u * u));
            width = Mathf.Lerp(ridgeWidth, headWidth, headScale) * Mathf.Lerp(1f, CapTipWidth, u * u);
        }

        // The head falls off behind its top, and the ridge tapers out over the last metre where digging started.
        void RidgeProfile(MoundRow s, float total, out float height, out float width)
        {
            var k = s.behind / Mathf.Max(MinTime, headLength);
            var head = Mathf.Exp(-k * k) * headScale;
            var tail = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(total - s.behind));
            var r = ridgeHeight * s.lump * tail;
            height = Mathf.Lerp(r, headHeight, head);
            width = Mathf.Lerp(ridgeWidth, headWidth, head);
        }
    }
}
