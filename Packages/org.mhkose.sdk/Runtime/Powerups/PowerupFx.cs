using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;

namespace MHK.Powerups
{
    /// <summary>Fire-and-forget visual/sound effects and the shared parent of runtime powerup objects.</summary>
    public static partial class PowerupFx
    {
        public const string RootName = "Powerups";
        const float FxLifetime = 4f;

        [AutoStaticsCleanup] static Transform root;

        /// <summary>
        /// Parent of every runtime powerup object (use prefabs, shots, hazards, effects): a "Powerups" object at the
        /// root of the active track scene, created on first use and destroyed with the scene.
        /// </summary>
        public static Transform Root
        {
            get
            {
                if (root == null || root.gameObject.scene != SceneManager.GetActiveScene())
                    root = new GameObject(RootName).transform;
                return root;
            }
        }

        /// <summary>Instantiates <paramref name="prefab"/> under <see cref="Root"/> at a world pose.</summary>
        public static GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation) =>
            Object.Instantiate(prefab, position, rotation, Root);

        /// <summary>
        /// Instantiates a use prefab under <see cref="Root"/> and launches all its behaviours in component order (online
        /// they join their use first, <see cref="OnlineItems"/>).
        /// </summary>
        public static GameObject SpawnAndLaunch(GameObject prefab, Vector3 position, Quaternion rotation, in PowerupContext ctx)
        {
            var go = Instantiate(prefab, position, rotation);
            using (ListPool<PowerupBehaviour>.Get(out var behaviours))
            {
                go.GetComponents(behaviours);
                foreach (var b in behaviours) OnlineItems.Track(b, ctx);
                foreach (var b in behaviours) b.Launch(ctx);
            }
            return go;
        }

        /// <summary>
        /// Spawns <paramref name="prefab"/> (destroyed after 4 s) and plays <paramref name="sound"/> there (both optional),
        /// carrying as far as <paramref name="range"/> says (null = the default one-shot range). Returns the spawned
        /// object, null for none.
        /// </summary>
        public static GameObject Spawn(GameObject prefab, Vector3 position, AudioClip sound = null, float volume = 1f, Audio.SoundRange range = null)
        {
            var fx = prefab != null ? Instantiate(prefab, position, Quaternion.identity) : null;
            if (fx != null) Object.Destroy(fx, FxLifetime);
            Audio.AudioManager.PlaySfx(sound, position, volume, 1f, range);
            return fx;
        }

        /// <summary>
        /// Lets the particle systems below <paramref name="owner"/> outlive it. Each one is moved to <see cref="Root"/>
        /// where it is, stops emitting, and goes once its last particles have died. Call before removing the owner, so
        /// a flame or smoke dies away instead of vanishing.
        /// </summary>
        public static void LetParticlesFinish(GameObject owner)
        {
            using (ListPool<ParticleSystem>.Get(out var systems))
            {
                owner.GetComponentsInChildren(systems);
                foreach (var ps in systems)
                {
                    // Sub-emitters and nested systems go with the topmost system of their branch.
                    var parent = ps.transform.parent;
                    if (parent != null && parent.GetComponentInParent<ParticleSystem>() != null && parent.IsChildOf(owner.transform)) continue;
                    ps.transform.SetParent(Root, true);
                    var main = ps.main;
                    main.stopAction = ParticleSystemStopAction.Destroy;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }
    }
}
