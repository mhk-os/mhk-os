using MHK.Modding;
using UnityEngine;

namespace MHK.Karts
{
    /// <summary>
    /// Builds a kart model with its seated driver and their animators. Races (<see cref="KartVisual"/>) and the menu
    /// showcase both build karts this way, so seat, scale and poses always match.
    /// </summary>
    public static class KartRigBuilder
    {
        /// <summary>A kart model with its seated driver, as built by <see cref="Spawn"/>.</summary>
        public readonly struct Rig
        {
            /// <summary>Wheels and seat of the kart model. Null without a kart prefab or KartModel.</summary>
            public readonly KartModel model;
            /// <summary>Plays the driver's clips. Null without a driver.</summary>
            public readonly CharacterAnimator driver;
            /// <summary>Plays the kart model's own clips. Null when the kart has none.</summary>
            public readonly CharacterAnimator kart;

            public Rig(KartModel model, CharacterAnimator driver, CharacterAnimator kart)
            {
                this.model = model;
                this.driver = driver;
                this.kart = kart;
            }
        }

        /// <summary>
        /// Instantiates the kart model under <paramref name="parent"/> and seats the driver on its
        /// <see cref="KartModel.seat"/> (driver prefabs have their origin at the seat point), with the animators set up.
        /// </summary>
        public static Rig Spawn(Transform parent, CharacterDefinition character, KartDefinition kartDef)
        {
            var model = SpawnKart(parent, kartDef, out var kartAnimator);
            var seat = model != null && model.seat != null ? model.seat : parent;
            var driver = SeatDriver(seat, character);
            return new Rig(model, driver, kartAnimator);
        }

        /// <summary>Local rotations of <paramref name="ts"/> as instantiated (identity for missing entries).</summary>
        public static Quaternion[] Rests(Transform[] ts)
        {
            var r = new Quaternion[ts.Length];
            for (var i = 0; i < ts.Length; i++) r[i] = ts[i] != null ? ts[i].localRotation : Quaternion.identity;
            return r;
        }

        static KartModel SpawnKart(Transform parent, KartDefinition kartDef, out CharacterAnimator kartAnimator)
        {
            kartAnimator = null;
            if (kartDef == null || kartDef.kartPrefab == null) return null;
            var k = Object.Instantiate(kartDef.kartPrefab, parent);
            k.transform.localScale *= kartDef.modelScale;
            var model = k.GetComponentInChildren<KartModel>();
            if (kartDef.animations != null && kartDef.animations.Count > 0)
            {
                var ka = k.GetComponentInChildren<Animator>();
                if (ka == null) ka = k.AddComponent<Animator>();
                kartAnimator = k.AddComponent<CharacterAnimator>();
                kartAnimator.Setup(kartDef.animations, ka);
            }
            return model;
        }

        static CharacterAnimator SeatDriver(Transform seat, CharacterDefinition character)
        {
            if (character == null || character.driverPrefab == null) return null;
            var d = Object.Instantiate(character.driverPrefab, seat);
            d.transform.localScale *= character.modelScale;
            var anim = d.GetComponentInChildren<Animator>();
            if (anim == null) anim = d.AddComponent<Animator>();
            anim.applyRootMotion = false;
            // Legacy Animation components on the model would fight the Playables graph.
            foreach (var legacy in d.GetComponentsInChildren<Animation>()) legacy.enabled = false;
            var driver = d.AddComponent<CharacterAnimator>();
            driver.Setup(character.animations, anim);
            return driver;
        }
    }
}
