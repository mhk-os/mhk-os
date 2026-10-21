using MHK.Karts;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>Drops several hazards scattered around the user (Thunder: Affe's banana rain, 10 bananas within ±radius).</summary>
    public class ScatterPowerup : PowerupBehaviour
    {
        const float GroundProbeUp = 3f;
        const float GroundProbeDown = 7f;

        [SerializeField] GameObject hazardPrefab;
        [SerializeField, Min(1)] int count = 10;
        public float radius = 3.5f;
        [SerializeField] float minDistance = 1.5f;
        [SerializeField] LayerMask groundMask = ~0;
        [SerializeField] float groundOffset = 0.1f;

        protected override void OnLaunch()
        {
            var o = Owner.transform.position;
            for (var i = 0; i < count; i++)
            {
                var offs = Random.insideUnitCircle * radius;
                if (offs.magnitude < minDistance) offs = offs.normalized * minDistance;
                var p = o + new Vector3(offs.x, 0f, offs.y);
                if (GroundProbe.Down(p, GroundProbeUp, GroundProbeDown, groundMask, out var hit) && Kart.FromCollider(hit.collider) == null)
                    p = hit.point + Vector3.up * groundOffset;
                PowerupFx.SpawnAndLaunch(hazardPrefab, p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), Context);
            }
            Destroy(gameObject);
        }
    }
}
