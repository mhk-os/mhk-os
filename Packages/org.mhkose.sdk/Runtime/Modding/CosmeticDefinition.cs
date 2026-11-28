using UnityEngine;

namespace MHK.Modding
{
    /// <summary>Where a cosmetic sits on the driver. A driver wears at most one cosmetic per slot. Keep the numeric values.</summary>
    public enum CosmeticSlot
    {
        /// <summary>On the head (hats, helmets, crowns).</summary>
        Hat = 0,
        /// <summary>On the back, hanging from the shoulders (capes, backpacks).</summary>
        Body = 1,
    }

    /// <summary>
    /// A purely visual item any driver can wear, a hat on the head or a body item on the back, in a colour the player
    /// picks. Cosmetics change nothing in a race and are no lobby content. Online, players see each other's cosmetics
    /// only when they have them, and a mod that adds nothing but cosmetics never keeps anyone from racing together.
    /// The game measures every driver's head and back itself (<see cref="Cosmetics.CosmeticAnchors"/>), so one model fits
    /// every character without per-character settings.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Content/Cosmetic", fileName = "Cosmetic")]
    public class CosmeticDefinition : ContentDefinition
    {
        [Header("Model")]
        public CosmeticSlot slot;
        [Tooltip("The model (+z forward, +y up, metres at fitWidth). Its origin is the point that touches the driver: the " +
                 "bottom centre of a hat sits on top of the head, the front top centre of a body item on the back of the " +
                 "neck. A CosmeticCloth on it makes a cape of cloth.")]
        public GameObject prefab;
        [Tooltip("Shift from that point in the model's space (metres at fitWidth, scaled with the model): 0 sits on top of " +
                 "the head, a negative y sinks a helmet into it, a negative z moves a backpack away from the back.")]
        public Vector3 offset;
        [Tooltip("Width (m) of the head (hats) or the back below the neck (body items) the model is made for: the model is " +
                 "scaled to every driver's own width. 0 = it keeps its size.")]
        public float fitWidth = 0.3f;

        [Header("Colour")]
        [Tooltip("The model takes the colour the player picks (multiplied with the textures and material colours of its " +
                 "MHK/Lit materials, so paint them white). Off = it always looks as made.")]
        public bool tintable = true;
        [Tooltip("Colour until the player picks one.")]
        public Color defaultColor = Color.white;

        [Header("Unlock")]
        [Tooltip("Id of the achievement that unlocks it (e.g. 'core.achievement.first_win'), empty = free from the start. " +
                 "It stays locked while the achievement is neither unlocked nor loaded.")]
        public string unlockAchievement;

        /// <summary>Free from the start (no <see cref="unlockAchievement"/>).</summary>
        public bool UnlockedByDefault => string.IsNullOrEmpty(unlockAchievement);
    }
}
