using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Content fingerprint of a time trial: a track hash (driving data of the loaded scene) and a setup hash (race rules,
    /// engine class, kart physics, laps). Both hash an explicit list of gameplay values (GhostFingerprintValues.cs), so
    /// refactoring a serialized layout never changes them. Hex of the first 128 bits of SHA-256.
    /// </summary>
    public static partial class GhostFingerprint
    {
        /// <summary>First token of both hashes. Change it whenever a value list changes (that invalidates every ghost once).</summary>
        public const string Scheme = "fp3";

        /// <summary>Hash of the driving data of <paramref name="runtime"/> (the loaded track scene) and <paramref name="track"/>.</summary>
        public static string TrackHash(TrackDefinition track, TrackRuntime runtime)
        {
            using var b = new FingerprintBuilder();
            b.Add(Scheme);
            b.Add("track");
            AddTrackDefinition(b, track);
            if (runtime == null) return b.Finish();
            AddTrackRuntime(b, runtime);
            foreach (var root in runtime.gameObject.scene.GetRootGameObjects()) AddSceneRoot(b, root);
            return b.Finish();
        }

        /// <summary>Hash of the race rules, engine class <paramref name="engineClass"/>, kart <paramref name="kart"/> and lap count <paramref name="laps"/>.</summary>
        public static string SetupHash(Difficulty engineClass, KartDefinition kart, int laps)
        {
            var cfg = GameRoot.Instance.Config;
            using var b = new FingerprintBuilder();
            b.Add(Scheme);
            b.Add("setup");
            AddRaceRules(b, cfg);
            AddEngineClass(b, cfg, engineClass);
            AddKart(b, cfg, kart);
            b.Add(laps);
            return b.Finish();
        }

        static void AddTrackDefinition(FingerprintBuilder b, TrackDefinition track)
        {
            b.Add(track != null ? track.Id : "");
            if (track == null) return;
            b.Add(track.gravity);
            AddTrackPhysics(b, track.physics);
        }

        static void AddTrackRuntime(FingerprintBuilder b, TrackRuntime runtime)
        {
            b.Add(runtime.killHeight);
            b.Add(runtime.groundMask.value);
            b.Add(runtime.fallDepth);
            b.Add(runtime.fallMinAirTime);
            AddStartGrid(b, runtime);
            AddPath(b, runtime.path);
            foreach (var alt in runtime.alternatePaths) AddPath(b, alt);
        }

        // Per scene root, in this order: colliders, surfaces, zones, hazards, animated props.
        static void AddSceneRoot(FingerprintBuilder b, GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
                if (c.enabled && c.gameObject.activeInHierarchy) AddCollider(b, c);
            foreach (var s in root.GetComponentsInChildren<TrackSurface>(true)) AddSurface(b, s);
            foreach (var z in root.GetComponentsInChildren<TrackZone>(true)) AddZone(b, z);
            foreach (var h in root.GetComponentsInChildren<TrackHazard>(true)) AddHazard(b, h);
            foreach (var p in root.GetComponentsInChildren<PropAnimator>(true)) AddProp(b, p);
        }

        static void AddRaceRules(FingerprintBuilder b, GameConfig cfg)
        {
            b.Add(cfg.physicsHz);
            b.Add(cfg.gravity);
            b.Add(cfg.offroadResistance);
            b.Add(cfg.airHeadingAlign);
            b.Add(cfg.airSteerRate);
            b.Add(cfg.driveTorqueScale);
            b.Add(cfg.countdownSeconds);
            b.Add(cfg.powerStartWindow);
            b.Add(cfg.powerStartIgnoreAbove);
            AddEffect(b, cfg.powerStartEffect);
            AddEffect(b, cfg.powerFailEffect);
            AddEffect(b, cfg.dropEffect);
            b.Add(cfg.respawnBackDistance);
            b.Add(cfg.gapRunUp);
            b.Add(cfg.respawnDropHeight);
            b.Add(cfg.respawnOccupiedStep);
            b.Add(cfg.respawnClearRadius);
            b.Add(cfg.respawnProtectionSeconds);
        }

        static void AddEngineClass(FingerprintBuilder b, GameConfig cfg, Difficulty engineClass)
        {
            var diff = cfg.GetDifficulty(engineClass);
            b.Add(diff.engineClassFactor);
            b.Add(diff.topSpeedKmh);
            b.Add(cfg.SpeedScale(engineClass));
            AddProfile(b, cfg.defaultPhysics);
        }

        static void AddKart(FingerprintBuilder b, GameConfig cfg, KartDefinition kart)
        {
            b.Add(kart != null ? kart.Id : "");
            AddProfile(b, kart != null && kart.physics != null ? kart.physics : cfg.defaultPhysics);
            AddChassis(b, cfg.kartRootPrefab != null ? cfg.kartRootPrefab.GetComponent<KartPhysics>() : null);
        }

        // Moving objects (rigidbody, animation, spinner) have world bounds that depend on when the hash is taken: only their shape counts.
        static bool Moves(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
            {
                if (p.TryGetComponent<Rigidbody>(out _) || p.TryGetComponent<Animator>(out _) || p.TryGetComponent<Animation>(out _)
                    || p.TryGetComponent<PropAnimator>(out _) || p.TryGetComponent<Effects.Spinner>(out _)
                    || p.TryGetComponent<Effects.Oscillator>(out _))
                    return true;
            }
            return false;
        }
    }
}
