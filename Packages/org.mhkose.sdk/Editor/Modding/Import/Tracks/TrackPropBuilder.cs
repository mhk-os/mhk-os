using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Core;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Props of track.json ("props"): models with optional animation (<see cref="PropAnimator"/>), colliders, kinematic
    /// or dynamic bodies, <see cref="TrackHazard"/>s ("crusher", "hazard", "squash") and looping 3D sounds.
    /// </summary>
    public static class TrackPropBuilder
    {
        /// <summary>Builds the Props group under <paramref name="root"/> (after the Collision group).</summary>
        public static void Build(TrackImportContext c, Transform root)
        {
            var propRoot = ImportHierarchy.CreateChild(root, "Props");
            if (c.Json["props"] is not JArray props) return;
            var bounds = CollisionBoundsXZ(root);
            foreach (var p in props)
            {
                if (bounds.HasValue && ExportJson.B(p["dynamic"]) && IsOutside(ExportJson.Pos(p["pos"], c.Scale), bounds.Value))
                {
                    Debug.LogWarning($"[Import] {c.ShortName}: dynamic prop {ExportJson.Str(p["name"])} at {ExportJson.Pos(p["pos"], c.Scale)} lies outside the track's collision; skipped.");
                    continue;
                }
                BuildProp(c, p, propRoot);
            }
        }

        // Loose physics props outside the track's collision would fall forever (export glitches: a crate 2 km away).
        static Bounds? CollisionBoundsXZ(Transform root)
        {
            var colRoot = root.Find(TrackRuntime.CollisionGroup);
            if (colRoot == null) return null;
            Bounds? result = null;
            foreach (var mc in colRoot.GetComponentsInChildren<MeshCollider>())
            {
                if (mc.sharedMesh == null) continue;
                var b = mc.sharedMesh.bounds;
                if (result is { } r)
                {
                    r.Encapsulate(b);
                    b = r;
                }
                result = b;
            }
            return result;
        }

        static bool IsOutside(Vector3 at, Bounds b) => at.x < b.min.x || at.x > b.max.x || at.z < b.min.z || at.z > b.max.z;

        static void BuildProp(TrackImportContext c, JToken p, Transform propRoot)
        {
            var file = ExportJson.Str(p["model"]);
            var m = c.Model(file, GlbLightingStyle.World);
            if (m == null) return;
            var inst = InstantiateProp(c, m, p, propRoot);
            var clipName = ExportJson.Str(p["animation"]);
            var clip = clipName != null && m.clips.TryGetValue(clipName, out var named) ? named : m.clips.Values.FirstOrDefault();
            var collides = ExportJson.B(p["collides"]);
            var dynamic = ExportJson.B(p["dynamic"]);
            var animated = clip != null && !dynamic ? AttachAnimator(inst, m, clip, p, collides) : null;
            if (animated == null) ImportHierarchy.StripAnimation(inst);
            if (ExportJson.B(p["invisible"]))
                foreach (var r in inst.GetComponentsInChildren<Renderer>(true)) Object.DestroyImmediate(r);
            if (ExportJson.Str(p["sound"]) is { } sound) TrackAudioBuilder.AddLoopSound(c, inst, sound, p);
            if (!collides) return;
            ImportHierarchy.SetLayer(inst, GameLayers.Track);
            if (dynamic) AddDynamicBody(c, inst);
            else if (animated == null) AddStaticMeshColliders(inst);
            else AddAnimatedColliders(c, inst, m, clip, p, animated);
        }

        static GameObject InstantiateProp(TrackImportContext c, GlbModel m, JToken p, Transform propRoot)
        {
            var file = ExportJson.Str(p["model"]);
            var inst = c.Instance(m, propRoot, Path.GetFileNameWithoutExtension(ExportJson.Str(p["name"]) ?? file));
            var t = inst.transform;
            t.SetLocalPositionAndRotation(ExportJson.Pos(p["pos"], c.Scale), ExportJson.Rot(p["rot"]));
            t.localScale = Vector3.Scale(t.localScale, ExportJson.Scale(p["scale"]));
            return inst;
        }

        static PropAnimator AttachAnimator(GameObject inst, GlbModel m, AnimationClip clip, JToken p, bool collides)
        {
            ImportHierarchy.GetOrAddComponent<Animator>(inst).runtimeAnimatorController = null;
            var animated = inst.AddComponent<PropAnimator>();
            animated.clip = clip;
            animated.alternates = collides ? Array.Empty<AnimationClip>() : m.clips.Values.Where(a => a != clip).ToArray();
            animated.pingPong = ExportJson.Str(p["post"]) == "oscillate";
            // Moving obstacles keep the original's timing, scenery may start anywhere in its loop.
            animated.randomizeStart = !collides && ExportJson.B(p["statist"]);
            return animated;
        }

        static void AddDynamicBody(TrackImportContext c, GameObject inst)
        {
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true)) mf.gameObject.AddComponent<BoxCollider>();
            var rb = inst.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.sleepThreshold = c.Settings.dynamicPropSleepThreshold;
        }

        static void AddStaticMeshColliders(GameObject inst)
        {
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
        }

        static void AddAnimatedColliders(TrackImportContext c, GameObject inst, GlbModel m, AnimationClip clip, JToken p, PropAnimator animated)
        {
            var col = p["collider"] as JObject;
            var (hazard, effectId) = ResolveHazard(c.Settings, p);
            var bodies = new List<Rigidbody>();
            foreach (var node in AnimatedNodes(inst.transform, m, clip))
            {
                var go = node.gameObject;
                AddShapeCollider(go, col);
                var body = go.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                bodies.Add(body);
                if (hazard == null || string.IsNullOrEmpty(effectId)) continue;
                var h = go.AddComponent<TrackHazard>();
                h.kind = hazard == "stomp" ? HazardKind.Stomp : HazardKind.Touch;
                h.effectId = effectId;
            }
            if (bodies.Count == 0) Debug.LogWarning($"[Import] {c.ShortName}: prop {inst.name} collides but its clip moves no node.");
            animated.bodies = bodies.ToArray();
        }

        static (string hazard, string effectId) ResolveHazard(ExportImportSettings s, JToken p)
        {
            var hazard = ExportJson.Str(p["hazard"]) ?? (ExportJson.B(p["crusher"]) ? "touch" : null);
            var effectId = s.crushEffectId;
            if (hazard == null && ExportJson.B(p["squash"]))
            {
                hazard = "touch";
                if (!string.IsNullOrEmpty(s.squashEffectId)) effectId = s.squashEffectId;
            }
            else if (hazard != null && s.crushersAsTriggers) hazard = "touch";
            else if (hazard == null && s.movingWallsCrush) hazard = "stomp";
            return (hazard, effectId);
        }

        // The original collision shape sits on the node's origin or the export's centre (AddComponent would fit it to the mesh).
        static void AddShapeCollider(GameObject go, JObject col)
        {
            var shape = ExportJson.Str(col?["shape"]);
            var center = ExportJson.Pos(col?["center"]);
            if (shape == "box" && col?["size"] is JArray { Count: 3 } size)
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.center = center;
                bc.size = ExportJson.Scale(size);
            }
            else if (shape == "sphere" && col?["radius"] != null)
            {
                var sc = go.AddComponent<SphereCollider>();
                sc.center = center;
                sc.radius = ExportJson.F(col["radius"]);
            }
            else
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                    go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
        }

        // The root never matches: it is the model's wrapper, renamed after the prop, often named like its single node.
        static List<Transform> AnimatedNodes(Transform root, GlbModel m, AnimationClip clip)
        {
            var result = new List<Transform>();
            if (m.json["animations"] is not JArray anims || m.json["nodes"] is not JArray nodes) return result;
            var name = m.clips.FirstOrDefault(kv => kv.Value == clip).Key;
            var anim = anims.OfType<JObject>().FirstOrDefault(a => ExportJson.Str(a["name"]) == name) ?? anims.OfType<JObject>().FirstOrDefault();
            foreach (var ch in ExportJson.Items(anim?["channels"]))
            {
                var ni = ExportJson.I(ch["target"]?["node"], -1);
                if (ni < 0 || ni >= nodes.Count) continue;
                var t = ImportHierarchy.FindBelow(root, ExportJson.Str(nodes[ni]["name"]));
                if (t != null && !result.Contains(t)) result.Add(t);
            }
            return result;
        }
    }
}
