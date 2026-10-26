using System.Collections.Generic;
using MHK.Karts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Zone kinds. Values are serialized (0 was MHK2's AI "speed limit" area, replaced by the computer drivers' own
    /// braking for bends ahead).
    /// </summary>
    public enum TrackZoneKind
    {
        [Tooltip("Karts entering use 'respawnPoint' for respawns until they are back on the racing line (MHK2 'respawn').")] RespawnPoint = 1,
        [Tooltip("Karts entering are respawned immediately.")] Kill = 2,
        [Tooltip("Speed pad: turns the kart to the zone's forward axis, applies 'effectId' and raises its speed to its current top speed including that effect's multiplier (MHK3 SpeedUp).")] Boost = 3,
        [Tooltip("Push: karts inside drift along the zone's forward axis at 'value' m/s on top of their own motion (conveyor belts, fans with 'pushFalloff').")] Push = 4,
        [Tooltip("Effect: karts inside have 'effectId' (re-applied whenever it has run out, so a short effect lasts while the kart stays inside: oil slicks).")] Effect = 5,
    }

    /// <summary>
    /// Box trigger on a track with gameplay meaning for karts (respawn overrides, kill zones, speed pads, conveyor belts /
    /// fans, effect areas).
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public partial class TrackZone : MonoBehaviour
    {
        [Tooltip("What the zone does to karts inside it.")]
        public TrackZoneKind kind = TrackZoneKind.RespawnPoint;
        [Tooltip("Push: drift speed (m/s) along the zone's forward axis, added to the kart's own motion (TINcan conveyor belts " +
                 "and fans move the robot's position, whatever its speed).")]
        public float value = 20f;
        [Tooltip("RespawnPoint: pose karts respawn at after entering the zone.")]
        public Transform respawnPoint;
        [Tooltip("Boost / Effect: status effect id applied (resolved through the content registry).")]
        public string effectId;
        [Tooltip("Push: the push speed falls off with the distance to the zone's origin: value / max(pushFalloffMinDistance, distance) (fans).")]
        public bool pushFalloff;
        [Tooltip("Push with falloff: distances below this (m) count as this, so the push stays finite at the centre.")]
        public float pushFalloffMinDistance = 2f;
        [Tooltip("Push: upper limit of the drift speed (m/s).")]
        public float pushMaxSpeed = 15f;

        readonly HashSet<Kart> pushed = new();

        [AutoStaticsCleanup] static readonly List<TrackZone> speedPads = new();
        [AutoStaticsCleanup] static readonly List<TrackZone> killZones = new();
        /// <summary>All enabled speed pads (<see cref="TrackZoneKind.Boost"/>), for computer drivers.</summary>
        public static IReadOnlyList<TrackZone> SpeedPads => speedPads;
        /// <summary>All enabled kill zones (<see cref="TrackZoneKind.Kill"/>), for computer drivers.</summary>
        public static IReadOnlyList<TrackZone> KillZones => killZones;

        Modding.StatusEffectDefinition effect;
        bool effectResolved;
        BoxCollider box;

        void OnEnable()
        {
            if (kind == TrackZoneKind.Boost) speedPads.Add(this);
            if (kind == TrackZoneKind.Kill) killZones.Add(this);
        }

        void OnDisable()
        {
            speedPads.Remove(this);
            killZones.Remove(this);
        }

        /// <summary><paramref name="point"/> lies inside the zone's box.</summary>
        public bool Contains(Vector3 point)
        {
            if (box == null && !TryGetComponent(out box)) return false;
            var local = box.transform.InverseTransformPoint(point) - box.center;
            var half = box.size * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        void Reset() => GetComponent<BoxCollider>().isTrigger = true;

        Modding.StatusEffectDefinition Effect
        {
            get
            {
                if (effectResolved) return effect;
                effectResolved = true;
                effect = TrackEffect.Resolve(effectId);
                return effect;
            }
        }

        void OnTriggerEnter(Collider other)
        {
            var kart = Kart.FromCollider(other);
            // A remote racer meets the zones on its own client.
            if (kart == null || kart.IsRemote) return;
            switch (kind)
            {
                case TrackZoneKind.RespawnPoint:
                    if (respawnPoint != null) kart.Progress.SetRespawnOverride(new Pose(respawnPoint.position, respawnPoint.rotation));
                    break;
                case TrackZoneKind.Kill:
                    kart.RequestRespawn();
                    break;
                case TrackZoneKind.Boost:
                    Boost(kart);
                    break;
            }
        }

        void OnTriggerStay(Collider other)
        {
            if (kind != TrackZoneKind.Push && kind != TrackZoneKind.Effect) return;
            var kart = Kart.FromCollider(other);
            if (kart == null || kart.Eliminated || kart.IsRemote) return;
            if (kind == TrackZoneKind.Effect)
            {
                if (Effect != null && !kart.Effects.Has(Effect)) kart.Effects.Apply(Effect);
                return;
            }
            // Each kart once per physics step (its colliders all report the stay).
            if (!pushed.Add(kart)) return;
            var rb = kart.Physics.Body;
            var target = pushFalloff ? value / Mathf.Max(pushFalloffMinDistance, Vector3.Distance(kart.transform.position, transform.position)) : value;
            rb.position += transform.forward * (Mathf.Min(target, pushMaxSpeed) * Time.fixedDeltaTime);
        }

        void FixedUpdate()
        {
            if (pushed.Count > 0) pushed.Clear();
        }

        void Boost(Kart kart)
        {
            if (kart.Eliminated) return;
            var rb = kart.Physics.Body;
            var up = kart.transform.up;
            var fwd = Vector3.ProjectOnPlane(transform.forward, up).normalized;
            if (fwd.sqrMagnitude < 0.5f) return;
            rb.rotation = Quaternion.LookRotation(fwd, up);
            rb.angularVelocity = Vector3.zero;
            // MHK3 SpeedUp: the pad effect (higher top speed) first, then straight to the resulting top speed.
            if (Effect != null) kart.Effects.Apply(Effect, kart);
            var speed = Mathf.Max(Vector3.Dot(rb.linearVelocity, fwd), kart.Physics.MaxSpeed());
            rb.linearVelocity = fwd * speed + Vector3.Project(rb.linearVelocity, up);
        }
    }
}
