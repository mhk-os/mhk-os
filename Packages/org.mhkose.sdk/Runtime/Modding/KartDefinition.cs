using System.Collections.Generic;
using MHK.Karts;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>A kart: body model, physics profile, body animations, menu ratings and engine sound.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Kart", fileName = "Kart")]
    public class KartDefinition : ContentDefinition
    {
        [Tooltip("Visual kart body. Should carry a KartModel component marking wheels and the seat.")]
        public GameObject kartPrefab;
        [Tooltip("Vehicle physics. Empty = GameConfig default (MHK2 reference kart).")]
        public KartPhysicsProfile physics;
        public float modelScale = 1f;
        [Tooltip("Version of what decides lap times with this kart (physics profile, scale, chassis). Bump it when a change " +
                 "alters lap times for identical inputs: older records with the kart no longer count.")]
        [Min(1)] public int contentVersion = 1;
        [Tooltip("Kart body animations played in sync with the driver's roles (MHK3 / Extra karts).")]
        public List<CharacterAnimBinding> animations = new();

        [Header("Menu stats (0..1, display only)")]
        [Range(0, 1)] public float speedRating = 0.5f;
        [Range(0, 1)] public float accelerationRating = 0.5f;
        [Range(0, 1)] public float handlingRating = 0.5f;

        [Header("Audio")]
        public AudioClip engineLoop;
        [Tooltip("Pitch of the engine loop at idle and at maximum rpm.")]
        public Vector2 enginePitchRange = new(0.6f, 1.6f);
    }
}
