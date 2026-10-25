using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>Applies effects to the user (turbo, speed light, shield, ...).</summary>
    public class SelfEffectPowerup : PowerupBehaviour
    {
        public StatusEffectDefinition[] effects;
        [Tooltip("Destroy this object right away (effects carry their own VFX).")]
        [SerializeField] bool destroyImmediately = true;

        protected override void OnLaunch()
        {
            foreach (var e in effects) Owner.Effects.Apply(e, Owner, -1f, Usage);
            if (destroyImmediately) Destroy(gameObject);
        }
    }
}
