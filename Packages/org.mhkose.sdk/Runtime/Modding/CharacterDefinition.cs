using System.Collections.Generic;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A driver with its model, default kart, special powerup, own cosmetics, animation clips per role and voice lines.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Character", fileName = "Character")]
    public class CharacterDefinition : ContentDefinition
    {
        [Header("Visuals")]
        [Tooltip("Driver model without a kart. Its origin is the seat point: races and the menu place it on the kart's " +
                 "KartModel.seat, shifted by seatOffset.")]
        public GameObject driverPrefab;
        [Tooltip("Uniform scale of the driver model, about its seat point.")]
        public float modelScale = 1f;
        [Tooltip("Square picture of the driver that every menu shows (character cards, race summary, lobby, results, " +
                 "records). Empty = the icon.")]
        public Texture2D portrait;

        /// <summary>The picture menus show for this driver: the portrait, else the icon.</summary>
        public Texture2D Portrait => portrait != null ? portrait : Icon;
        [Tooltip("Cosmetics that are part of the driver, such as its own hat, cut out of its model. It wears them in the " +
                 "slots its player leaves empty, so a hat the player picks replaces its own. Every driver can wear them too.")]
        public List<CosmeticDefinition> ownCosmetics = new();

        /// <summary>The cosmetic of <see cref="ownCosmetics"/> in <paramref name="slot"/>, null for none.</summary>
        public CosmeticDefinition OwnCosmetic(CosmeticSlot slot)
        {
            foreach (var cosmetic in ownCosmetics)
                if (cosmetic != null && cosmetic.slot == slot) return cosmetic;
            return null;
        }

        [Header("Seat")]
        [Tooltip("How far ahead of its seat point (metres, at modelScale) the driver's hands hold the steering wheel in " +
                 "its driving pose. On every kart the driver slides along the seat so its hands reach the kart's wheel " +
                 "(KartModel.SteeringWheel), within a short way. 0 = it stays on the seat point.")]
        public float handReach;
        [Tooltip("Shift of the seated driver in the kart's space (x right, y up, z forward, metres), on top of the slide to " +
                 "the wheel, so it sits on the seat (a big driver whose feet would hang below the floor sits higher).")]
        public Vector3 seatOffset;

        [Header("Without a kart")]
        [Tooltip("The driver is a vehicle itself (the TINcan racers). The kart page offers No kart for it, and No kart is " +
                 "its default when defaultKart is empty.")]
        public bool racesWithoutKart;
        [Tooltip("Without a kart, where the driver's origin sits in the kart's space (metres), so the model stands on the " +
                 "ground as the kart body.")]
        public Vector3 noKartOffset;
        [Tooltip("Without a kart, the uniform scale of the driver model instead of modelScale.")]
        public float noKartScale = 1f;
        [Tooltip("Without a kart, the box (kart space, metres) whose front, top and back carry held items and attached " +
                 "effects, about the driver's body, so a hammer sits in its hands. Size 0 = the kart's chassis box.")]
        public Bounds noKartMounts;

        /// <summary>
        /// Races as its own body on <paramref name="kart"/>: no kart model (No kart or none) and
        /// <see cref="racesWithoutKart"/>, as <see cref="Karts.KartRigBuilder.Spawn"/> builds it.
        /// </summary>
        public bool RacesBareOn(KartDefinition kart) => racesWithoutKart && (kart == null || kart.kartPrefab == null);

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
