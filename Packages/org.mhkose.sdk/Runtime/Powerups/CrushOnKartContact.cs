using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Makes the kart it is attached to crushable: another kart touching it applies <see cref="crushEffect"/> to this
    /// kart (MHK3 shrunk karts get squashed). Put it on a status effect's attachedVfxPrefab so it lives with the effect.
    /// </summary>
    public class CrushOnKartContact : MonoBehaviour
    {
        [SerializeField] StatusEffectDefinition crushEffect;
        [Tooltip("Only contacts at least this fast (relative, original m/s × the kart's SpeedScale) crush.")]
        [SerializeField] float minRelativeSpeed = 0.5f;

        Kart kart;

        void OnEnable()
        {
            kart = GetComponentInParent<Kart>();
            if (kart != null) kart.Collided += OnCollided;
        }

        void OnDisable()
        {
            if (kart != null) kart.Collided -= OnCollided;
        }

        void OnCollided(Kart self, Collision c)
        {
            if (crushEffect == null || self.Effects.Has(crushEffect)) return;
            var other = Kart.FromCollider(c.collider);
            if (other == null || other == self || other.Eliminated) return;
            if (c.relativeVelocity.magnitude < minRelativeSpeed * self.Physics.SpeedScale) return;
            KartReactions.Apply(self, crushEffect, other);
        }
    }
}
