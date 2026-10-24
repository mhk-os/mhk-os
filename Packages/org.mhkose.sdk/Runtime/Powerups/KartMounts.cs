using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>Where held items and attached effects hang on a kart.</summary>
    public static class KartMounts
    {
        /// <summary>
        /// Transform held items and attached effects hang on: the kart's drawn body (follows spin-outs, squashing, quakes
        /// and the body bob), else the kart.
        /// </summary>
        public static Transform Parent(Kart k) => k.Visual != null ? k.Visual.Body : k.transform;

        /// <summary>
        /// <see cref="Parent"/> without the visual-only body bob (the kart's visual root), for gameplay that starts from a
        /// mount (item drops, hammer strikes): the body bob changes per rendered frame and must not move items.
        /// </summary>
        public static Transform Frame(Kart k) => k.Visual != null ? k.Visual.transform : k.transform;

        /// <summary>Attachment point of <paramref name="mount"/> in <see cref="Parent"/> and <see cref="Frame"/> space, from the kart's chassis box.</summary>
        public static Vector3 Point(Kart k, HeldMount mount)
        {
            if (mount == HeldMount.Origin || k == null || !k.TryGetComponent<BoxCollider>(out var box)) return Vector3.zero;
            var c = box.center;
            var h = box.size * 0.5f;
            return mount switch
            {
                HeldMount.Front => c + Vector3.forward * h.z,
                HeldMount.Back => c - Vector3.forward * h.z,
                _ => c + Vector3.up * h.y,
            };
        }
    }
}
