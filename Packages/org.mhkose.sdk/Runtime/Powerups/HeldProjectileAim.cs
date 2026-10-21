using MHK.Core;
using MHK.Karts;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// Root of a held-item prefab whose use prefab fires a <see cref="StraightProjectile"/> (MHK Extra fireworks rocket):
    /// floats the item above the driver's head and points it where the next shot would fly
    /// (<see cref="StraightProjectile.AimDirection"/>). The shot starts at this root.
    /// </summary>
    public class HeldProjectileAim : MonoBehaviour
    {
        [Tooltip("Height of the item's centre above the top of the driver (m, in the kart's visual space).")]
        [SerializeField] float headClearance = 0.6f;
        [Tooltip("Height of the item's centre above the kart's visual origin when the kart has no driver (m).")]
        [SerializeField] float fallbackHeight = 1.8f;
        [Tooltip("How quickly the height follows the driver's animated top (1/s, 0 = instantly).")]
        [SerializeField] float heightSharpness = 12f;

        Kart kart;
        StraightProjectile shot;
        Vector3 localPosition;
        bool placed;

        void Start()
        {
            kart = GetComponentInParent<Kart>();
            var def = kart != null && kart.Inventory != null ? kart.Inventory.Held : null;
            shot = def != null && def.usePrefab != null ? def.usePrefab.GetComponent<StraightProjectile>() : null;
            LateUpdate();
        }

        void LateUpdate()
        {
            if (kart == null) return;
            var target = kart.Visual != null && kart.Visual.TryGetDriverTop(out var top)
                ? top + Vector3.up * headClearance
                : Vector3.up * fallbackHeight;
            localPosition = placed && heightSharpness > 0f
                ? Vector3.Lerp(localPosition, target, Smoothing.Factor(heightSharpness, Time.deltaTime))
                : target;
            placed = true;
            transform.localPosition = localPosition;

            var aim = shot != null ? shot.aim : ProjectileAim.KartForward;
            var smoothing = shot != null ? shot.trackTangentSmoothing : 0f;
            var dir = StraightProjectile.AimDirection(kart, aim, smoothing, kart.CurrentInput.lookBack);
            transform.rotation = StraightProjectile.AimRotation(kart, aim, dir);
        }
    }
}
