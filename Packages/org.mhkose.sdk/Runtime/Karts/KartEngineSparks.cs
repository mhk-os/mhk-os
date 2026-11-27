using MHK.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Karts
{
    /// <summary>
    /// Electric sparks of a supercharged engine (<see cref="DifficultySettings.engineSparks"/>). Every 0.2 to 0.6 s,
    /// more often as the engine revs, a crackle of blue-white zaps jumps off the top and sides of the engine part,
    /// followed by a smaller echo, and one or two jagged arcs flicker across the engine or out into the air. On the high
    /// graphics tier near the camera a faint light flickers with them. <see cref="KartEngine"/> adds it to racing karts
    /// only. The sparks live in one small particle system per kart and the arcs in a few line renderers, children of the
    /// engine part, so they follow and scale with it and go when it goes. Karts far from the camera skip them, and so do
    /// hidden or faded karts, so 50 idle karts cost next to nothing.
    /// </summary>
    public class KartEngineSparks : MonoBehaviour
    {
        // Seconds between crackles, from the slow range at idle to the fast range flat out.
        const float SlowMinInterval = 0.35f, SlowMaxInterval = 0.6f, FastMinInterval = 0.2f, FastMaxInterval = 0.35f;
        // Sparks per crackle on the high graphics tier. The medium and low tiers throw a share, never fewer than MinTierBurst.
        const int MinBurst = 5, MaxBurst = 10, MinTierBurst = 3;
        const float MediumTierShare = 0.7f, LowTierShare = 0.45f;
        // The echo follows this many seconds later with half the sparks.
        const float MinEcho = 0.04f, MaxEcho = 0.09f;
        const int MaxParticles = 40;
        // Life (s) of a spark. Its size and speed (per s) are shares of the engine's size, in the engine's own units.
        const float MinLife = 0.08f, MaxLife = 0.18f;
        const float MinSize = 0.1f, MaxSize = 0.17f, EndSize = 0.5f;
        const float MinSpeed = 1.5f, MaxSpeed = 4f;
        const float HalfAlpha = 0.8f;
        // Sparks start on a shell just outside the engine (its bounds times ShellScale) and fly outwards with some scatter.
        const float ShellScale = 1.1f, Scatter = 0.7f;
        // Drawn stretched along their flight, so each spark reads as a short zap.
        const float StretchLength = 2.2f, StretchVelocity = 0.06f;
        // Arcs per crackle (the low tier draws at most one) and how long one flickers (s). An arc ends on the shell again
        // or OutReach engine sizes out into the air, its ArcPoints jitter sideways by up to Jitter engine sizes every frame.
        // Widths are shares of the engine's size, the glowing core drawn over the blue body.
        const int MaxArcs = 2, ArcPoints = 7;
        const float MinArcLife = 0.1f, MaxArcLife = 0.18f, OutChance = 0.4f, MinOutReach = 0.3f, MaxOutReach = 0.6f;
        const float Jitter = 0.08f, BodyWidth = 0.07f, CoreWidth = 0.025f;
        // No sparks farther than SparkDistance (m) from the camera, the light only within LightDistance.
        const float SparkDistance = 45f, LightDistance = 20f;
        // Light reach as a multiple of the engine's size. MinFlicker is the dimmest share of LightIntensity, FlashSeconds one flicker.
        const float LightRangeShare = 2.5f, LightIntensity = 0.7f, MinFlicker = 0.5f, FlashSeconds = 0.06f;
        const float MinOpacity = 0.05f;
        static readonly Color BlueSpark = new(0.55f, 0.75f, 1f, 1f), WhiteSpark = new(0.92f, 0.96f, 1f, 1f);
        // The arc body is drawn alpha-blended in a deep electric blue, so it reads on snow and sky as well as at night.
        static readonly Color ArcBody = new(0.25f, 0.5f, 1f, 1f), ArcCore = new(0.85f, 0.95f, 1f, 1f);

        KartEngine engine;
        IKartView view;
        ParticleSystem system;
        Light flash;
        Bounds bounds;
        float size, nextCrackle, echoAt, flashUntil;
        int echoCount;
        bool lit;
        readonly LineRenderer[] arcBodies = new LineRenderer[MaxArcs], arcCores = new LineRenderer[MaxArcs];
        readonly Vector3[] arcFrom = new Vector3[MaxArcs], arcTo = new Vector3[MaxArcs];
        readonly float[] arcUntil = new float[MaxArcs], arcLife = new float[MaxArcs];
        readonly Vector3[] arcPoints = new Vector3[ArcPoints];
        Material arcMaterial;

        /// <summary>Starts the sparks of <paramref name="engine"/> on the kart <paramref name="view"/> shows.</summary>
        public void Setup(KartEngine engine, IKartView view)
        {
            this.engine = engine;
            this.view = view;
            var config = GameRoot.Instance != null ? GameRoot.Instance.Config : null;
            var material = config != null ? config.engineSparkMaterial : null;
            if (material == null || !TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null)
            {
                Destroy(this);
                return;
            }
            bounds = filter.sharedMesh.bounds;
            size = bounds.size.magnitude;
            system = CreateSystem(material);
            arcMaterial = config.engineArcMaterial != null ? config.engineArcMaterial : material;
            nextCrackle = Time.time + Random.Range(0f, SlowMaxInterval);
        }

        void LateUpdate()
        {
            if (system == null) return;
            if (engine == null)
            {
                Destroy(this);
                return;
            }
            var now = Time.time;
            if (flash != null && flash.enabled && now >= flashUntil) flash.enabled = false;
            UpdateArcs(now);
            if (echoCount > 0 && now >= echoAt)
            {
                Burst(echoCount);
                echoCount = 0;
            }
            if (now < nextCrackle) return;
            var revs = Mathf.Clamp01(engine.Revs);
            nextCrackle = now + Random.Range(Mathf.Lerp(SlowMinInterval, FastMinInterval, revs), Mathf.Lerp(SlowMaxInterval, FastMaxInterval, revs));
            if (!Seen(out var distanceSq)) return;
            lit = GraphicsQuality.Tier == GraphicsPreset.High && distanceSq <= LightDistance * LightDistance;
            var count = Mathf.Max(MinTierBurst, Mathf.RoundToInt(Random.Range(MinBurst, MaxBurst + 1) * TierShare));
            Burst(count);
            var arcs = GraphicsQuality.Tier == GraphicsPreset.Low ? Random.Range(0, 2) : Random.Range(1, MaxArcs + 1);
            for (var i = 0; i < arcs; i++) StartArc(now);
            echoCount = count / 2;
            echoAt = now + Random.Range(MinEcho, MaxEcho);
        }

        // A point on the upper half of the shell around the engine.
        Vector3 ShellPoint(out Vector3 dir)
        {
            dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y);
            return bounds.center + Vector3.Scale(bounds.extents * ShellScale, dir);
        }

        void StartArc(float now)
        {
            var slot = -1;
            for (var i = 0; i < MaxArcs; i++)
                if (arcUntil[i] <= now)
                {
                    slot = i;
                    break;
                }
            if (slot < 0) return;
            if (arcBodies[slot] == null)
            {
                arcBodies[slot] = CreateArc("EngineArc", arcMaterial, 0);
                arcCores[slot] = CreateArc("EngineArcCore", arcMaterial, 1);
            }
            arcFrom[slot] = ShellPoint(out var dir);
            arcTo[slot] = Random.value < OutChance
                ? arcFrom[slot] + (dir + Random.insideUnitSphere * Scatter).normalized * (Random.Range(MinOutReach, MaxOutReach) * size)
                : ShellPoint(out _);
            arcLife[slot] = Random.Range(MinArcLife, MaxArcLife);
            arcUntil[slot] = now + arcLife[slot];
            arcBodies[slot].enabled = arcCores[slot].enabled = true;
        }

        // Active arcs crackle (new jitter every frame) and fade over their life. Ended ones are hidden.
        void UpdateArcs(float now)
        {
            var opacity = view != null ? Mathf.Clamp01(view.Opacity) : 1f;
            for (var i = 0; i < MaxArcs; i++)
            {
                if (arcBodies[i] == null || !arcBodies[i].enabled) continue;
                if (now >= arcUntil[i])
                {
                    arcBodies[i].enabled = arcCores[i].enabled = false;
                    continue;
                }
                var from = arcFrom[i];
                var to = arcTo[i];
                var along = to - from;
                var side = Vector3.Cross(along, Vector3.up);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
                side.Normalize();
                var up = Vector3.Cross(side, along).normalized;
                for (var p = 0; p < ArcPoints; p++)
                {
                    var t = p / (float)(ArcPoints - 1);
                    var bend = p == 0 || p == ArcPoints - 1 ? Vector2.zero : Random.insideUnitCircle * (Jitter * size);
                    // Arcs bow upwards a little, the way a spark jumps over the engine.
                    arcPoints[p] = from + along * t + side * bend.x + up * (bend.y + Mathf.Sin(t * Mathf.PI) * Jitter * size);
                }
                // Full strength with a random flicker, fading only over the last third of the arc's life.
                var alpha = opacity * Mathf.Clamp01((arcUntil[i] - now) / arcLife[i] * 3f) * Random.Range(0.75f, 1f);
                SetArc(arcBodies[i], ArcBody, alpha);
                SetArc(arcCores[i], ArcCore, alpha);
            }
        }

        void SetArc(LineRenderer line, Color color, float alpha)
        {
            line.SetPositions(arcPoints);
            color.a = alpha;
            line.startColor = line.endColor = color;
        }

        LineRenderer CreateArc(string name, Material material, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = ArcPoints;
            line.sharedMaterial = material;
            var scale = transform.lossyScale;
            var width = size * (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f * (order == 0 ? BodyWidth : CoreWidth);
            line.widthMultiplier = width;
            line.numCapVertices = 2;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sortingOrder = order;
            line.enabled = false;
            return line;
        }

        static float TierShare => GraphicsQuality.Tier switch
        {
            GraphicsPreset.High => 1f,
            GraphicsPreset.Medium => MediumTierShare,
            _ => LowTierShare,
        };

        // Drawn and close enough to the camera to be worth sparking.
        bool Seen(out float distanceSq)
        {
            distanceSq = 0f;
            if (view.Hidden || view.Opacity < MinOpacity) return false;
            var cam = Camera.main;
            if (cam == null) return true;
            distanceSq = (cam.transform.position - transform.position).sqrMagnitude;
            return distanceSq <= SparkDistance * SparkDistance;
        }

        // count sparks off the upper half of the shell around the engine (none down into the chassis), as see-through as the kart.
        void Burst(int count)
        {
            var opacity = Mathf.Clamp01(view.Opacity);
            for (var i = 0; i < count; i++)
            {
                var dir = Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y);
                var color = Color.Lerp(BlueSpark, WhiteSpark, Random.value);
                color.a *= opacity;
                system.Emit(new ParticleSystem.EmitParams
                {
                    position = bounds.center + Vector3.Scale(bounds.extents * ShellScale, dir),
                    velocity = (dir + Random.insideUnitSphere * Scatter).normalized * (Random.Range(MinSpeed, MaxSpeed) * size),
                    startSize = Random.Range(MinSize, MaxSize) * size,
                    startLifetime = Random.Range(MinLife, MaxLife),
                    startColor = color,
                }, 1);
            }
            if (lit) Flicker();
        }

        void Flicker()
        {
            if (flash == null)
            {
                var go = new GameObject("EngineSparkLight");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = bounds.center;
                flash = go.AddComponent<Light>();
                flash.type = LightType.Point;
                flash.color = BlueSpark;
                flash.shadows = LightShadows.None;
                var scale = transform.lossyScale;
                flash.range = size * (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f * LightRangeShare;
            }
            flash.intensity = LightIntensity * Random.Range(MinFlicker, 1f);
            flash.enabled = true;
            flashUntil = Time.time + FlashSeconds;
        }

        // A system in the engine's own space (it moves and scales with the engine), emitted by hand. Sparks shrink and
        // fade over their short life.
        ParticleSystem CreateSystem(Material material)
        {
            var go = new GameObject("EngineSparks");
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = MaxParticles;
            main.startLifetime = MaxLife;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, EndSize));
            var colorOverLife = ps.colorOverLifetime;
            colorOverLife.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(HalfAlpha, 0.5f), new GradientAlphaKey(0f, 1f) });
            colorOverLife.color = fade;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.lengthScale = StretchLength;
            r.velocityScale = StretchVelocity;
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.None;
            ps.Play();
            return ps;
        }

        void OnDisable()
        {
            if (flash != null) flash.enabled = false;
        }

        // When this component is removed and the engine part stays, the sparks, arcs and the light go too.
        void OnDestroy()
        {
            if (system != null) Destroy(system.gameObject);
            if (flash != null) Destroy(flash.gameObject);
            for (var i = 0; i < MaxArcs; i++)
            {
                if (arcBodies[i] != null) Destroy(arcBodies[i].gameObject);
                if (arcCores[i] != null) Destroy(arcCores[i].gameObject);
            }
        }
    }
}
