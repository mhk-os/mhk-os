using MHK.Core;
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
            /// <summary>The shaking engine part of the kart model. Null when the model has none.</summary>
            public readonly KartEngine engine;

            public Rig(KartModel model, CharacterAnimator driver, CharacterAnimator kart, KartEngine engine)
            {
                this.model = model;
                this.driver = driver;
                this.kart = kart;
                this.engine = engine;
            }
        }

        // Furthest a driver slides from the seat point to reach the steering wheel (m).
        const float MaxSlideForward = 0.18f, MaxSlideBack = 0.06f;

        /// <summary>
        /// Instantiates the kart model under <paramref name="parent"/> and seats the driver on its
        /// <see cref="KartModel.seat"/> (driver prefabs have their origin at the seat point). The driver slides along the
        /// seat until its hands reach the steering wheel (<see cref="CharacterDefinition.handReach"/>) and is shifted by
        /// its <see cref="CharacterDefinition.seatOffset"/>, with the animators set up. Without a kart model a driver that
        /// races without a kart (<see cref="CharacterDefinition.racesWithoutKart"/>) becomes the kart body itself. The
        /// engine part is sized to <paramref name="engineClass"/> (<see cref="KartEngine.Attach"/>).
        /// </summary>
        public static Rig Spawn(Transform parent, CharacterDefinition character, KartDefinition kartDef, Difficulty engineClass)
        {
            var model = SpawnKart(parent, kartDef, GameRoot.Instance.Config.GetDifficulty(engineClass), out var kartAnimator, out var engine);
            var seat = model != null && model.seat != null ? model.seat : parent;
            var bare = model == null && character != null && character.RacesBareOn(kartDef);
            var driver = SeatDriver(seat, character, bare ? character.noKartScale : character != null ? character.modelScale : 1f);
            if (driver != null)
            {
                var offset = bare ? character.noKartOffset : character.seatOffset + Vector3.forward * SlideToWheel(parent, model, seat, character);
                driver.transform.position += parent.TransformVector(offset);
            }
            return new Rig(model, driver, kartAnimator, engine);
        }

        // How far (m, forward in the kart's space) the driver moves from the seat point so its hands hold the wheel. Karts
        // of one game place their wheels differently (MHK Extra by up to 20 cm), and drivers reach differently far. The
        // slide stays short, since further forward the driver would leave the seat and further back it would sink into the
        // backrest.
        static float SlideToWheel(Transform parent, KartModel model, Transform seat, CharacterDefinition character)
        {
            var wheel = model != null ? model.SteeringWheel : null;
            if (wheel == null || character.handReach <= 0f) return 0f;
            var gap = parent.InverseTransformPoint(wheel.position).z - parent.InverseTransformPoint(seat.position).z - character.handReach;
            return Mathf.Clamp(gap, -MaxSlideBack, MaxSlideForward);
        }

        /// <summary>Local rotations of <paramref name="ts"/> as instantiated (identity for missing entries).</summary>
        public static Quaternion[] Rests(Transform[] ts)
        {
            var r = new Quaternion[ts.Length];
            for (var i = 0; i < ts.Length; i++) r[i] = ts[i] != null ? ts[i].localRotation : Quaternion.identity;
            return r;
        }

        static KartModel SpawnKart(Transform parent, KartDefinition kartDef, DifficultySettings engineClass, out CharacterAnimator kartAnimator, out KartEngine engine)
        {
            kartAnimator = null;
            engine = null;
            if (kartDef == null || kartDef.kartPrefab == null) return null;
            var k = Object.Instantiate(kartDef.kartPrefab, parent);
            k.transform.localScale *= kartDef.modelScale;
            var model = k.GetComponentInChildren<KartModel>();
            // Sized before the animator records the rest pose it restores after each cross-fade, or the engine would
            // grow back to its modelled size a moment after spawning.
            engine = KartEngine.Attach(model, engineClass);
            if (kartDef.animations != null && kartDef.animations.Count > 0)
            {
                var ka = k.GetComponentInChildren<Animator>();
                if (ka == null) ka = k.AddComponent<Animator>();
                kartAnimator = k.AddComponent<CharacterAnimator>();
                kartAnimator.Setup(kartDef.animations, ka);
            }
            return model;
        }

        static CharacterAnimator SeatDriver(Transform seat, CharacterDefinition character, float scale)
        {
            if (character == null || character.driverPrefab == null) return null;
            var d = Object.Instantiate(character.driverPrefab, seat);
            d.transform.localScale *= scale;
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
