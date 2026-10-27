using System.Collections.Generic;
using MHK.Race;
using Unity.Collections;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Karts
{
    public partial class KartPhysics
    {
        const float GhostRescanInterval = 0.5f;

        /// <summary>
        /// The chassis passes through other karts, dropped items, movable (non-kinematic) props, track hazards and the
        /// bodies animated props move (stomper walls), and no wheel rests on them. Static track geometry still collides.
        /// Set while a ghost effect is active and until the kart is clear of what it passed through.
        /// </summary>
        public bool Intangible { get; private set; }

        /// <summary>
        /// Run over: other karts drive through this one and their wheels do not rest on it (flattened), set while a
        /// run-over effect is active and until no other kart overlaps it. Items and hazards still hit it.
        /// </summary>
        public bool RunOver { get; private set; }

        [AutoStaticsCleanup] static readonly HashSet<Rigidbody> passThroughBodies = new();
        /// <summary>Chassis colliders that slide along steep static faces, with their <see cref="wallMaxNormalY"/>.</summary>
        [AutoStaticsCleanup] static readonly Dictionary<EntityId, float> wallSlideChassis = new();
        static readonly Collider[] overlapBuffer = new Collider[32];
        static readonly List<Collider> colliderScratch = new();

        readonly HashSet<Collider> ghostIgnored = new();
        LayerMask chassisExcludeLayers;
        bool passThroughLayersSet;
        float ghostRescan, ghostExitTime, runOverExitTime;
        EntityId wallSlideId = EntityId.None;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void SubscribeContactModification()
        {
            Physics.ContactModifyEvent -= SlideAlongWalls;
            Physics.ContactModifyEventCCD -= SlideAlongWalls;
            Physics.ContactModifyEvent += SlideAlongWalls;
            Physics.ContactModifyEventCCD += SlideAlongWalls;
        }

        void RegisterWallSlide()
        {
            if (wallSlideId != EntityId.None) wallSlideChassis.Remove(wallSlideId);
            wallSlideId = EntityId.None;
            chassis.hasModifiableContacts = wallMaxNormalY > 0f;
            if (!chassis.hasModifiableContacts) return;
            wallSlideId = chassis.GetEntityId();
            wallSlideChassis[wallSlideId] = wallMaxNormalY;
        }

        void OnDestroy()
        {
            if (wallSlideId != EntityId.None) wallSlideChassis.Remove(wallSlideId);
        }

        // PhysX contact modification on the simulation thread. It only reads wallSlideChassis, which changes on the main
        // thread between steps: chassis contacts with static faces steeper than the kart's wallMaxNormalY lose friction.
        static void SlideAlongWalls(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            for (var i = 0; i < pairs.Length; i++)
            {
                var pair = pairs[i];
                bool otherStatic;
                if (wallSlideChassis.TryGetValue(pair.colliderEntityId, out var maxY)) otherStatic = !pair.otherBodyEntityId.IsValid();
                else if (wallSlideChassis.TryGetValue(pair.otherColliderEntityId, out maxY)) otherStatic = !pair.bodyEntityId.IsValid();
                else continue;
                if (!otherStatic) continue;
                for (var c = 0; c < pair.contactCount; c++)
                {
                    if (Mathf.Abs(pair.GetNormal(c).y) >= maxY) continue;
                    pair.SetStaticFriction(c, 0f);
                    pair.SetDynamicFriction(c, 0f);
                }
            }
        }

        void OnDisable()
        {
            if (Intangible) EndIntangible();
            if (RunOver)
            {
                RunOver = false;
                ApplyPassThrough();
            }
        }

        void UpdateIntangible(bool ghost, float h)
        {
            if (chassis == null) return;
            if (ghost)
            {
                StayIntangible(h);
                return;
            }
            if (!Intangible) return;
            ghostExitTime += h;
            if (ghostExitTime < ghostExitTimeout && Overlaps(ghostExcludeLayers, true)) return;
            EndIntangible();
        }

        void StayIntangible(float h)
        {
            if (!Intangible)
            {
                Intangible = true;
                ApplyPassThrough();
                ghostRescan = 0f;
            }
            ghostExitTime = 0f;
            ghostRescan -= h;
            if (ghostRescan > 0f) return;
            ghostRescan = GhostRescanInterval;
            IgnoreMovables();
        }

        void UpdateRunOver(bool runOver, float h)
        {
            if (chassis == null) return;
            if (runOver)
            {
                runOverExitTime = 0f;
                if (RunOver) return;
                RunOver = true;
                ApplyPassThrough();
                return;
            }
            if (!RunOver) return;
            runOverExitTime += h;
            if (runOverExitTime < ghostExitTimeout && Overlaps(runOverExcludeLayers, false)) return;
            RunOver = false;
            ApplyPassThrough();
        }

        // Chassis layer exclusions and wheel pass-through for the current Intangible / RunOver state.
        void ApplyPassThrough()
        {
            var any = Intangible || RunOver;
            if (any) passThroughBodies.Add(Body);
            else passThroughBodies.Remove(Body);
            if (chassis == null) return;
            if (any && !passThroughLayersSet)
            {
                chassisExcludeLayers = chassis.excludeLayers;
                passThroughLayersSet = true;
            }
            if (!passThroughLayersSet) return;
            chassis.excludeLayers = chassisExcludeLayers | (Intangible ? ghostExcludeLayers.value : 0) | (RunOver ? runOverExcludeLayers.value : 0);
            if (!any) passThroughLayersSet = false;
        }

        // Movable things the chassis passes through: non-kinematic bodies, track hazards and the bodies animated props move
        // (other karts by layer).
        void IgnoreMovables()
        {
            foreach (var rb in FindObjectsByType<Rigidbody>())
            {
                if (rb == Body || (ghostExcludeLayers.value & (1 << rb.gameObject.layer)) != 0) continue;
                if (rb.isKinematic && !rb.TryGetComponent<TrackHazard>(out _) && rb.GetComponentInParent<PropAnimator>() == null) continue;
                if (rb.gameObject.scene.GetPhysicsScene() != physicsScene) continue;
                rb.GetComponentsInChildren(colliderScratch);
                foreach (var c in colliderScratch)
                {
                    if (c.isTrigger || c.attachedRigidbody != rb || !ghostIgnored.Add(c)) continue;
                    Physics.IgnoreCollision(chassis, c, true);
                }
            }
            colliderScratch.Clear();
        }

        bool Overlaps(LayerMask layers, bool ghostIgnoredToo)
        {
            var t = chassis.transform;
            var centre = t.TransformPoint(chassis.center);
            var half = Vector3.Scale(chassis.size * 0.5f, t.lossyScale);
            var n = physicsScene.OverlapBox(centre, half, overlapBuffer, t.rotation, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var c = overlapBuffer[i];
                if (c.attachedRigidbody == Body) continue;
                if ((ghostIgnoredToo && ghostIgnored.Contains(c)) || (layers.value & (1 << c.gameObject.layer)) != 0) return true;
            }
            return false;
        }

        void EndIntangible()
        {
            Intangible = false;
            ApplyPassThrough();
            if (chassis != null)
                foreach (var c in ghostIgnored)
                    if (c != null) Physics.IgnoreCollision(chassis, c, false);
            ghostIgnored.Clear();
        }

        // Wheel rays pass through intangible and run-over karts, and such a kart's wheels pass through what its chassis does.
        bool PassesThrough(in RaycastHit hit)
        {
            var rb = hit.rigidbody;
            if (rb != null && passThroughBodies.Contains(rb)) return true;
            var layer = 1 << hit.collider.gameObject.layer;
            if (RunOver && (runOverExcludeLayers.value & layer) != 0) return true;
            return Intangible && (ghostIgnored.Contains(hit.collider) || (ghostExcludeLayers.value & layer) != 0);
        }
    }
}
