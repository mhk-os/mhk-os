using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// MHK2 crash classification (work/mhk2/physics.md §10): the velocity change of a kart's collision in original m/s
    /// against 10 / 5 / 7 / 5 m/s for front / left / back / right. Shared by the crash sound and the collide animations.
    /// </summary>
    public readonly struct KartImpact
    {
        /// <summary>Seconds a kart ignores further impacts after one counted.</summary>
        public const float CooldownSeconds = 0.2f;
        const float ScratchShare = 0.75f;
        const float FrontThreshold = 10f, BackThreshold = 7f, SideThreshold = 5f;

        /// <summary>Velocity change of the hit in original MHK2 m/s.</summary>
        public readonly float deltaV;
        /// <summary>Hit side: CollideFront, CollideBack, CollideLeft or CollideRight.</summary>
        public readonly CharacterAnimRole side;
        /// <summary>The first contact of the collision.</summary>
        public readonly ContactPoint contact;
        readonly float threshold;

        KartImpact(float deltaV, CharacterAnimRole side, float threshold, ContactPoint contact)
        {
            this.deltaV = deltaV;
            this.side = side;
            this.threshold = threshold;
            this.contact = contact;
        }

        /// <summary>Between 0.75× and 1× of the side's threshold: a scratch (shake) instead of a crash.</summary>
        public bool IsScratch => deltaV < threshold;

        /// <summary>
        /// Classifies <paramref name="c"/> on <paramref name="kart"/>. False when it has no contact or is too soft to
        /// count (below 0.75× of the hit side's threshold).
        /// </summary>
        public static bool TryClassify(Kart kart, Collision c, out KartImpact impact)
        {
            impact = default;
            if (c.contactCount == 0) return false;
            var contact = c.GetContact(0);
            var dv = c.impulse.magnitude / Mathf.Max(1f, kart.Physics.Body.mass);
            if (dv <= 0f) dv = Mathf.Abs(Vector3.Dot(c.relativeVelocity, contact.normal));
            dv = kart.Physics.ToOriginalSpeed(dv);
            var local = kart.transform.InverseTransformDirection(contact.point - kart.transform.position);
            CharacterAnimRole side;
            float threshold;
            if (Mathf.Abs(local.z) > Mathf.Abs(local.x))
            {
                side = local.z > 0f ? CharacterAnimRole.CollideFront : CharacterAnimRole.CollideBack;
                threshold = local.z > 0f ? FrontThreshold : BackThreshold;
            }
            else
            {
                side = local.x > 0f ? CharacterAnimRole.CollideRight : CharacterAnimRole.CollideLeft;
                threshold = SideThreshold;
            }
            if (dv < ScratchShare * threshold) return false;
            impact = new KartImpact(dv, side, threshold, contact);
            return true;
        }
    }
}
