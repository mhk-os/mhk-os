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

        /// <summary>Instantiates a use prefab under <see cref="Root"/> and launches all its behaviours in component order.</summary>
        public static GameObject SpawnAndLaunch(GameObject prefab, Vector3 position, Quaternion rotation, in PowerupContext ctx)
        {
            var go = Instantiate(prefab, position, rotation);
            using (ListPool<PowerupBehaviour>.Get(out var behaviours))
            {
                go.GetComponents(behaviours);
                foreach (var b in behaviours) b.Launch(ctx);
            }
            return go;
        }

        /// <summary>
        /// Spawns <paramref name="prefab"/> (destroyed after 4 s) and plays <paramref name="sound"/> there (both optional),
        /// carrying as far as <paramref name="range"/> says (null = the default one-shot range).
        /// </summary>
        public static void Spawn(GameObject prefab, Vector3 position, AudioClip sound = null, float volume = 1f, Audio.SoundRange range = null)
        {
            if (prefab != null) Object.Destroy(Instantiate(prefab, position, Quaternion.identity), FxLifetime);
            Audio.AudioManager.PlaySfx(sound, position, volume, 1f, range);
        }
    }
}
