using MHK.Karts;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    public enum HazardKind
    {
        /// <summary>Any contact squashes the kart (rolling cannonballs, tumbling boulders).</summary>
        Touch,
        /// <summary>Squashes only a kart it comes down on from above. Its sides are a solid (moving) wall.</summary>
        Stomp,
    }

    /// <summary>
    /// Moving kinematic hazard (MHK3 crushers) that applies <see cref="effectId"/> to karts it hits. Touch hazards are
    /// triggers, Stomp hazards stay solid and only fire when coming down on a kart's roof.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public partial class TrackHazard : MonoBehaviour
    {
        [Tooltip("Touch: any contact hits. Stomp: only coming down on a kart's roof hits.")]
        public HazardKind kind = HazardKind.Touch;
        [Tooltip("Status effect id applied to the hit kart (resolved through the content registry). A shared reaction such " +
                 "as core.reaction.flatten, or a track game's own effect based on one (mhk3.hit.crushed).")]
        public string effectId = KartReactions.Flatten;
        [Tooltip("Stomp: minimum downward speed of the hazard relative to the kart at the contact (m/s).")]
        public float minDownSpeed = 0.3f;
        [Tooltip("Stomp: the contact normal must be at least this vertical (|dot(normal, kart up)|).")]
        [Range(0f, 1f)] public float minVerticalNormal = 0.6f;
        [Tooltip("Stomp: contact height above the kart's centre of mass as a fraction of the kart collider's half height.")]
        [Range(0f, 1f)] public float minRoofHeight = 0.3f;

        [NoAutoStaticsCleanup] static readonly ContactPoint[] Contacts = new ContactPoint[16];

        Rigidbody body;
        StatusEffectDefinition effect;
        bool effectResolved;
        Vector3 stepStartPos;
        Quaternion stepStartRot = Quaternion.identity;

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            if (kind == HazardKind.Touch)
                foreach (var c in GetComponentsInChildren<Collider>()) c.isTrigger = true;
        }

        void OnEnable()
        {
            stepStartPos = body.position;
            stepStartRot = body.rotation;
        }

        // Pose at the start of the physics step. Collision callbacks run after the step and compare against it.
        void FixedUpdate()
        {
            stepStartPos = body.position;
            stepStartRot = body.rotation;
        }

        StatusEffectDefinition Effect
        {
            get
            {
                if (effectResolved) return effect;
                effectResolved = true;
                effect = TrackEffect.Resolve(effectId);
                if (effect == null) Debug.LogWarning($"[TrackHazard] {name}: effect '{effectId}' not found");
                return effect;
            }
        }

        // Touch colliders are triggers (Awake), so only stomp hazards get collision callbacks.
        void OnCollisionEnter(Collision c) => Hit(c);
        void OnCollisionStay(Collision c) => Hit(c);
        void OnTriggerEnter(Collider other) => Touch(other);
        void OnTriggerStay(Collider other) => Touch(other);

        void Touch(Collider other)
        {
            if (kind == HazardKind.Touch && Victim(other, out var kart, out var def)) KartReactions.Apply(kart, def);
        }

        void Hit(Collision c)
        {
            if (kind == HazardKind.Stomp && Victim(c.collider, out var kart, out var def) && Stomps(kart, c)) KartReactions.Apply(kart, def);
        }

        bool Victim(Collider other, out Kart kart, out StatusEffectDefinition def)
        {
            kart = Kart.FromCollider(other);
            def = kart == null || kart.Eliminated || kart.Effects.Ghost ? null : Effect;
            return def != null && !kart.Effects.Has(def) && !kart.Effects.HasRespawnPending;
        }

        bool Stomps(Kart kart, Collision c)
        {
            var kartBody = kart.Physics.Body;
            var up = kart.transform.up;
            var centre = kartBody.worldCenterOfMass;
            var halfHeight = c.collider.bounds.extents.y;
            var dt = Time.fixedDeltaTime;
            var n = c.GetContacts(Contacts);
            for (var i = 0; i < n; i++)
            {
                var cp = Contacts[i];
                if (Mathf.Abs(Vector3.Dot(cp.normal, up)) < minVerticalNormal) continue;
                if (Vector3.Dot(cp.point - centre, up) < minRoofHeight * halfHeight) continue;
                // Velocity of the hazard's surface at the contact over the last step (kinematic: from its poses).
                var local = Quaternion.Inverse(body.rotation) * (cp.point - body.position);
                var before = stepStartPos + stepStartRot * local;
                var hazardVel = (cp.point - before) / dt;
                if (Vector3.Dot(hazardVel - kartBody.GetPointVelocity(cp.point), up) <= -minDownSpeed) return true;
            }
            return false;
        }
    }
}
