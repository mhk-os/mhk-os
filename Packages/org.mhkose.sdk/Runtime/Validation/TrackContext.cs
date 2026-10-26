using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Race;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Validation
{
    /// <summary>Track features at a point that explain motion the kart's own physics cannot (<see cref="TrackContext.Classify"/>).</summary>
    [Flags]
    public enum TrackRegion
    {
        None = 0,
        /// <summary>Near a speed pad.</summary>
        Boost = 1,
        /// <summary>Near a push zone (conveyor belt, fan): the position drifts without velocity.</summary>
        Push = 2,
        /// <summary>Near something that moves (animated or physics props, hazards): not in the static copy.</summary>
        Mover = 4,
        /// <summary>In a collapsing bridge's corridor.</summary>
        Bridge = 8,
    }

    /// <summary>
    /// What a kart validator needs to know about the loaded track: its racing line, the track features that move karts
    /// (speed pads, push zones, movers, collapsing bridges) and a local physics scene holding copies of the static
    /// collision geometry (shared meshes, no renderers). Geometry queries and re-simulations run in that scene, so they
    /// never see karts, ghosts, items or props in motion and never disturb the race. One per loaded track.
    /// </summary>
    public sealed class TrackContext : IDisposable
    {
        const float BoostMargin = 3f;
        const float PushMargin = 2f;
        const float MoverMargin = 8f;
        const int OverlapCapacity = 32;

        public TrackRuntime Track { get; }
        /// <summary>The local physics scene of the static copy.</summary>
        public PhysicsScene Physics { get; }
        /// <summary>Static geometry that blocks karts (<see cref="TrackRuntime.ObstacleMask"/>).</summary>
        public int ObstacleMask { get; }
        /// <summary>Magnitude of the world gravity (m/s²).</summary>
        public float Gravity { get; }
        /// <summary>Copied colliders.</summary>
        public int ColliderCount { get; private set; }

        readonly Scene scene;
        readonly KartPhysics template;
        readonly List<Bounds> boosts = new(), movers = new();
        readonly List<(Bounds bounds, float speed)> pushes = new();
        readonly List<CollapsingBridge> bridges = new();
        readonly Collider[] overlaps = new Collider[OverlapCapacity];
        readonly List<(KartSpec spec, ResimKart kart)> karts = new();
        BoxCollider probe;
        bool disposed;

        TrackContext(TrackRuntime track, KartPhysics template)
        {
            Track = track;
            this.template = template;
            ObstacleMask = track.ObstacleMask;
            Gravity = UnityEngine.Physics.gravity.magnitude;
            scene = SceneManager.CreateScene($"Validation {track.name} {Guid.NewGuid():N}", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            Physics = scene.GetPhysicsScene();
        }

        /// <summary>
        /// Builds the context of <paramref name="track"/> (a loaded track scene). <paramref name="template"/>: the race
        /// kart's physics, whose inspector settings re-simulation karts take.
        /// </summary>
        public static TrackContext Create(TrackRuntime track, KartPhysics template)
        {
            var ctx = new TrackContext(track, template);
            ctx.Collect(track.gameObject.scene);
            return ctx;
        }

        void Collect(Scene source)
        {
            var root = new GameObject("Static copy");
            SceneManager.MoveGameObjectToScene(root, scene);
            var holders = new Dictionary<TrackSurface, Transform>();
            var colliders = new List<Collider>();
            foreach (var go in source.GetRootGameObjects())
            {
                foreach (var zone in go.GetComponentsInChildren<TrackZone>()) AddZone(zone);
                bridges.AddRange(go.GetComponentsInChildren<CollapsingBridge>());
                foreach (var animator in go.GetComponentsInChildren<PropAnimator>())
                    foreach (var c in animator.GetComponentsInChildren<Collider>())
                        AddMover(c.bounds);
                go.GetComponentsInChildren(colliders);
                foreach (var c in colliders) Consider(c, root.transform, holders);
            }
            var probeObject = new GameObject("Probe") { layer = 2 };
            probeObject.transform.SetParent(root.transform, false);
            probe = probeObject.AddComponent<BoxCollider>();
            probe.isTrigger = true;
        }

        void AddZone(TrackZone zone)
        {
            if (!zone.isActiveAndEnabled || !zone.TryGetComponent<BoxCollider>(out var box)) return;
            var b = box.bounds;
            if (zone.kind == TrackZoneKind.Boost)
            {
                b.Expand(2f * BoostMargin);
                boosts.Add(b);
            }
            else if (zone.kind == TrackZoneKind.Push)
            {
                b.Expand(2f * PushMargin);
                pushes.Add((b, zone.pushFalloff ? zone.pushMaxSpeed : Mathf.Min(Mathf.Abs(zone.value), zone.pushMaxSpeed)));
            }
        }

        void AddMover(Bounds b)
        {
            b.Expand(2f * MoverMargin);
            movers.Add(b);
        }

        void Consider(Collider c, Transform root, Dictionary<TrackSurface, Transform> holders)
        {
            if (!c.enabled || !c.gameObject.activeInHierarchy || c.isTrigger) return;
            if (c.attachedRigidbody != null)
            {
                AddMover(c.bounds);
                return;
            }
            if (c.GetComponentInParent<PropAnimator>() != null) return;
            if (c is not (BoxCollider or SphereCollider or CapsuleCollider or MeshCollider)) return;
            var surface = TrackSurface.Of(c);
            var parent = surface != null ? HolderFor(surface, root, holders) : root;
            Copy(c, parent);
            ColliderCount++;
        }

        static Transform HolderFor(TrackSurface surface, Transform root, Dictionary<TrackSurface, Transform> holders)
        {
            if (holders.TryGetValue(surface, out var holder)) return holder;
            var go = new GameObject(surface.name);
            go.transform.SetParent(root, false);
            var copy = go.AddComponent<TrackSurface>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(surface), copy);
            holders[surface] = go.transform;
            return go.transform;
        }

        static void Copy(Collider c, Transform parent)
        {
            var t = c.transform;
            var go = new GameObject(c.name) { layer = c.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(t.position, t.rotation);
            go.transform.localScale = t.lossyScale;
            Collider copy;
            switch (c)
            {
                case BoxCollider b:
                    var box = go.AddComponent<BoxCollider>();
                    box.center = b.center;
                    box.size = b.size;
                    copy = box;
                    break;
                case SphereCollider s:
                    var sphere = go.AddComponent<SphereCollider>();
                    sphere.center = s.center;
                    sphere.radius = s.radius;
                    copy = sphere;
                    break;
                case CapsuleCollider k:
                    var capsule = go.AddComponent<CapsuleCollider>();
                    capsule.center = k.center;
                    capsule.radius = k.radius;
                    capsule.height = k.height;
                    capsule.direction = k.direction;
                    copy = capsule;
                    break;
                default:
                    var m = (MeshCollider)c;
                    var mesh = go.AddComponent<MeshCollider>();
                    mesh.cookingOptions = m.cookingOptions;
                    mesh.convex = m.convex;
                    mesh.sharedMesh = m.sharedMesh;
                    copy = mesh;
                    break;
            }
            copy.sharedMaterial = c.sharedMaterial;
            copy.contactOffset = c.contactOffset;
            copy.includeLayers = c.includeLayers;
            copy.excludeLayers = c.excludeLayers;
            copy.hasModifiableContacts = c.hasModifiableContacts;
        }

        /// <summary>Track features around <paramref name="position"/>.</summary>
        public TrackRegion Classify(Vector3 position)
        {
            var r = TrackRegion.None;
            foreach (var b in boosts)
                if (b.Contains(position)) { r |= TrackRegion.Boost; break; }
            foreach (var p in pushes)
                if (p.bounds.Contains(position)) { r |= TrackRegion.Push; break; }
            foreach (var b in movers)
                if (b.Contains(position)) { r |= TrackRegion.Mover; break; }
            foreach (var b in bridges)
                if (b != null && b.Contains(position)) { r |= TrackRegion.Bridge; break; }
            return r;
        }

        /// <summary>Strongest push-zone drift (m/s) around <paramref name="position"/>, 0 outside them.</summary>
        public float PushSpeedAt(Vector3 position)
        {
            var speed = 0f;
            foreach (var p in pushes)
                if (p.bounds.Contains(position)) speed = Mathf.Max(speed, p.speed);
            return speed;
        }

        /// <summary>Distance from <paramref name="origin"/> straight down to static geometry within <paramref name="maxDistance"/>.</summary>
        public bool GroundBelow(Vector3 origin, float maxDistance, out float distance)
        {
            var hit = Physics.Raycast(origin, Vector3.down, out var h, maxDistance, ObstacleMask, QueryTriggerInteraction.Ignore);
            distance = hit ? h.distance : float.PositiveInfinity;
            return hit;
        }

        /// <summary>
        /// The first face of static geometry steeper than <paramref name="wallMaxNormalY"/> (or with a wall surface) on the
        /// way from <paramref name="from"/> to <paramref name="to"/>, front faces only.
        /// </summary>
        public bool WallBetween(Vector3 from, Vector3 to, float wallMaxNormalY, out RaycastHit hit)
        {
            var d = to - from;
            var length = d.magnitude;
            hit = default;
            if (length < 1e-4f || !Physics.Raycast(from, d / length, out hit, length, ObstacleMask, QueryTriggerInteraction.Ignore)) return false;
            if (Mathf.Abs(hit.normal.y) < wallMaxNormalY) return true;
            var surface = TrackSurface.Of(hit.collider);
            return surface != null && surface.kind == SurfaceKind.Wall;
        }

        /// <summary>Deepest penetration (m) of the chassis box of <paramref name="spec"/> at the given pose into static geometry.</summary>
        public float ChassisPenetration(Vector3 position, Quaternion rotation, KartSpec spec)
        {
            var centre = position + rotation * spec.chassisCentre;
            var n = Physics.OverlapBox(centre, spec.chassisHalfExtents, overlaps, rotation, ObstacleMask, QueryTriggerInteraction.Ignore);
            if (n == 0) return 0f;
            probe.center = Vector3.zero;
            probe.size = spec.chassisHalfExtents * 2f;
            var deepest = 0f;
            for (var i = 0; i < n; i++)
            {
                var c = overlaps[i];
                if (c.attachedRigidbody != null) continue;
                var t = c.transform;
                if (UnityEngine.Physics.ComputePenetration(probe, centre, rotation, c, t.position, t.rotation, out _, out var depth))
                    deepest = Mathf.Max(deepest, depth);
            }
            return deepest;
        }

        /// <summary>Builds the re-simulation kart of <paramref name="spec"/> now (while the race loads), not on its first window.</summary>
        public void Prepare(KartSpec spec) => KartFor(spec).SetActive(false);

        /// <summary>The re-simulation kart of <paramref name="spec"/>, active; every other one is deactivated.</summary>
        internal ResimKart KartFor(KartSpec spec)
        {
            ResimKart found = null;
            foreach (var (s, k) in karts)
            {
                if (found == null && s.SamePhysics(spec)) found = k;
                else k.SetActive(false);
            }
            if (found == null)
            {
                found = new ResimKart(scene, template, spec);
                karts.Add((spec, found));
            }
            found.SetActive(true);
            return found;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            karts.Clear();
            if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
        }
    }

    /// <summary>A stripped kart (rigidbody, chassis, <see cref="KartPhysics"/>) in a <see cref="TrackContext"/>'s physics scene.</summary>
    internal sealed class ResimKart
    {
        public readonly KartPhysics physics;
        public readonly ResimContactProbe contacts;
        readonly GameObject gameObject;

        public ResimKart(Scene scene, KartPhysics template, KartSpec spec)
        {
            gameObject = new GameObject("Re-simulated kart") { layer = template.gameObject.layer };
            gameObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            physics = gameObject.AddComponent<KartPhysics>();
            contacts = gameObject.AddComponent<ResimContactProbe>();
            physics.CopySettings(template);
            physics.Profile = spec.profile;
            gameObject.SetActive(true);
            physics.ClassFactor = spec.classFactor;
            physics.SpeedScale = spec.speedScale;
        }

        public void SetActive(bool active)
        {
            if (gameObject.activeSelf != active) gameObject.SetActive(active);
        }
    }
}
