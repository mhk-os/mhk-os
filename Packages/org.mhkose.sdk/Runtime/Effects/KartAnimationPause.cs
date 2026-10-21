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
        Kart kart;
        CharacterAnimator[] animators;
        StatusEffectDefinition effect;
        bool paused;

        void Awake()
        {
            kart = GetComponentInParent<Kart>();
            if (kart == null) return;
            animators = kart.GetComponentsInChildren<CharacterAnimator>();
            foreach (var a in animators) a.SetPaused(true);
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

        void OnDestroy() => Resume();

        void Resume()
        {
            if (!paused) return;
            paused = false;
            if (kart != null) kart.Effects.Ended -= OnEnded;
            foreach (var a in animators)
                if (a != null) a.SetPaused(false);
        }
    }
}
