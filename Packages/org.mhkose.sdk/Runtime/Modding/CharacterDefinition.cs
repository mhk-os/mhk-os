using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A driver: model, default kart, special powerup, animation clips per role and voice lines.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Character", fileName = "Character")]
    public class CharacterDefinition : ContentDefinition
    {
        [Header("Visuals")]
        [Tooltip("Driver model without a kart. Its origin is the seat point: races and the menu place it on the kart's " +
                 "KartModel.seat without any offset.")]
        public GameObject driverPrefab;
        [Tooltip("Uniform scale of the driver model, about its seat point.")]
        public float modelScale = 1f;
        [Tooltip("Square picture of the driver that every menu shows (character cards, race summary, lobby, results, " +
                 "records). Empty = the icon.")]
        public Texture2D portrait;

        /// <summary>The picture menus show for this driver: the portrait, else the icon.</summary>
        public Texture2D Portrait => portrait != null ? portrait : Icon;

        [Header("Gameplay")]
        [Tooltip("Kart used when the player does not pick one.")]
        public KartDefinition defaultKart;
        [Tooltip("Given by 'special' pickups (the original's special item of Thunder). Empty = random item.")]
        public PowerupDefinition specialPowerup;

        [Header("Animation")]
        public List<CharacterAnimBinding> animations = new();

        [Header("Audio")]
        public List<CharacterVoiceBinding> voices = new();

        // Cycle guard of the fallback chain (the longest real chain has 5 steps).
        const int MaxFallbackDepth = 16;

        /// <summary>The role played instead of <paramref name="role"/> when a character has no clip for it. False for Idle.</summary>
        public static bool TryGetFallback(CharacterAnimRole role, out CharacterAnimRole fallback)
        {
            fallback = role switch
            {
                CharacterAnimRole.Accelerate or CharacterAnimRole.Brake or CharacterAnimRole.Backward
                    or CharacterAnimRole.TurnLeft or CharacterAnimRole.TurnRight
                    or CharacterAnimRole.LookLeft or CharacterAnimRole.LookRight or CharacterAnimRole.Shake => CharacterAnimRole.Drive,
                CharacterAnimRole.Drive or CharacterAnimRole.Respawn or CharacterAnimRole.Start
                    or CharacterAnimRole.Win or CharacterAnimRole.Lose or CharacterAnimRole.EngineOnly => CharacterAnimRole.Idle,
                CharacterAnimRole.BackwardTurnLeft => CharacterAnimRole.TurnLeft,
                CharacterAnimRole.BackwardTurnRight => CharacterAnimRole.TurnRight,
                CharacterAnimRole.ShoutLeft => CharacterAnimRole.LookLeft,
                CharacterAnimRole.ShoutRight => CharacterAnimRole.LookRight,
                CharacterAnimRole.CollideFront or CharacterAnimRole.ShotDown or CharacterAnimRole.JumpLanding
                    or CharacterAnimRole.PowerFail or CharacterAnimRole.Dizzy or CharacterAnimRole.Shocked => CharacterAnimRole.Shake,
                CharacterAnimRole.CollideBack or CharacterAnimRole.CollideLeft or CharacterAnimRole.CollideRight => CharacterAnimRole.CollideFront,
                CharacterAnimRole.Drop or CharacterAnimRole.Burning or CharacterAnimRole.Crushed => CharacterAnimRole.ShotDown,
                CharacterAnimRole.PowerStart => CharacterAnimRole.Accelerate,
                _ => role,
            };
            return fallback != role;
        }

        /// <summary>Resolves a role to a clip following the fallback chain. Returns false if nothing applies.</summary>
        public bool TryResolve(CharacterAnimRole role, out CharacterAnimBinding binding) => TryResolve(animations, role, out binding);

        /// <summary><see cref="TryResolve(CharacterAnimRole, out CharacterAnimBinding)"/> over any binding list (null = none).</summary>
        public static bool TryResolve(IReadOnlyList<CharacterAnimBinding> animations, CharacterAnimRole role, out CharacterAnimBinding binding)
        {
            binding = default;
            if (animations == null) return false;
            var current = role;
            for (var depth = 0; depth < MaxFallbackDepth; depth++)
            {
                for (var i = 0; i < animations.Count; i++)
                {
                    if (animations[i].role != current || animations[i].clip == null) continue;
                    binding = animations[i];
                    return true;
                }
                if (!TryGetFallback(current, out current)) break;
            }
            return false;
        }

        public AudioClip GetVoice(CharacterVoice voice)
        {
            foreach (var v in voices)
            {
                if (v.voice == voice && v.clips != null && v.clips.Length > 0)
                    return v.clips[Random.Range(0, v.clips.Length)];
            }
            return null;
        }
    }
}
