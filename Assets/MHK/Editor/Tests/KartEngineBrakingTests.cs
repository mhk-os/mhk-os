using System.Collections.Generic;
using System.Reflection;
using MHK.Karts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MHK.Tests
{
    public class KartEngineBrakingTests
    {
        const string KartPrefab = "Assets/MHK/Prefabs/KartRoot.prefab";
        const string Profile = "Packages/org.mhkose.sdk/Settings/DefaultKartPhysics.asset";
        static readonly Vector3 TestOrigin = new(20000f, 500f, 20000f);
        readonly List<Object> created = new();
        SimulationMode previousMode;

        [SetUp]
        public void SetUp()
        {
            previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            var ground = new GameObject("Engine braking test ground");
            created.Add(ground);
            ground.transform.position = TestOrigin + Vector3.down * 0.5f;
            ground.AddComponent<BoxCollider>().size = new Vector3(400f, 1f, 400f);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created)
                if (o != null) Object.DestroyImmediate(o);
            created.Clear();
            Physics.simulationMode = previousMode;
        }

        // A kart left without input after stopping (in reverse once the brake key held it at a standstill, else in a
        // forward gear) and shoved along the track by another kart rolls out: the engine brakes it, never drives it on.
        [TestCase(true, 8f)]
        [TestCase(false, -15f)]
        public void ShovedIdleKartSlowsDown(bool stoppedWithBrake, float shove)
        {
            var kart = SpawnKart();
            Step(kart, default, 1f);
            if (stoppedWithBrake) Step(kart, new KartInput { brake = 1f }, 0.5f);
            Step(kart, default, 0.5f);
            Assert.AreEqual(stoppedWithBrake, kart.Reversing);

            kart.Body.linearVelocity = kart.transform.forward * shove;
            var fastest = 0f;
            var dt = Time.fixedDeltaTime;
            for (var t = 0f; t < 3f; t += dt)
            {
                Step(kart, default, dt);
                fastest = Mathf.Max(fastest, Mathf.Abs(kart.ForwardSpeed));
            }
            Assert.LessOrEqual(fastest, Mathf.Abs(shove) + 0.1f);
            Assert.Less(Mathf.Abs(kart.ForwardSpeed), Mathf.Abs(shove));
        }

        KartPhysics SpawnKart()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(KartPrefab).GetComponent<KartPhysics>();
            var go = new GameObject("Engine braking test kart") { layer = prefab.gameObject.layer };
            created.Add(go);
            go.AddComponent<Rigidbody>();
            var kart = go.AddComponent<KartPhysics>();
            // Edit mode runs no Awake: set the body up as a spawned kart does.
            typeof(KartPhysics).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(kart, null);
            kart.CopySettings(prefab);
            kart.Profile = AssetDatabase.LoadAssetAtPath<KartPhysicsProfile>(Profile);
            kart.PlaceOnGround(TestOrigin, Vector3.up, Vector3.forward);
            Physics.SyncTransforms();
            return kart;
        }

        static void Step(KartPhysics kart, KartInput input, float seconds)
        {
            var dt = Time.fixedDeltaTime;
            for (var t = 0f; t < seconds; t += dt)
            {
                kart.Step(input, dt);
                Physics.Simulate(dt);
            }
        }
    }
}
