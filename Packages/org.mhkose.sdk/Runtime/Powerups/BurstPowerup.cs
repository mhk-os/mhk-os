using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Fires several copies of another use prefab one after the other (MHK3 soap cannon: 3 bubbles 0.5 s apart), timed in
    /// physics steps. Each shot receives the same <see cref="PowerupContext"/> as this object.
    /// </summary>
    public class BurstPowerup : PowerupBehaviour
    {
        public GameObject shotPrefab;
        [SerializeField, Min(1)] int count = 3;
        [SerializeField] float interval = 0.5f;
        [SerializeField] float firstDelay;

        // Summed steps fall short of a whole interval by rounding: a shot due within this (s) fires on that step.
        const float DueTolerance = 1e-4f;

        float elapsed;
        int fired;

        protected override void OnLaunch()
        {
            if (firstDelay <= 0f) Fire();
        }

        protected override void Step(float dt)
        {
            elapsed += dt;
            while (fired < count && elapsed >= firstDelay + fired * interval - DueTolerance && isActiveAndEnabled) Fire();
        }

        void Fire()
        {
            if (Owner == null || shotPrefab == null)
            {
                Remove();
                return;
            }
            PowerupFx.SpawnAndLaunch(shotPrefab, Owner.transform.position, Owner.transform.rotation, Context);
            if (++fired >= count) Remove();
        }
    }
}
