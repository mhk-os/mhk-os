using UnityEngine;

namespace MHK.Modding
{
    /// <summary>An item: what it spawns when used, how it is held and how often item boxes give it.</summary>
    [CreateAssetMenu(menuName = "MHK/Content/Powerup", fileName = "Powerup")]
    public class PowerupDefinition : ContentDefinition
    {
        [Tooltip("Spawned when the powerup is used. Must carry a PowerupBehaviour component.")]
        public GameObject usePrefab;
        [Tooltip("Shown on the kart while the item is held, including between charges (optional).")]
        public GameObject heldPrefab;
        [Tooltip("Where heldPrefab is attached, its own local position is added. Use-prefabs that animate the held " +
                 "item (hammer swing, spike-strip drop) start from the same point.")]
        public HeldMount heldMount = HeldMount.Top;
        [Min(1)] public int charges = 1;
        [Tooltip("Seconds after a use before the next charge of this item can be used (0 = none). Pressing fire " +
                 "earlier does nothing, the HUD shows the item dimmed meanwhile.")]
        [Min(0)] public float useCooldown;
        [Tooltip("Excluded from random item boxes (still usable by scripts/mods).")]
        public bool excludeFromItemBoxes;
        [Tooltip("Relative pick weight depending on race position: x = 0 (leader) .. 1 (last place).")]
        public AnimationCurve weightByPosition = AnimationCurve.Constant(0, 1, 1);
        public PowerupAiUsage aiUsage = PowerupAiUsage.OffensiveForward;
        public AudioClip useSound;
        [Tooltip("Gameplay family (e.g. 'hole', 'homing', 'boost'). 'decoy' marks fake item boxes, which computer drivers may see through.")]
        public string family;

        /// <summary>Item box pick weight at race position <paramref name="positionFraction"/> (0 = leader, 1 = last).</summary>
        public float WeightFor(float positionFraction) =>
            excludeFromItemBoxes ? 0f : Mathf.Max(0f, weightByPosition.Evaluate(Mathf.Clamp01(positionFraction)));
    }
}
