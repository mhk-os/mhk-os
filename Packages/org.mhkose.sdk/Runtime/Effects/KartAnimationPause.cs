using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Effects
{
    /// <summary>
    /// Put on a status effect's <see cref="StatusEffectDefinition.attachedVfxPrefab"/>: while the effect lasts the
    /// driver's and the kart's animations freeze in their current pose (MHK2 x-field). They resume the moment the
    /// effect ends, before an end-of-effect respawn starts the respawn animation.
    /// </summary>
    public class KartAnimationPause : MonoBehaviour
    {
        [Tooltip("Also hold body bob, steering and wheel poses (melting), not just the animation clips.")]
        [SerializeField] bool freezePose;
        Kart kart;
        KartVisual visual;
        StatusEffectDefinition effect;
        bool paused;

        void Awake()
        {
            kart = GetComponentInParent<Kart>();
            if (kart == null) return;
            visual = kart.Visual;
            if (visual == null) return;
            visual.SetAnimationPaused(true, freezePose);
            paused = true;
            kart.Effects.Ended += OnEnded;
        }

        void Start()
        {
            // KartEffects assigns the instance to its ActiveEffect right after instantiating it.
            if (kart == null) return;
            foreach (var e in kart.Effects.Active)
                if (e.Vfx == gameObject) effect = e.Definition;
        }

        void OnEnded(StatusEffectDefinition def)
        {
            if (effect == null || def == effect) Resume();
        }

        void OnDisable() => Resume();
        void OnDestroy() => Resume();

        void Resume()
        {
            if (!paused) return;
            paused = false;
            if (kart != null) kart.Effects.Ended -= OnEnded;
            if (visual != null) visual.SetAnimationPaused(false, freezePose);
        }
    }
}
