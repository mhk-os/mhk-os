using MHK.Race;
using NUnit.Framework;
using UnityEngine;

namespace MHK.Tests
{
    public class RespawnGroundTests
    {
        const int GroundLayer = 31;
        static readonly Vector3 TestOrigin = new(10000f, 1000f, 10000f);
        GameObject root;
        TrackRuntime track;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Respawn ground test");
            root.transform.position = TestOrigin;
            track = root.AddComponent<TrackRuntime>();
            track.groundMask = 1 << GroundLayer;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [TestCase(0.2f, false)]
        [TestCase(5f, false)]
        [TestCase(0.2f, true)]
        [TestCase(5f, true)]
        public void MovingHazardChildIsSkippedForRoadBelow(float bottom, bool animatedWall)
        {
            var road = Box(root.transform, new Vector3(0f, -0.5f, 0f), new Vector3(10f, 1f, 10f));
            var prop = new GameObject("Moving prop");
            prop.transform.SetParent(root.transform, false);
            prop.transform.localPosition = Vector3.up * (bottom + 0.5f);
            var body = prop.AddComponent<Rigidbody>();
            body.isKinematic = true;
            if (animatedWall)
            {
                var animation = prop.AddComponent<PropAnimator>();
                animation.bodies = new[] { body };
            }
            else prop.AddComponent<TrackHazard>().kind = HazardKind.Stomp;
            var obstacle = Box(prop.transform, Vector3.zero, new Vector3(4f, 1f, 4f));
            // Keep it solid even if a lifecycle callback ran before the hazard kind was assigned.
            obstacle.isTrigger = false;
            Physics.SyncTransforms();

            var point = TestOrigin + Vector3.up * (bottom + 1f);
            Assert.IsTrue(GroundProbe.Down(point, 20f, 25f, track.groundMask, out var first));
            Assert.AreSame(obstacle, first.collider, "The moving body must be the nearest hit, not hidden by the fixture.");
            Assert.IsTrue(track.FindGridGround(point, out var ground));
            Assert.AreSame(road, ground.collider);
            Assert.AreEqual(TestOrigin.y, ground.point.y, 0.01f);
            Assert.IsFalse(obstacle.isTrigger, "Placement filtering must not change driving collisions.");
        }

        [TestCase(false, 0f)]
        [TestCase(true, 0f)]
        [TestCase(false, 20f)]
        public void StaticRoadBridgeAndRampRemainGround(bool kinematic, float slope)
        {
            var road = Box(root.transform, new Vector3(0f, -0.5f, 0f), new Vector3(10f, 1f, 10f));
            road.transform.localRotation = Quaternion.Euler(slope, 0f, 0f);
            road.gameObject.AddComponent<TrackSurface>();
            if (kinematic) road.gameObject.AddComponent<Rigidbody>().isKinematic = true;
            Physics.SyncTransforms();

            Assert.IsTrue(track.FindGridGround(TestOrigin, out var ground));
            Assert.AreSame(road, ground.collider);
        }

        static BoxCollider Box(Transform parent, Vector3 position, Vector3 size)
        {
            var obj = new GameObject("Ground collider");
            obj.layer = GroundLayer;
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            var collider = obj.AddComponent<BoxCollider>();
            collider.size = size;
            return collider;
        }
    }
}
