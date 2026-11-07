using MHK.Core;
using MHK.Race;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Tyre effects from KartPhysics wheel contacts: skid marks into the shared SkidMarks pool, the kart's skid intensity
    /// and sound surface, rear-tyre dust (MHK2 Car update rules) and, in wet weather, water spray instead of dust from tyres
    /// rolling on uncovered ground (<see cref="TrackWeather"/>). The spray's particle system is made on the first wet frame,
    /// so dry tracks never pay for it.
    /// </summary>
    [RequireComponent(typeof(KartPhysics))]
    public class KartWheelFx : MonoBehaviour
    {
        [Tooltip("Alpha-blended particle material for tyre dust (MHK2 rauch_partikel puff), also drawing the rain spray.")]
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
        [SerializeField] private float dustOpacity = 0.35f;

        [Header("Rain spray")]
        [Tooltip("Colour and opacity of the water tyres throw up on wet uncovered ground.")]
        [SerializeField] private Color sprayColor = new(0.62f, 0.66f, 0.72f, 0.16f);
        [Tooltip("Ground speed (m/s) from which tyres throw spray, and the speed at which they throw the most.")]
        [SerializeField] private Vector2 spraySpeedRange = new(4f, 30f);
        [Tooltip("Spray particles per second of a rear tyre at full speed (front tyres throw a share), on the high graphics tier.")]
        [SerializeField] private float sprayRate = 60f;
        [Tooltip("Random start size range of a spray particle (m).")]
        [SerializeField] private Vector2 spraySize = new(0.1f, 0.22f);
        [Tooltip("Random lifetime range of a spray particle (s).")]
        [SerializeField] private Vector2 sprayLifetime = new(0.25f, 0.5f);
        [Tooltip("Per-kart spray particle budget.")]
        [SerializeField] private int maxSprayParticles = 120;

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
        // Rain spray: front tyres throw this share of the rear tyres' spray (they roll on ground the rear tyres reach
        // next); a slow tyre throws the low-speed share, a fast one the full rate, higher and bigger (rooster tail).
        const float FrontSprayShare = 0.3f, LowSpeedSprayShare = 0.25f;
        const int MaxSprayPerWheelPerFrame = 6;
        // Spray leaves the back of the tyre up and backwards (deg above the kart's backward axis, slightly outward),
        // keeping a share of the kart's velocity, ejected at a base speed plus a share of the ground speed.
        const float SprayAngleLow = 12f, SprayAngleHigh = 28f, SprayOutward = 0.2f;
        const float SprayVelocityKeep = 0.35f, SprayEjectBase = 1.5f, SprayEjectShare = 0.2f;
        const float SprayOriginLift = 0.1f, SprayOriginBack = 0.15f, SprayPositionJitter = 0.08f, SprayDirectionJitter = 0.25f;
        const float SpraySizeBase = 0.7f, SpraySizeSpeed = 0.6f;
        const float SprayShadeMin = 0.92f, SprayShadeMax = 1.05f;
        const float SprayGravity = 0.5f, SprayDrag = 2.5f;
        const float SprayGrowStart = 1f, SprayGrowEnd = 1.8f;
        // Spray drops are drawn stretched along their motion (streaks, not puffs): length per m/s and base length.
        const float SprayStretchVelocity = 0.05f, SprayStretchLength = 1.2f;
        const float SprayFadeInEnd = 0.08f, SprayHalfAlphaTime = 0.4f, SprayHalfAlpha = 0.6f;
        const float MediumTierSpray = 0.6f, LowTierSpray = 0.35f;
        static readonly Color DefaultSkid = new(0f, 0f, 0f, 0.5f);

        KartPhysics phys;
        IKartView view;
        ParticleSystem dust, spray;
        readonly Trail[] trails = new Trail[WheelCount];
        readonly float[] dustDebt = new float[2];
        readonly float[] sprayDebt = new float[WheelCount];

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
            view = GetComponent<IKartView>();
            if (dustMaterial != null) dust = CreateParticles("TyreDust", maxDustParticles, dustLifetime.y, DustGravity, DustDrag,
                DustGrowStart, DustGrowEnd, DustFadeInEnd, DustHalfAlphaTime, DustHalfAlpha);
        }

        void LateUpdate()
        {
            if (phys == null || phys.Body == null || phys.Profile == null) return;
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            var skidEnergy = phys.SkidEnergy;
            UpdateMarks(skidEnergy);
            var near = NearCamera();
            UpdateDust(near, skidEnergy, dt);
            UpdateSpray(near, dt);
        }

        bool NearCamera()
        {
            var cam = Camera.main;
            return cam == null || (cam.transform.position - transform.position).sqrMagnitude <= dustCullDistance * dustCullDistance;
        }

        // A world-space system of puffs emitted by hand: they grow, turn, fade in and out and slow down by drag.
        ParticleSystem CreateParticles(string objectName, int maxParticles, float maxLifetime, float gravity, float drag,
            float growStart, float growEnd, float fadeInEnd, float halfAlphaTime, float halfAlpha)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = maxParticles;
            main.startLifetime = maxLifetime;
            main.gravityModifier = gravity;
            var emission = ps.emission;
            emission.enabled = false; // emitted manually per wheel
            var shape = ps.shape;
            shape.enabled = false;
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = DustDragLimit;
            limit.drag = drag;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, growStart, 1f, growEnd));
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-DustSpin, DustSpin);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, fadeInEnd), new GradientAlphaKey(halfAlpha, halfAlphaTime), new GradientAlphaKey(0f, 1f) });
            col.color = fade;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = dustMaterial;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.None;
            r.maxParticleSize = 1f;
            ps.Play();
            return ps;
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

        void UpdateDust(bool near, float skidEnergy, float dt)
        {
            if (dust == null) return;
            if (!near)
            {
                dustDebt[0] = dustDebt[1] = 0f;
                return;
            }
            var vel = phys.Body.linearVelocity;
            var speedN = phys.ToOriginalSpeed(vel.magnitude);
            for (var k = 0; k < dustDebt.Length; k++)
            {
                var surface = phys.WheelSurface(RearLeftWheel + k);
                // Wet ground raises no dust: the tyre throws spray there instead.
                if (surface == null || !surface.dust || WetUnderWheel(RearLeftWheel + k)) { dustDebt[k] = 0f; continue; }
                dustDebt[k] += DustRate(surface, speedN, skidEnergy, out var intensity) * dt;
                if (dustDebt[k] < 1f) continue;
                var n = Mathf.Min(MaxDustPerWheelPerFrame, (int)dustDebt[k]);
                dustDebt[k] -= n;
                EmitDust(k, n, surface, vel, intensity);
            }
        }

        bool WetUnderWheel(int wheel) => TrackWeather.Wet && TrackWeather.OutdoorGrip(phys.WheelContactPoint(wheel)) < 1f;

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

        // Every tyre rolling on wet uncovered ground throws water, rear tyres most, more and higher the faster the kart
        // goes. None on dry tracks, from hidden karts or far from the camera; less on lower graphics tiers.
        void UpdateSpray(bool near, float dt)
        {
            if (!TrackWeather.Wet || dustMaterial == null || !near || (view != null && view.Hidden))
            {
                System.Array.Clear(sprayDebt, 0, WheelCount);
                return;
            }
            var vel = phys.Body.linearVelocity;
            var up = transform.up;
            var speed = Vector3.ProjectOnPlane(vel, up).magnitude;
            if (speed < spraySpeedRange.x)
            {
                System.Array.Clear(sprayDebt, 0, WheelCount);
                return;
            }
            var speedT = Mathf.Clamp01((speed - spraySpeedRange.x) / Mathf.Max(MinEnergyRange, spraySpeedRange.y - spraySpeedRange.x));
            var rate = sprayRate * Mathf.Lerp(LowSpeedSprayShare, 1f, speedT) * SprayTierShare;
            for (var i = 0; i < WheelCount; i++)
            {
                if (!phys.WheelOnGround(i) || !WetUnderWheel(i)) { sprayDebt[i] = 0f; continue; }
                sprayDebt[i] += rate * (i < RearLeftWheel ? FrontSprayShare : 1f) * dt;
                if (sprayDebt[i] < 1f) continue;
                var n = Mathf.Min(MaxSprayPerWheelPerFrame, (int)sprayDebt[i]);
                sprayDebt[i] -= n;
                if (spray == null) CreateSpray();
                EmitSpray(i, n, vel, speed, speedT);
            }
        }

        void CreateSpray()
        {
            spray = CreateParticles("TyreSpray", maxSprayParticles, sprayLifetime.y, SprayGravity, SprayDrag,
                SprayGrowStart, SprayGrowEnd, SprayFadeInEnd, SprayHalfAlphaTime, SprayHalfAlpha);
            var r = spray.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = SprayStretchVelocity;
            r.lengthScale = SprayStretchLength;
        }

        static float SprayTierShare => GraphicsQuality.Tier switch
        {
            GraphicsPreset.High => 1f,
            GraphicsPreset.Medium => MediumTierSpray,
            _ => LowTierSpray,
        };

        void EmitSpray(int wheel, int count, Vector3 vel, float speed, float speedT)
        {
            var up = transform.up;
            var back = -transform.forward;
            var outward = wheel % 2 == 0 ? -transform.right : transform.right;
            var a = Mathf.Lerp(SprayAngleLow, SprayAngleHigh, speedT) * Mathf.Deg2Rad;
            var dir = (back * Mathf.Cos(a) + up * Mathf.Sin(a) + outward * SprayOutward).normalized;
            var eject = SprayEjectBase + SprayEjectShare * speed;
            var origin = phys.WheelContactPoint(wheel) + up * SprayOriginLift + back * SprayOriginBack;
            var sizeScale = SpraySizeBase + SpraySizeSpeed * speedT;
            for (var j = 0; j < count; j++)
            {
                var jitter = Random.insideUnitSphere;
                var shade = Random.Range(SprayShadeMin, SprayShadeMax);
                var ep = new ParticleSystem.EmitParams
                {
                    position = origin + jitter * SprayPositionJitter,
                    velocity = vel * SprayVelocityKeep + (dir + jitter * SprayDirectionJitter) * eject,
                    startSize = Random.Range(spraySize.x, spraySize.y) * sizeScale,
                    startLifetime = Random.Range(sprayLifetime.x, sprayLifetime.y),
                    startColor = new Color(Mathf.Clamp01(sprayColor.r * shade), Mathf.Clamp01(sprayColor.g * shade), Mathf.Clamp01(sprayColor.b * shade), sprayColor.a),
                    rotation = Random.Range(0f, 360f),
                };
                spray.Emit(ep, 1);
            }
        }
    }
}
