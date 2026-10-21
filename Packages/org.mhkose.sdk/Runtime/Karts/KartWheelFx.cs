using MHK.Race;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Tyre effects from KartPhysics wheel contacts: skid marks into the shared SkidMarks pool, the kart's skid intensity
    /// and sound surface, and rear-tyre dust (MHK2 Car update rules).
    /// </summary>
    [RequireComponent(typeof(KartPhysics))]
    public class KartWheelFx : MonoBehaviour
    {
        [Tooltip("Alpha-blended particle material for tyre dust (MHK2 rauch_partikel puff).")]
        [SerializeField] private Material dustMaterial;
        [Tooltip("Alpha-blended, vertex-coloured material for skid marks (its texture's middle row is the strip profile).")]
        [SerializeField] private Material skidMaterial;

        [Header("Skid marks")]
        [Tooltip("Mark width (m). MHK2 tyres: 0.2 m radius, about 0.15 m wide.")]
        [SerializeField] private float markWidth = 0.15f;
        [Tooltip("Mark segments are added every this many metres.")]
        [SerializeField] private float segmentLength = 0.3f;
        [Tooltip("Lift above the ground against z-fighting (m).")]
        [SerializeField] private float markLift = 0.02f;
        [Tooltip("Multiplier on the surface's skid mark opacity ('skidColor' alpha × slide intensity), clamped to 1.")]
        [SerializeField] private float markOpacity = 2f;

        [Header("Dust")]
        [Tooltip("Angle between the dust trail and the kart's backward axis, outward (deg).")]
        [SerializeField] private float dustAngle = 35f;
        [Tooltip("Upward share of the ejection direction.")]
        [SerializeField] private float dustLift = 0.25f;
        [Tooltip("Ejection speed relative to the kart (m/s), added to the share of kart speed the dust loses.")]
        [SerializeField] private float dustEjectSpeed = 1.5f;
        [Tooltip("Random start size range of a dust puff (m).")]
        [SerializeField] private Vector2 dustSize = new(0.35f, 0.75f);
        [Tooltip("Random lifetime range of a dust puff (s).")]
        [SerializeField] private Vector2 dustLifetime = new(0.6f, 1.1f);
        [Tooltip("Per-kart particle budget.")]
        [SerializeField] private int maxDustParticles = 80;
        [Tooltip("No dust for karts farther than this from the camera (m).")]
        [SerializeField] private float dustCullDistance = 90f;
        [Tooltip("Multiplier on the surface's dust opacity ('dustColor' alpha).")]
        [SerializeField] private float dustOpacity = 0.5f;

        struct Trail
        {
            public bool active;
            public Vector3 pos, left, right;
            public Color color;
        }

        const int WheelCount = 4, RearLeftWheel = 2;
        const float DefaultMinSkidEnergy = 10f, DefaultMaxSkidEnergy = 20f;
        const float MinEnergyRange = 0.01f, MinMarkLevel = 0.01f;
        const float TrailBreakDistanceSq = 9f;
        const float DustMinSpeed = 1f;
        const int MaxDustPerWheelPerFrame = 8;
        const float DustOriginLift = 0.12f, DustPositionJitter = 0.1f, DustDirectionJitter = 0.15f;
        const float DustSizeBase = 0.7f, DustSizeIntensity = 0.6f;
        const float DustShadeMin = 0.9f, DustShadeMax = 1.1f;
        const float DustSpeedMin = 0.8f, DustSpeedMax = 1.2f;
        const float DustGravity = -0.02f, DustDragLimit = 1000f, DustDrag = 1.5f;
        const float DustGrowStart = 0.6f, DustGrowEnd = 2.2f, DustSpin = 1.2f;
        const float DustFadeInEnd = 0.12f, DustHalfAlphaTime = 0.5f, DustHalfAlpha = 0.5f;
        static readonly Color DefaultSkid = new(0f, 0f, 0f, 0.5f);

        KartPhysics phys;
        ParticleSystem dust;
        readonly Trail[] trails = new Trail[WheelCount];
        readonly float[] dustDebt = new float[2];

        /// <summary>
        /// Skid intensity of the last frame (0..1): the strongest wheel's skid mark opacity factor, also the volume of the
        /// kart's skid sound (MHK2 CAL_brake).
        /// </summary>
        public float SkidIntensity { get; private set; }
        /// <summary>Surface under most wheels (rear wheels first on a tie), null in the air or off any <see cref="TrackSurface"/>.</summary>
        public TrackSurface SoundSurface { get; private set; }

        void Awake()
        {
            phys = GetComponent<KartPhysics>();
            if (dustMaterial != null) dust = CreateDust();
        }

        void LateUpdate()
        {
            if (phys == null || phys.Body == null || phys.Profile == null) return;
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            var skidEnergy = phys.SkidEnergy;
            UpdateMarks(skidEnergy);
            UpdateDust(skidEnergy, dt);
        }

        ParticleSystem CreateDust()
        {
            var go = new GameObject("TyreDust");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ConfigureDustMain(ps);
            ConfigureDustModules(ps);
            ConfigureDustRenderer(go.GetComponent<ParticleSystemRenderer>());
            ps.Play();
            return ps;
        }

        void ConfigureDustMain(ParticleSystem ps)
        {
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = maxDustParticles;
            main.startLifetime = dustLifetime.y;
            main.gravityModifier = DustGravity;
        }

        static void ConfigureDustModules(ParticleSystem ps)
        {
            var emission = ps.emission;
            emission.enabled = false; // emitted manually per wheel
            var shape = ps.shape;
            shape.enabled = false;
            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.limit = DustDragLimit;
            drag.drag = DustDrag;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, DustGrowStart, 1f, DustGrowEnd));
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-DustSpin, DustSpin);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, DustFadeInEnd), new GradientAlphaKey(DustHalfAlpha, DustHalfAlphaTime), new GradientAlphaKey(0f, 1f) });
            col.color = fade;
        }

        void ConfigureDustRenderer(ParticleSystemRenderer r)
        {
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = dustMaterial;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.None;
            r.maxParticleSize = 1f;
        }

        void UpdateMarks(float skidEnergy)
        {
            var marks = skidMaterial != null ? SkidMarks.Get(skidMaterial) : null;
            var strongest = 0f;
            TrackSurface sound = null;
            var soundCount = 0;
            for (var i = 0; i < WheelCount; i++)
            {
                ref var t = ref trails[i];
                if (!phys.WheelOnGround(i)) { t.active = false; continue; }
                var surface = phys.WheelSurface(i);
                if (surface != null && surface != sound) VoteSoundSurface(i, surface, ref sound, ref soundCount);
                var intensity = WheelSkidIntensity(surface, skidEnergy, out var color);
                strongest = Mathf.Max(strongest, intensity);
                if (marks == null || intensity <= MinMarkLevel || color.a <= MinMarkLevel) { t.active = false; continue; }
                color.a = Mathf.Clamp01(color.a * intensity * markOpacity);
                AdvanceTrail(ref t, i, color, marks);
            }
            SkidIntensity = strongest;
            SoundSurface = sound;
        }

        void VoteSoundSurface(int wheel, TrackSurface surface, ref TrackSurface sound, ref int soundCount)
        {
            var count = 0;
            for (var j = 0; j < WheelCount; j++)
                if (phys.WheelOnGround(j) && phys.WheelSurface(j) == surface) count++;
            // On a tie the rear wheels' surface wins (wheels 2, 3 come last).
            if (count > soundCount || (count == soundCount && wheel >= RearLeftWheel))
            {
                sound = surface;
                soundCount = count;
            }
        }

        static float WheelSkidIntensity(TrackSurface surface, float skidEnergy, out Color color)
        {
            float minE = DefaultMinSkidEnergy, maxE = DefaultMaxSkidEnergy;
            color = DefaultSkid;
            if (surface != null) { minE = surface.minSkidEnergy; maxE = surface.maxSkidEnergy; color = surface.skidColor; }
            return Mathf.Clamp01((skidEnergy - minE) / Mathf.Max(MinEnergyRange, maxE - minE));
        }

        void AdvanceTrail(ref Trail t, int wheel, Color color, SkidMarks marks)
        {
            var n = phys.WheelContactNormal(wheel);
            var p = phys.WheelContactPoint(wheel) + n * markLift;
            if (!t.active || (p - t.pos).sqrMagnitude > TrailBreakDistanceSq)
            {
                // Start a new strip (also after teleports and respawns).
                var side0 = Vector3.Cross(n, transform.forward).normalized * (markWidth * 0.5f);
                SetTrail(ref t, p, p - side0, p + side0, color);
                return;
            }
            var d = p - t.pos;
            if (d.sqrMagnitude < segmentLength * segmentLength) return;
            var side = Vector3.Cross(n, d).normalized * (markWidth * 0.5f);
            var l = p - side;
            var r = p + side;
            marks.Add(t.left, t.right, l, r, t.color, color);
            SetTrail(ref t, p, l, r, color);
        }

        static void SetTrail(ref Trail t, Vector3 p, Vector3 left, Vector3 right, Color color)
        {
            t.active = true;
            t.pos = p;
            t.left = left;
            t.right = right;
            t.color = color;
        }

        void UpdateDust(float skidEnergy, float dt)
        {
            if (dust == null) return;
            var cam = Camera.main;
            if (cam != null && (cam.transform.position - transform.position).sqrMagnitude > dustCullDistance * dustCullDistance)
            {
                dustDebt[0] = dustDebt[1] = 0f;
                return;
            }
            var vel = phys.Body.linearVelocity;
            var speedN = phys.ToOriginalSpeed(vel.magnitude);
            for (var k = 0; k < dustDebt.Length; k++)
            {
                var surface = phys.WheelSurface(RearLeftWheel + k);
                if (surface == null || !surface.dust) { dustDebt[k] = 0f; continue; }
                dustDebt[k] += DustRate(surface, speedN, skidEnergy, out var intensity) * dt;
                if (dustDebt[k] < 1f) continue;
                var n = Mathf.Min(MaxDustPerWheelPerFrame, (int)dustDebt[k]);
                dustDebt[k] -= n;
                EmitDust(k, n, surface, vel, intensity);
            }
        }

        static float DustRate(TrackSurface surface, float speedN, float skidEnergy, out float intensity)
        {
            var speedT = Mathf.Clamp01((speedN - surface.dustSpeedRange.x) / Mathf.Max(MinEnergyRange, surface.dustSpeedRange.y - surface.dustSpeedRange.x));
            var skidT = Mathf.Clamp01((skidEnergy - surface.dustSkidRange.x) / Mathf.Max(MinEnergyRange, surface.dustSkidRange.y - surface.dustSkidRange.x));
            intensity = Mathf.Max(speedT, skidT);
            var rate = surface.dustBaseRate + surface.dustSpeedRate * speedT + surface.dustSkidRate * skidT;
            return speedN < DustMinSpeed ? 0f : rate;
        }

        void EmitDust(int rear, int count, TrackSurface surface, Vector3 vel, float intensity)
        {
            var up = transform.up;
            var outward = rear == 0 ? -transform.right : transform.right;
            var a = dustAngle * Mathf.Deg2Rad;
            var dir = (-transform.forward * Mathf.Cos(a) + outward * Mathf.Sin(a) + up * dustLift).normalized;
            var horizontal = Vector3.ProjectOnPlane(vel, up).magnitude;
            var rel = (1f - Mathf.Clamp01(surface.dustVelocityFactor)) * horizontal + dustEjectSpeed;
            var origin = phys.WheelContactPoint(RearLeftWheel + rear) + up * DustOriginLift;
            var sizeScale = DustSizeBase + DustSizeIntensity * intensity;
            for (var j = 0; j < count; j++) EmitPuff(origin, vel, dir, rel, sizeScale, surface.dustColor);
        }

        void EmitPuff(Vector3 origin, Vector3 vel, Vector3 dir, float rel, float sizeScale, Color c)
        {
            var jitter = Random.insideUnitSphere;
            var shade = Random.Range(DustShadeMin, DustShadeMax);
            var ep = new ParticleSystem.EmitParams
            {
                position = origin + jitter * DustPositionJitter,
                velocity = vel + (dir + jitter * DustDirectionJitter) * (rel * Random.Range(DustSpeedMin, DustSpeedMax)),
                startSize = Random.Range(dustSize.x, dustSize.y) * sizeScale,
                startLifetime = Random.Range(dustLifetime.x, dustLifetime.y),
                startColor = new Color(Mathf.Clamp01(c.r * shade), Mathf.Clamp01(c.g * shade), Mathf.Clamp01(c.b * shade), c.a * dustOpacity),
                rotation = Random.Range(0f, 360f),
            };
            dust.Emit(ep, 1);
        }
    }
}
