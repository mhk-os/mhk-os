using System.Collections;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Fires several copies of another use prefab one after the other (MHK3 soap cannon: 3 bubbles 0.5 s apart).
    /// Each shot receives the same <see cref="PowerupContext"/> as this object.
    /// </summary>
    public class BurstPowerup : PowerupBehaviour
    {
        public GameObject shotPrefab;
        [SerializeField, Min(1)] int count = 3;
        [SerializeField] float interval = 0.5f;
        [SerializeField] float firstDelay;

        protected override void OnLaunch() => StartCoroutine(Run());

        IEnumerator Run()
        {
            if (firstDelay > 0f) yield return new WaitForSeconds(firstDelay);
            for (var i = 0; i < count; i++)
            {
                if (Owner == null || shotPrefab == null) break;
                PowerupFx.SpawnAndLaunch(shotPrefab, Owner.transform.position, Owner.transform.rotation, Context);
                if (i < count - 1) yield return new WaitForSeconds(interval);
            }
            Destroy(gameObject);
        }
    }
}
