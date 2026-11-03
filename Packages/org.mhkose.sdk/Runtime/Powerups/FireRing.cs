using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Powerups
{
    /// <summary>
    /// A ring of fire lit on the ground (Pumpkin's grenade: spawned by <see cref="ExplosiveCharge"/>) that spreads out to
    /// <see cref="radius"/>, burns there for a while and dies down. Every kart its band touches catches
    /// <see cref="effect"/>, once per kart. It follows the ground piece by piece and goes out where it meets a wall, a
    /// drop or steep ground. Simulated in physics steps from its lighting, so every online client spreads it alike; drawn
    /// as a procedural band of flames (first material) over a glowing strip on the ground (second material).
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class FireRing : PowerupBehaviour
    {
        const float StartRadius = 0.3f;
        // Rise between two steps of a piece above which the ground in between counts as a wall (m).
        const float WallClearance = 0.75f;
        // A kart's origin may lie this far below the ground interpolated between two pieces (bumps) and still touch (m).
        const float TouchBelow = 1f;
        // The inner row of flames stands this far inside the outer one (m), shifted by half a flame.
        const float InnerRowInset = 0.35f;
        // Share of the flame height of the inner row.
        const float InnerRowHeight = 0.75f;
        // Seconds the flames take to shoot up when lit.
        const float FlareTime = 0.15f;
        const float FlickerPhasePerPiece = 1.7f;
        const int QuadsPerPiece = 3;

        [Header("Spread")]
        [Tooltip("Radius the ring spreads out to from where it was lit (m). Read by computer drivers as the reach of the " +
                 "item that leaves it.")]
        public float radius = 11f;
        [Tooltip("Seconds the ring takes to spread to its radius: fast at first, slowing down towards the end.")]
        [SerializeField, Min(0.05f)] float spreadTime = 1f;
        [Tooltip("Seconds it keeps burning at its full radius after spreading.")]
        [SerializeField, Min(0f)] float burnTime = 1.5f;
        [Tooltip("Seconds the flames take to die down at the end. They burn nobody any more.")]
        [SerializeField, Min(0.05f)] float fadeTime = 0.5f;
        [Tooltip("Pieces the ring is made of around. Each follows the ground on its own and goes out where it meets a " +
                 "wall, a drop or ground steeper than maxSlope.")]
        [SerializeField, Range(16, 96)] int pieces = 48;
        [Tooltip("Ground steeper than this puts the fire out there (degrees).")]
        [SerializeField, Range(0f, 90f)] float maxSlope = 50f;
        [Tooltip("The ground is searched from this far above the ring's plane (m)...")]
        [SerializeField, Min(0f)] float probeUp = 2f;
        [Tooltip("...down to this far below it. Without ground under where it is lit, no fire is left at all (m).")]
        [SerializeField, Min(0f)] float probeDown = 4f;
        [SerializeField] LayerMask groundMask = ~0;

        [Header("Burn")]
        [Tooltip("Hostile effect given to a kart the band touches (a burn). Each kart meets the fire once: a kart that is " +
                 "protected (shield, ghost, respawn), still reeling from a hit (the blast's) or the user within " +
                 "GameConfig.ownerHazardImmunity of the lighting is spared for good.")]
        [SerializeField] StatusEffectDefinition effect;
        [Tooltip("A kart touches the band when its centre comes this close to the middle of the band (m, along the " +
                 "ground): half the band plus about half a kart.")]
        [SerializeField, Min(0f)] float touchDistance = 1.6f;
        [Tooltip("Karts higher than this above the burning ground are not touched (m).")]
        [SerializeField, Min(0f)] float touchHeight = 2f;

        [Header("Look")]
        [Tooltip("Height of the flames (m). First material: a flame texture, one flame per piece, V upwards; vertex " +
                 "colours fade it.")]
        [SerializeField, Min(0f)] float flameHeight = 1.8f;
        [Tooltip("Share by which each flame is randomly taller or shorter.")]
        [SerializeField, Range(0f, 0.9f)] float flameHeightVariation = 0.35f;
        [Tooltip("Share by which the flame heights flicker.")]
        [SerializeField, Range(0f, 0.5f)] float flicker = 0.12f;
        [Tooltip("Flickers per second.")]
        [SerializeField, Min(0f)] float flickerFrequency = 7f;
        [Tooltip("Width of the glowing strip on the ground (m). Second material: V across the strip; vertex colours fade it.")]
        [SerializeField, Min(0f)] float bandWidth = 1.6f;
        [Tooltip("Height above the ground the fire is drawn at, against z-fighting (m).")]
        [SerializeField, Min(0f)] float lift = 0.05f;
        [Tooltip("One-shot where the fire is lit.")]
        [SerializeField] AudioClip igniteSound;

        readonly HashSet<Kart> touched = new();
        Vector3 centre, up, axisX, axisZ;
        Vector3[] directions, ground, previousGround;
        bool[] lit;
        float[] heightScale;
        float age, spread;
        Mesh mesh;
        Vector3[] vertices;
        Color32[] colors;
        Matrix4x4 toLocal;

        float BurnEnd => spreadTime + burnTime;
        float Lifetime => BurnEnd + fadeTime;

        protected internal override float NetHitReach => spread + touchDistance;

        // Another client's racer caught fire here (or another part's hit was confirmed): it has met the fire.
        protected internal override void OnNetHit(Kart victim) => touched.Add(victim);

        protected override void OnLaunch()
        {
            if (!ItemPhysics.TryGroundBelow(transform.position, probeUp, probeDown, out var hit, groundMask))
            {
                Remove();
                return;
            }
            centre = hit.point;
            up = Vector3.Angle(hit.normal, Vector3.up) <= maxSlope ? hit.normal : Vector3.up;
            transform.SetPositionAndRotation(centre, Quaternion.FromToRotation(Vector3.up, up));
            axisX = transform.right;
            axisZ = transform.forward;
            toLocal = transform.worldToLocalMatrix;
            directions = new Vector3[pieces];
            ground = new Vector3[pieces];
            previousGround = new Vector3[pieces];
            lit = new bool[pieces];
            heightScale = new float[pieces];
            spread = StartRadius;
            for (var i = 0; i < pieces; i++)
            {
                var a = i * Mathf.PI * 2f / pieces;
                directions[i] = axisX * Mathf.Cos(a) + axisZ * Mathf.Sin(a);
                lit[i] = TryGround(centre + directions[i] * spread, out ground[i]);
                heightScale[i] = 1f + flameHeightVariation * (2f * RaceClock.Value01((uint)Usage, i) - 1f);
            }
            System.Array.Copy(ground, previousGround, pieces);
            BuildMesh();
            Draw();
            PlaySfx(igniteSound, centre);
        }

        protected override void Step(float dt)
        {
            age += dt;
            if (age >= Lifetime)
            {
                Remove();
                return;
            }
            System.Array.Copy(ground, previousGround, pieces);
            var t = Mathf.Clamp01(age / spreadTime);
            var target = Mathf.Lerp(StartRadius, radius, 1f - (1f - t) * (1f - t));
            if (target > spread) Spread(target);
            if (age < BurnEnd) TouchKarts();
        }

        // Moves every burning piece out to radius r over the ground; a piece that meets a wall, a drop or steep ground goes out.
        void Spread(float r)
        {
            spread = r;
            for (var i = 0; i < pieces; i++)
            {
                if (!lit[i]) continue;
                if (!TryGround(centre + directions[i] * r, out var point)
                    || ItemPhysics.Linecast(ground[i] + up * WallClearance, point + up * WallClearance, out _, groundMask))
                {
                    lit[i] = false;
                    continue;
                }
                ground[i] = point;
            }
        }

        bool TryGround(Vector3 planar, out Vector3 point)
        {
            point = planar;
            if (!ItemPhysics.TryGroundAlong(planar, up, probeUp, probeDown, out var hit, groundMask)
                || Vector3.Angle(hit.normal, Vector3.up) > maxSlope) return false;
            point = hit.point;
            return true;
        }

        void TouchKarts()
        {
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (!IsTangible(k) || touched.Contains(k) || !Touches(k.transform.position)) continue;
                touched.Add(k);
                // Immunity counts from the lighting: a ring thrown ahead spreads towards its user.
                if ((k == Owner && age < OwnerHazardImmunity) || k.Effects.HasHostileEffect) continue;
                HitKart(k, effect);
            }
        }

        bool Touches(Vector3 p)
        {
            var d = p - centre;
            var flat = d - up * Vector3.Dot(d, up);
            if (Mathf.Abs(flat.magnitude - spread) > touchDistance) return false;
            var turn = Mathf.Atan2(Vector3.Dot(flat, axisZ), Vector3.Dot(flat, axisX)) / (Mathf.PI * 2f);
            var at = Mathf.Repeat(turn * pieces, pieces);
            var i = Mathf.Min((int)at, pieces - 1);
            var j = (i + 1) % pieces;
            if (!lit[i] || !lit[j]) return false;
            var above = Vector3.Dot(p - Vector3.Lerp(ground[i], ground[j], at - i), up);
            return above >= -TouchBelow && above <= touchHeight;
        }

        void BuildMesh()
        {
            var n = pieces * QuadsPerPiece * 4;
            vertices = new Vector3[n];
            colors = new Color32[n];
            var uvs = new Vector2[n];
            // The materials are self-lit; the shader still needs a normal.
            var normals = new Vector3[n];
            System.Array.Fill(normals, Vector3.up);
            var flames = new int[pieces * 2 * 6];
            var band = new int[pieces * 6];
            for (var s = 0; s < pieces; s++)
            {
                SetQuadUvs(uvs, OuterQuad(s), 0f, 1f);
                SetQuadUvs(uvs, InnerQuad(s), 0.5f, 1.5f);
                SetQuadUvs(uvs, BandQuad(s), 0.5f, 0.5f);
                QuadIndices(flames, s * 12, OuterQuad(s));
                QuadIndices(flames, s * 12 + 6, InnerQuad(s));
                QuadIndices(band, s * 6, BandQuad(s));
            }
            mesh = new Mesh { name = "FireRing" };
            mesh.MarkDynamic();
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.colors32 = colors;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(flames, 0, false);
            mesh.SetTriangles(band, 1, false);
            var reach = radius + bandWidth;
            var top = probeUp + flameHeight * (1f + flameHeightVariation + flicker);
            mesh.bounds = new Bounds(new Vector3(0f, (top - probeDown) * 0.5f, 0f), new Vector3(2f * reach, top + probeDown, 2f * reach));
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = GetComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        int OuterQuad(int s) => s * QuadsPerPiece * 4;
        int InnerQuad(int s) => OuterQuad(s) + 4;
        int BandQuad(int s) => OuterQuad(s) + 8;

        // Quad vertices: 0 = start bottom/inner, 1 = end bottom/inner, 2 = start top/outer, 3 = end top/outer; V runs
        // up the flames and across the strip.
        static void SetQuadUvs(Vector2[] uvs, int q, float uStart, float uEnd)
        {
            uvs[q] = new Vector2(uStart, 0f);
            uvs[q + 1] = new Vector2(uEnd, 0f);
            uvs[q + 2] = new Vector2(uStart, 1f);
            uvs[q + 3] = new Vector2(uEnd, 1f);
        }

        static void QuadIndices(int[] tris, int t, int q)
        {
            tris[t] = q; tris[t + 1] = q + 2; tris[t + 2] = q + 1;
            tris[t + 3] = q + 1; tris[t + 4] = q + 2; tris[t + 5] = q + 3;
        }

        // The pieces drawn between their last two steps; flames shoot up when lit and die down at the end.
        void LateUpdate()
        {
            if (mesh != null) Draw();
        }

        void Draw()
        {
            var dt = Time.fixedDeltaTime;
            var share = PoseInterpolation.StepShare;
            var time = Mathf.Max(0f, age - (1f - share) * dt);
            var strength = Mathf.Clamp01(time / FlareTime) * Mathf.Clamp01(1f - (time - BurnEnd) / fadeTime);
            var shade = (byte)(strength * 255f);
            var on = new Color32(shade, shade, shade, shade);
            var off = new Color32(0, 0, 0, 0);
            var clock = Time.time * flickerFrequency * Mathf.PI * 2f;
            var halfBand = bandWidth * 0.5f;
            for (var s = 0; s < pieces; s++)
            {
                var e = (s + 1) % pieces;
                var burning = strength > 0f && lit[s] && lit[e];
                var a = Vector3.LerpUnclamped(previousGround[s], ground[s], share) + up * lift;
                var b = Vector3.LerpUnclamped(previousGround[e], ground[e], share) + up * lift;
                var height = burning ? flameHeight * heightScale[s] * strength * (1f + flicker * Mathf.Sin(clock + s * FlickerPhasePerPiece)) : 0f;
                var color = burning ? on : off;
                SetQuad(OuterQuad(s), a, b, Vector3.up * height, Vector3.up * height, color);
                var inner = height * InnerRowHeight * heightScale[e];
                SetQuad(InnerQuad(s), a - directions[s] * InnerRowInset, b - directions[e] * InnerRowInset, Vector3.up * inner, Vector3.up * inner, color);
                SetQuad(BandQuad(s), a - directions[s] * halfBand, b - directions[e] * halfBand,
                    directions[s] * bandWidth, directions[e] * bandWidth, color);
            }
            mesh.SetVertices(vertices, 0, vertices.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.SetColors(colors, 0, colors.Length, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        void SetQuad(int q, Vector3 start, Vector3 end, Vector3 startRise, Vector3 endRise, Color32 color)
        {
            vertices[q] = toLocal.MultiplyPoint3x4(start);
            vertices[q + 1] = toLocal.MultiplyPoint3x4(end);
            vertices[q + 2] = toLocal.MultiplyPoint3x4(start + startRise);
            vertices[q + 3] = toLocal.MultiplyPoint3x4(end + endRise);
            colors[q] = colors[q + 1] = colors[q + 2] = colors[q + 3] = color;
        }

        void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }
    }
}
