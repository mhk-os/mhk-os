using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// MHK2 black hole (ctor 0x4559a0, update 0x4555b0): a thrown ball that opens a flat hole where it lands. The first
    /// kart within <see cref="triggerRadius"/> gets <see cref="effect"/>, then the hole closes. One victim per hole.
    /// </summary>
    public class BlackHoleTrap : PowerupBehaviour, IPlantedHazard
    {
        const float MinShrinkTime = 0.01f;

        [SerializeField] StatusEffectDefinition effect;
        [Tooltip("Shown while the hole is thrown, hidden when it lands.")]
        [SerializeField] GameObject flightModel;
        [SerializeField] GroundDecal hole;
        [Tooltip("MHK2 'black hole size': radius of the open hole (m).")]
        [SerializeField] float openRadius = 2.5f;
        [Tooltip("Radius growth while opening (m/s).")]
        [SerializeField] float openSpeed = 10f;
        [SerializeField] float triggerRadius = 1.6f;
        [Tooltip("Karts further above / below the hole than this are not caught.")]
        [SerializeField] float triggerHeight = 2f;
        [Tooltip("Seconds from catching a kart until the hole is gone.")]
        [SerializeField] float closeTime = 2f;
        [Tooltip("The hole shrinks to nothing during the last seconds of closeTime.")]
        [SerializeField] float shrinkTime = 0.25f;
        [Tooltip("Optional spin of the flying ball (deg/s). The original does not spin it.")]
        [SerializeField] Vector3 flightSpin;

        [Header("Sounds")]
        [Tooltip("One-shot when the hole opens where it landed (MHK2 PBH_open).")]
        [SerializeField] AudioClip openSound;
        [Tooltip("Loop while the hole lies open, waiting for a victim (MHK2: none).")]
        [SerializeField] Audio.LoopSound idleLoop = new();
        [Tooltip("One-shot when the closing hole starts to shrink, shrinkTime before it is gone (MHK2 PBH_close).")]
        [SerializeField] AudioClip closeSound;

        float radius, closing = -1f;
        bool open, closeSoundPlayed;
        Effects.EffectLoopSound loop;

        public Vector3 Position => transform.position;
        public float DangerRadius => open && closing < 0f ? triggerRadius : 0f;
        public bool LooksLikeItemBox => false;

        protected override void OnLaunch()
        {
            if (flightModel != null) flightModel.SetActive(true);
            if (hole != null) hole.Radius = 0f;
        }

        void OnEnable() => PlantedHazards.Register(this);
        void OnDisable() => PlantedHazards.Unregister(this);

        // The flying ball's spin is visual only: it follows frame time.
        void Update()
        {
            if (!open && InFlight && flightModel != null) flightModel.transform.Rotate(flightSpin * Time.deltaTime, Space.Self);
        }

        protected override void Step(float dt)
        {
            if (!open)
            {
                if (InFlight) return;
                Open();
            }
            if (closing >= 0f)
            {
                TickClosing(dt);
                return;
            }
            radius = Mathf.MoveTowards(radius, openRadius, openSpeed * dt);
            SetRadius(radius);
            TrySwallowNearby();
        }

        protected internal override float NetHitReach => triggerRadius;

        // Another client saw the hole catch a racer: it closes here as well.
        protected internal override void OnNetHit(Kart victim)
        {
            if (!open || closing >= 0f) return;
            closing = 0f;
            Effects.EffectLoopSound.Stop(ref loop);
        }

        void TickClosing(float dt)
        {
            closing += dt;
            var left = closeTime - closing;
            if (left <= shrinkTime)
            {
                if (!closeSoundPlayed)
                {
                    closeSoundPlayed = true;
                    PlaySfx(closeSound, transform.position);
                }
                SetRadius(radius * Mathf.Clamp01(left / Mathf.Max(MinShrinkTime, shrinkTime)));
            }
            if (left <= 0f) Remove();
        }

        void TrySwallowNearby()
        {
            var p = transform.position;
            var up = transform.up;
            var karts = Kart.All;
            for (var i = 0; i < karts.Count; i++)
            {
                var k = karts[i];
                if (OwnerImmune(k) || !IsTangible(k)) continue;
                var d = k.transform.position - p;
                var h = Vector3.Dot(d, up);
                if (Mathf.Abs(h) > triggerHeight) continue;
                if ((d - up * h).sqrMagnitude > triggerRadius * triggerRadius) continue;
                Swallow(k);
                return;
            }
        }

        void Open()
        {
            open = true;
            if (flightModel != null) flightModel.SetActive(false);
            PlaySfx(openSound, transform.position);
            loop = Effects.EffectLoopSound.Play(gameObject, idleLoop);
        }

        void SetRadius(float r)
        {
            if (hole != null) hole.Radius = r;
        }

        void Swallow(Kart k)
        {
            KartReactions.Apply(k, effect, Owner, -1f, Usage);
            closing = 0f;
            Effects.EffectLoopSound.Stop(ref loop);
        }
    }
}
