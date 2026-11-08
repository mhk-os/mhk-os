using MHK.Core;
using MHK.Race;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Tyre effects from KartPhysics wheel contacts: skid marks into the shared SkidMarks pool, the kart's skid intensity
    /// and sound surface, rear-tyre dust (MHK2 Car update rules) and, in wet weather, water spray instead of dust from tyres
    /// rolling on uncovered ground (<see cref="TrackWeather"/>): drops thrown off the tread in arcs (a rooster tail at speed)
    /// and a mist cloud that hangs behind the kart, both lit by the scene and the headlights (MHK/Particle). The spray's
    /// particle systems are made on the first wet frame, so dry tracks never pay for them.
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
        [SerializeField] private float dustOpacity = 0.35f;

        [Header("Rain spray")]
        [Tooltip("Lit particle material (MHK/Particle) of the spray mist: a 2 × 2 sheet of soft puffs.")]
        [SerializeField] private Material sprayMistMaterial;
        [Tooltip("Lit particle material (MHK/Particle) of the spray drops, drawn stretched along their flight.")]
        [SerializeField] private Material sprayDropMaterial;
        [Tooltip("Colour and opacity of the spray mist (lit by the scene).")]
        [SerializeField] private Color mistColor = new(0.85f, 0.88f, 0.92f, 0.32f);
        [Tooltip("Colour and opacity of the spray drops (lit by the scene).")]
        [SerializeField] private Color dropColor = new(0.88f, 0.92f, 0.97f, 0.8f);
        [Tooltip("Ground speed (m/s) from which tyres throw spray, and the speed at which they throw the most.")]
        [SerializeField] private Vector2 spraySpeedRange = new(4f, 30f);
        [Tooltip("Mist puffs per second of a rear tyre at full speed (front tyres throw a share), on the high graphics tier.")]
        [SerializeField] private float mistRate = 28f;
        [Tooltip("Drops per second of a rear tyre at full speed (front tyres throw a share), on the high graphics tier.")]
        [SerializeField] private float dropRate = 70f;
        [Tooltip("Random start size range of a mist puff (m), growing with speed and over its life.")]
        [SerializeField] private Vector2 mistSize = new(0.5f, 0.9f);
        [Tooltip("Random size range of a drop (m) across; its streak is longer.")]
        [SerializeField] private Vector2 dropSize = new(0.03f, 0.06f);
        [Tooltip("Random lifetime range of a mist puff (s).")]
        [SerializeField] private Vector2 mistLifetime = new(0.4f, 0.75f);
        [Tooltip("Random lifetime range of a drop (s).")]
        [SerializeField] private Vector2 dropLifetime = new(0.2f, 0.4f);
        [Tooltip("Per-kart mist and drop particle budgets.")]
        [SerializeField] private int maxMistParticles = 80, maxDropParticles = 100;

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
        const float FrontSprayShare = 0.3f, LowSpeedSprayShare = 0.2f;
        const int MaxSprayPerWheelPerFrame = 8;
        // Mist rises off the back of the tyre, a little up and outward, carried along with half the kart's velocity and
        // braked hard by the air, so it hangs and spreads behind the kart; it grows to a cloud as it fades.
        const float MistAngle = 12f, MistOutward = 0.35f, MistVelocityKeep = 0.55f, MistEjectBase = 1.5f, MistEjectShare = 0.1f;
        const float MistOriginLift = 0.15f, MistOriginBack = 0.25f, MistPositionJitter = 0.15f, MistDirectionJitter = 0.9f;
        const float MistSizeBase = 0.6f, MistSizeSpeed = 0.9f;
        const float MistGravity = -0.03f, MistDrag = 2.2f, MistGrowStart = 1f, MistGrowEnd = 3.5f;
        const float MistFadeInEnd = 0.12f, MistHalfAlphaTime = 0.45f, MistHalfAlpha = 0.6f;
        // Drops leave the back of the tread up and backwards (deg above the kart's backward axis, slightly outward), faster
        // and steeper the faster the kart goes, and fall in arcs under full gravity.
        const float DropAngleLow = 25f, DropAngleHigh = 50f, DropOutward = 0.25f, DropVelocityKeep = 0.45f;
        const float DropEjectBase = 1f, DropEjectShare = 0.22f, DropEjectMin = 0.6f, DropEjectMax = 1.1f;
        const float DropOriginLift = 0.12f, DropOriginBack = 0.18f, DropPositionJitter = 0.06f, DropDirectionJitter = 0.3f;
        const float DropGravity = 1f, DropDrag = 0.6f, DropGrowStart = 1f, DropGrowEnd = 0.7f;
        const float DropFadeInEnd = 0.05f, DropHalfAlphaTime = 0.6f, DropHalfAlpha = 0.8f;
        // Drops are drawn stretched along their motion (streaks): length in drop sizes plus per m/s.
        const float DropStretchLength = 2f, DropStretchVelocity = 0.02f;
        const float SprayShadeMin = 0.93f, SprayShadeMax = 1.05f;
        const int MistSheetTiles = 2;
        const float MediumTierSpray = 0.6f, LowTierSpray = 0.35f;
        static readonly Color DefaultSkid = new(0f, 0f, 0f, 0.5f);

        KartPhysics phys;
        IKartView view;
        ParticleSystem dust, mist, drops;
        readonly Trail[] trails = new Trail[WheelCount];
        readonly float[] dustDebt = new float[2];
        readonly float[] mistDebt = new float[WheelCount];
        readonly float[] dropDebt = new float[WheelCount];

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
            if (dustMaterial != null) dust = CreateParticles("TyreDust", dustMaterial, maxDustParticles, dustLifetime.y, DustGravity, DustDrag,
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

        // A world-space system of puffs drawn with material, emitted by hand: they grow, turn, fade in and out and slow
        // down by drag.
        ParticleSystem CreateParticles(string objectName, Material material, int maxParticles, float maxLifetime, float gravity, float drag,
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
            r.sharedMaterial = material;
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

        // Every tyre rolling on wet uncovered ground throws drops and mist, rear tyres most, more, higher and bigger the
        // faster the kart goes. None on dry tracks, from hidden karts or far from the camera; less on lower graphics tiers.
        void UpdateSpray(bool near, float dt)
        {
            if (!TrackWeather.Wet || sprayMistMaterial == null || sprayDropMaterial == null || !near || (view != null && view.Hidden))
            {
                ClearSprayDebts();
                return;
            }
            var vel = phys.Body.linearVelocity;
            var up = transform.up;
            var speed = Vector3.ProjectOnPlane(vel, up).magnitude;
            if (speed < spraySpeedRange.x)
            {
                ClearSprayDebts();
                return;
            }
            var speedT = Mathf.Clamp01((speed - spraySpeedRange.x) / Mathf.Max(MinEnergyRange, spraySpeedRange.y - spraySpeedRange.x));
            var share = Mathf.Lerp(LowSpeedSprayShare, 1f, speedT) * SprayTierShare * dt;
            for (var i = 0; i < WheelCount; i++)
            {
                if (!phys.WheelOnGround(i) || !WetUnderWheel(i))
                {
                    mistDebt[i] = dropDebt[i] = 0f;
                    continue;
                }
                var wheelShare = share * (i < RearLeftWheel ? FrontSprayShare : 1f);
                if (mist == null) CreateSpray();
                var n = Due(ref dropDebt[i], dropRate * wheelShare);
                if (n > 0) EmitDrops(i, n, vel, speed, speedT);
                n = Due(ref mistDebt[i], mistRate * wheelShare);
                if (n > 0) EmitMist(i, n, vel, speed, speedT);
            }
        }

        // Adds count to debt and takes the whole particles due now (at most MaxSprayPerWheelPerFrame).
        static int Due(ref float debt, float count)
        {
            debt += count;
            if (debt < 1f) return 0;
            var n = Mathf.Min(MaxSprayPerWheelPerFrame, (int)debt);
            debt -= n;
            return n;
        }

        void ClearSprayDebts()
        {
            System.Array.Clear(mistDebt, 0, WheelCount);
            System.Array.Clear(dropDebt, 0, WheelCount);
        }

        void CreateSpray()
        {
            mist = CreateParticles("TyreSprayMist", sprayMistMaterial, maxMistParticles, mistLifetime.y, MistGravity, MistDrag,
                MistGrowStart, MistGrowEnd, MistFadeInEnd, MistHalfAlphaTime, MistHalfAlpha);
            // Each puff shows one of the sheet's puffs, picked at random, for its whole life.
            var sheet = mist.textureSheetAnimation;
            sheet.enabled = true;
            sheet.numTilesX = MistSheetTiles;
            sheet.numTilesY = MistSheetTiles;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f, 0.999f);
            drops = CreateParticles("TyreSprayDrops", sprayDropMaterial, maxDropParticles, dropLifetime.y, DropGravity, DropDrag,
                DropGrowStart, DropGrowEnd, DropFadeInEnd, DropHalfAlphaTime, DropHalfAlpha);
            var rot = drops.rotationOverLifetime;
            rot.enabled = false;
            var r = drops.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.lengthScale = DropStretchLength;
            r.velocityScale = DropStretchVelocity;
        }

        static float SprayTierShare => GraphicsQuality.Tier switch
        {
            GraphicsPreset.High => 1f,
            GraphicsPreset.Medium => MediumTierSpray,
            _ => LowTierSpray,
        };

        // Spray direction of wheel: angle (deg) above the kart's backward axis, leaning outward by outwardShare.
        Vector3 SprayDirection(int wheel, float angle, float outwardShare)
        {
            var outward = wheel % 2 == 0 ? -transform.right : transform.right;
            var a = angle * Mathf.Deg2Rad;
            return (-transform.forward * Mathf.Cos(a) + transform.up * Mathf.Sin(a) + outward * outwardShare).normalized;
        }

        static Color Shaded(Color c)
        {
            var shade = Random.Range(SprayShadeMin, SprayShadeMax);
            return new Color(Mathf.Clamp01(c.r * shade), Mathf.Clamp01(c.g * shade), Mathf.Clamp01(c.b * shade), c.a);
        }

        void EmitDrops(int wheel, int count, Vector3 vel, float speed, float speedT)
        {
            var dir = SprayDirection(wheel, Mathf.Lerp(DropAngleLow, DropAngleHigh, speedT), DropOutward);
            var eject = DropEjectBase + DropEjectShare * speed;
            var origin = phys.WheelContactPoint(wheel) + transform.up * DropOriginLift - transform.forward * DropOriginBack;
            for (var j = 0; j < count; j++)
            {
                var jitter = Random.insideUnitSphere;
                drops.Emit(new ParticleSystem.EmitParams
                {
                    position = origin + jitter * DropPositionJitter,
                    velocity = vel * DropVelocityKeep + (dir + jitter * DropDirectionJitter) * (eject * Random.Range(DropEjectMin, DropEjectMax)),
                    startSize = Random.Range(dropSize.x, dropSize.y),
                    startLifetime = Random.Range(dropLifetime.x, dropLifetime.y),
                    startColor = Shaded(dropColor),
                }, 1);
            }
        }

        void EmitMist(int wheel, int count, Vector3 vel, float speed, float speedT)
        {
            var dir = SprayDirection(wheel, MistAngle, MistOutward);
            var eject = MistEjectBase + MistEjectShare * speed;
            var origin = phys.WheelContactPoint(wheel) + transform.up * MistOriginLift - transform.forward * MistOriginBack;
            var sizeScale = MistSizeBase + MistSizeSpeed * speedT;
            for (var j = 0; j < count; j++)
            {
                var jitter = Random.insideUnitSphere;
                mist.Emit(new ParticleSystem.EmitParams
                {
                    position = origin + jitter * MistPositionJitter,
                    velocity = vel * MistVelocityKeep + (dir + jitter * MistDirectionJitter) * eject,
                    startSize = Random.Range(mistSize.x, mistSize.y) * sizeScale,
                    startLifetime = Random.Range(mistLifetime.x, mistLifetime.y),
                    startColor = Shaded(mistColor),
                    rotation = Random.Range(0f, 360f),
                }, 1);
            }
        }
    }
}
