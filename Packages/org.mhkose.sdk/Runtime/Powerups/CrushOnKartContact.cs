using System.Collections.Generic;
using MHK.Karts;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Makes the kart it is attached to crushable: another kart touching it applies <see cref="crushEffect"/> to this
    /// kart (MHK3 shrunk karts get squashed). Put it on a status effect's attachedVfxPrefab so it lives with the effect.
    /// </summary>
    public partial class CrushOnKartContact : MonoBehaviour
    {
        [SerializeField] StatusEffectDefinition crushEffect;
        [Tooltip("Only contacts at least this fast (relative, original m/s × the kart's SpeedScale) crush.")]
        [SerializeField] float minRelativeSpeed = 0.5f;

        // Karts carrying an enabled instance (one entry per instance).
        [AutoStaticsCleanup] static readonly List<Kart> crushable = new();

        Kart kart;

        /// <summary>
        /// <paramref name="k"/> carries an enabled <see cref="CrushOnKartContact"/>: driving into it flattens it, so
        /// computer drivers need not steer around it (and may aim for it).
        /// </summary>
        public static bool IsCrushable(Kart k)
        {
            for (var i = 0; i < crushable.Count; i++)
                if (crushable[i] == k)
                    return true;
            return false;
        }

        void OnEnable()
        {
            kart = GetComponentInParent<Kart>();
            if (kart == null) return;
            kart.Collided += OnCollided;
            crushable.Add(kart);
        }

        void OnDisable()
        {
            if (kart == null) return;
            kart.Collided -= OnCollided;
            crushable.Remove(kart);
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
