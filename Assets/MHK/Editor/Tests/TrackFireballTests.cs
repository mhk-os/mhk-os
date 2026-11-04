using System.Collections.Generic;
using System.Reflection;
using MHK.Karts;
using MHK.Powerups;
using MHK.Race;
using NUnit.Framework;
using UnityEngine;

namespace MHK.Tests
{
    public class TrackFireballTests
    {
        const int GroundLayer = 31;
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        // The solver uses millimetre probes: isolate by height without losing horizontal float precision.
        static readonly Vector3 Origin = new(0f, 1000f, 0f);
        readonly List<Kart> karts = new();
        GameObject root;
        TrackRuntime track;
        TrackPath main;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Routed fireball test");
            root.transform.position = Origin;
            track = root.AddComponent<TrackRuntime>();
            Invoke(track, "Awake");
            track.groundMask = 1 << GroundLayer;
            main = Path(true, new Vector3(0, 0, 0), new Vector3(0, 0, 100),
                new Vector3(100, 0, 100), new Vector3(100, 0, 0));
            Box(new Vector3(50, -0.5f, 50), new Vector3(250, 1, 250));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var kart in karts) Invoke(kart, "OnDisable");
            karts.Clear();
            Invoke(track, "OnDestroy");
            Object.DestroyImmediate(root);
        }

        [Test]
        public void LookBackAndWrongWayFacingCannotReverseTheFireball()
        {
            var owner = KartAt(new Vector3(0, 0, 35), 2);
            owner.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            KartAt(new Vector3(0, 0, 90), 1);
            var fireball = Launch(owner, true);
            Assert.Greater(fireball.transform.position.z, owner.transform.position.z);
            Tick(fireball);
            Assert.Greater(fireball.NetVelocity.z, 0f);
        }

        [Test]
        public void OwnerIsTheTargetWhenLeadingAndRaceOrderChangesRetargetImmediately()
        {
            var owner = KartAt(new Vector3(0, 0, 5), 1);
            var other = KartAt(new Vector3(0, 0, 70), 2);
            var fireball = Launch(owner);
            Assert.AreSame(owner, fireball.ChosenTarget);
            Rank(owner, 2);
            Rank(other, 1);
            Tick(fireball);
            Assert.AreSame(other, fireball.ChosenTarget, "Not next-ahead, nearest or the launch-time leader.");
            other.enabled = false;
            Tick(fireball);
            Assert.AreSame(owner, fireball.ChosenTarget, "Inactive racers cannot remain the target.");
        }

        [Test]
        public void LeadingLauncherGetsForwardFlightBeforeSelfTargeting()
        {
            var owner = KartAt(new Vector3(0, 0, 10), 1);
            KartAt(new Vector3(0, 0, 90), 2);
            var fireball = Launch(owner, selfTargetDelay: 1f);
            fireball.speed = 36.125f;
            Assert.IsNull(fireball.ChosenTarget);
            var start = fireball.NetPosition;
            var dt = Time.fixedDeltaTime;
            var graceTicks = Mathf.FloorToInt(0.75f / dt);
            for (var i = 0; i < graceTicks; i++)
            {
                Tick(fireball);
                Assert.IsNull(fireball.ChosenTarget, "Release must not immediately lock onto the leading launcher.");
            }
            Assert.Greater(fireball.NetPosition.z - start.z, 20f, "The launch window drives forwards instead of waiting ahead.");
            for (var i = graceTicks; i < Mathf.CeilToInt(1.1f / dt); i++) Tick(fireball);
            Assert.AreSame(owner, fireball.ChosenTarget, "A leading launcher becomes eligible after the short launch window.");
        }

        [Test]
        public void LaunchWindowDelaysOnlyTheOwnerAndAdaptsToRankChanges()
        {
            var owner = KartAt(new Vector3(0, 0, 10), 2);
            var other = KartAt(new Vector3(0, 0, 90), 1);
            var fireball = Launch(owner, selfTargetDelay: 1f);
            Assert.AreSame(other, fireball.ChosenTarget, "Launching must not delay pursuit of another leader.");
            Rank(owner, 1);
            Rank(other, 2);
            Tick(fireball);
            Assert.IsNull(fireball.ChosenTarget, "Becoming leader during the launch window does not cancel its protection.");
            Rank(owner, 2);
            Rank(other, 1);
            Tick(fireball);
            Assert.AreSame(other, fireball.ChosenTarget, "Another leader is immediately eligible during that same window.");
        }

        [Test]
        public void BystandersInTheSweptFlightPathDoNotReplaceOrAbsorbTheLeader()
        {
            var owner = KartAt(new Vector3(0, 0, 35), 3);
            var leader = KartAt(new Vector3(0, 0, 90), 1);
            var bystander = KartAt(new Vector3(0, 0, 40), 2);
            var fireball = Launch(owner);
            var passed = bystander.transform.position.z + fireball.hitRadius;
            for (var i = 0; i < 60 && fireball.gameObject.activeSelf && fireball.NetPosition.z <= passed; i++) Tick(fireball);
            Assert.IsTrue(fireball.gameObject.activeSelf);
            Assert.AreSame(leader, fireball.ChosenTarget);
            Assert.Greater(fireball.NetPosition.z, passed, "Traverse the bystander's whole contact radius without consuming the shot.");
        }

        [Test]
        public void LaunchInsideAlternativeRouteProjectsOntoThatRoute()
        {
            var branch = Path(false, new Vector3(0, 0, 20), new Vector3(20, 0, 40), new Vector3(0, 0, 70));
            var owner = KartAt(new Vector3(20, 0, 40), 2);
            KartAt(new Vector3(80, 0, 100), 1);
            var fireball = Launch(owner);
            Assert.AreSame(branch, owner.Progress.CurrentBranch);
            Assert.AreSame(branch, fireball.FollowedPath);
            Assert.AreEqual(branch.Project(owner.transform.position), fireball.FollowedDistance, 0.1f);
        }

        [Test]
        public void LeaderOnLongerBranchIsPursuedThroughItsForkNotAcrossTerrain()
        {
            var branch = Path(false, new Vector3(0, 0, 30), new Vector3(20, 0, 50), new Vector3(0, 0, 80));
            var owner = KartAt(new Vector3(0, 0, 5), 2);
            var leader = KartAt(new Vector3(20, 0, 50), 1);
            var fireball = Launch(owner);
            Tick(fireball);
            Assert.AreSame(branch, leader.Progress.CurrentBranch);
            Assert.AreSame(branch, fireball.FollowedPath);
            Assert.Less(fireball.FollowedDistance, 0f, "Commit to the fork's approach before entering the route.");
            Assert.Less(fireball.transform.position.x - Origin.x, 3f,
                "Do not home diagonally across terrain at the leader already inside the branch.");
        }

        [Test]
        public void ShorterDrivableBranchIsChosenAndRejoinsMainWithoutTeleporting()
        {
            var branch = Path(false, new Vector3(0, 0, 80), new Vector3(20, 0, 100));
            var owner = KartAt(new Vector3(0, 0, 55), 2);
            KartAt(new Vector3(100, 0, 30), 1);
            var fireball = Launch(owner);
            Tick(fireball);
            Assert.AreSame(branch, fireball.FollowedPath);
            var entered = false;
            var merged = false;
            for (var i = 0; i < 150; i++)
            {
                var previous = fireball.transform.position;
                Tick(fireball);
                Assert.LessOrEqual(Vector3.Distance(previous, fireball.transform.position), fireball.speed * Time.fixedDeltaTime + 0.02f);
                entered |= fireball.FollowedPath == branch && fireball.FollowedDistance > 2f;
                if (entered && fireball.FollowedPath == main) { merged = true; break; }
            }
            Assert.IsTrue(entered, "Must actually enter the shortcut, not merely aim at its exit.");
            Assert.IsTrue(merged, "The route must hand navigation back to the main path at its merge.");
        }

        [Test]
        public void CommittedCornerEntryAdvancesOntoBranchBeforeMainForkProjection()
        {
            var branch = Path(false, new Vector3(0, 0, 50), new Vector3(100, 0, 50));
            var owner = KartAt(new Vector3(0, 0, 25), 2);
            KartAt(new Vector3(100, 0, 5), 1);
            var fireball = Launch(owner);
            Tick(fireball);
            fireball.Correct(Origin + new Vector3(4f, 1.2f, 46.5f), Vector3.right * fireball.speed);
            var enteredBeforeFork = false;
            for (var i = 0; i < 120; i++)
            {
                Tick(fireball);
                enteredBeforeFork |= fireball.FollowedPath == branch && fireball.FollowedDistance > 0.5f
                                     && main.Project(fireball.NetPosition) < 49.5f;
                if (fireball.FollowedPath == branch && fireball.FollowedDistance > 30f) break;
            }
            Assert.IsTrue(enteredBeforeFork, "The committed branch is reachable before crossing the raw main fork.");
            Assert.AreSame(branch, fireball.FollowedPath);
            Assert.Greater(fireball.FollowedDistance, 30f, "Do not orbit the approach's look-ahead point.");
        }

        [Test]
        public void CornerExitRejoinsMainBeforeRawBranchEndpoint()
        {
            var branch = Path(false, new Vector3(0, 0, 50), new Vector3(100, 0, 50));
            var owner = KartAt(new Vector3(75, 0, 50), 2);
            KartAt(new Vector3(100, 0, 5), 1);
            var fireball = Launch(owner);
            fireball.Correct(Origin + new Vector3(96.5f, 1.2f, 46f), Vector3.back * fireball.speed);
            for (var i = 0; i < 90 && fireball.FollowedPath != main; i++) Tick(fireball);
            Assert.AreSame(main, fireball.FollowedPath, "The solved exit must not wait at the raw branch endpoint.");
            Assert.Greater(main.Delta(track.BranchJoinEnd(0), fireball.FollowedDistance), 0f);
            Assert.Less(branch.Project(fireball.NetPosition), branch.Length - 0.5f,
                "The main road is already reached before the raw branch projection crosses its endpoint.");
        }

        [Test]
        public void SmoothedLunaKinksDoNotOrbitTheRawLookAheadPoint()
        {
            // Luna nodes 126-130, translated onto an isolated flat road with a return loop.
            main.SetNodes(new[]
            {
                new TrackPath.Node { position = new Vector3(100f, 0, 50f), width = 11.83741f },
                new TrackPath.Node { position = new Vector3(92.90473f, 0, 51.0959f), width = 18.85494f },
                new TrackPath.Node { position = new Vector3(84.95731f, 0, 47.6529f), width = 19.57404f },
                new TrackPath.Node { position = new Vector3(76.30126f, 0, 50.1465f), width = 14.84983f },
                new TrackPath.Node { position = new Vector3(67.4374f, 0, 49.3108f), width = 15.46578f },
                new TrackPath.Node { position = new Vector3(20f, 0, 50f), width = 16f },
                new TrackPath.Node { position = new Vector3(20f, 0, -40f), width = 16f },
                new TrackPath.Node { position = new Vector3(140f, 0, -40f), width = 16f },
                new TrackPath.Node { position = new Vector3(140f, 0, 50f), width = 16f }
            });
            track.InvalidateLines();
            var owner = KartAt(new Vector3(100f, 0, 50f), 2);
            var leader = KartAt(new Vector3(20f, 0, 20f), 1);
            leader.Effects.ProtectedUntilTick = int.MaxValue;
            var fireball = Launch(owner);
            fireball.speed = 36.125f;
            var travelled = 0f;
            for (var tick = 0; tick < 1600 && travelled < main.Length; tick++)
            {
                var previous = fireball.FollowedDistance;
                Tick(fireball);
                travelled += main.Delta(previous, fireball.FollowedDistance);
            }
            Assert.GreaterOrEqual(travelled, main.Length, "Round the real kink geometry and complete a lap, not orbit its steering target.");
            Assert.IsTrue(fireball.gameObject.activeSelf);
        }

        [Test]
        public void FullSpeedWallContactSlidesWithoutTunnellingOrDestroyingTheFireball()
        {
            var owner = KartAt(new Vector3(0, 0, 0), 2);
            KartAt(new Vector3(0, 0, 90), 1);
            var fireball = Launch(owner);
            Tick(fireball); // Prepare the driving line before a new obstacle appears.
            var wall = Box(new Vector3(0, 2, 6), new Vector3(200, 4, 0.1f));
            Physics.SyncTransforms();
            for (var i = 0; i < 40; i++)
            {
                Tick(fireball);
                Assert.LessOrEqual(fireball.transform.position.z, wall.bounds.min.z - 0.18f);
                Assert.IsTrue(fireball.gameObject.activeSelf, "Wall contact is not an explosion or despawn.");
            }
            Assert.Greater(Mathf.Abs(fireball.NetVelocity.x), 0f, "Contact must steer/slide, not freeze permanently.");
        }

        [Test]
        public void CorrectionAndContinuedFlightCannotCrossWallWhileLeaderRespawns()
        {
            var owner = KartAt(new Vector3(0, 0, 0), 2);
            var leader = KartAt(new Vector3(0, 0, 90), 1);
            var fireball = Launch(owner);
            var wall = Box(new Vector3(0, 2, 4), new Vector3(200, 4, 0.1f));
            Physics.SyncTransforms();
            leader.Effects.ProtectedUntilTick = int.MaxValue;
            fireball.Correct(Origin + new Vector3(0, 1.2f, 30), Vector3.forward * fireball.speed);
            for (var i = 0; i < 20; i++) Tick(fireball);
            Assert.LessOrEqual(fireball.NetPosition.z, wall.bounds.min.z - 0.18f);
            Assert.IsTrue(fireball.gameObject.activeSelf);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnavailableLeaderDoesNotStopTravelOrConsumeTheFireball(bool pendingRespawn)
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0, 0, 10), 1);
            MHK.Modding.StatusEffectDefinition death = null;
            try
            {
                if (pendingRespawn)
                {
                    death = ScriptableObject.CreateInstance<MHK.Modding.StatusEffectDefinition>();
                    death.duration = 10f;
                    death.hostile = false;
                    death.respawnOnEnd = true;
                    leader.Effects.Apply(death);
                }
                else leader.Effects.ProtectedUntilTick = int.MaxValue;
                var fireball = Launch(owner);
                for (var i = 0; i < 20; i++) Tick(fireball);
                Assert.Greater(fireball.NetPosition.z, leader.transform.position.z + 3f);
                Assert.AreSame(leader, fireball.ChosenTarget);
                Assert.IsTrue(fireball.gameObject.activeSelf, "Passing the unavailable leader must not consume it.");
            }
            finally
            {
                leader.Effects.ClearAll();
                if (death != null) Object.DestroyImmediate(death);
            }
        }

        [Test]
        public void AheadWaitingStepsForwardAndAcceleratesWhenTheLeaderApproaches()
        {
            var owner = KartAt(new Vector3(0, 0, 35), 2);
            var leader = KartAt(Vector3.zero, 1);
            var profile = CruisingProfile(leader);
            try
            {
                var fireball = Launch(owner);
                var start = fireball.NetPosition;
                var sawPause = false;
                var sawStep = false;
                var ticks = Mathf.CeilToInt(1f / Time.fixedDeltaTime);
                for (var i = 0; i < ticks; i++)
                {
                    Tick(fireball);
                    sawPause |= fireball.NetVelocity == Vector3.zero;
                    sawStep |= fireball.NetVelocity.z > 0f;
                }
                var farTravel = fireball.NetPosition.z - start.z;
                Assert.IsTrue(sawPause && sawStep, "Waiting consists of forward increments separated by pauses.");
                Move(leader, fireball.FollowedDistance - 2.5f, 0f);
                var nearStart = fireball.NetPosition;
                for (var i = 0; i < ticks; i++) Tick(fireball);
                Assert.Greater(fireball.NetPosition.z - nearStart.z, farTravel,
                    "The same waiting interval advances further when the leader is nearby.");
                leader.Effects.ProtectedUntilTick = int.MaxValue;
                Tick(fireball);
                Assert.Greater(fireball.NetVelocity.magnitude, leader.NormalTopSpeed,
                    "Respawn protection resumes full route traversal instead of the waiting steps.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [TestCase(8f)]
        [TestCase(1.2f)]
        public void ClosePursuitMovesAcrossBothSidesOfCruisingLeaderWithinTheRoad(float roadWidth)
        {
            var nodes = new TrackPath.Node[main.Nodes.Count];
            for (var i = 0; i < nodes.Length; i++)
            {
                nodes[i] = main.Nodes[i];
                nodes[i].width = roadWidth;
            }
            main.SetNodes(nodes);
            track.InvalidateLines();
            var owner = KartAt(new Vector3(0, 0, 10), 2);
            var leader = KartAt(new Vector3(0, 0, 13), 1);
            var profile = CruisingProfile(leader);
            try
            {
                var fireball = Launch(owner);
                fireball.speed = 36.125f;
                fireball.hitRadius = 0f;
                var distance = 13f;
                var cruise = leader.NormalTopSpeed;
                var dt = Time.fixedDeltaTime;
                var left = float.PositiveInfinity;
                var right = float.NegativeInfinity;
                for (var i = 0; i < Mathf.CeilToInt(2.8f / dt); i++)
                {
                    distance += cruise * dt;
                    Move(leader, distance, cruise);
                    var previous = fireball.NetPosition;
                    Tick(fireball);
                    var lateral = Vector3.Dot(fireball.NetPosition - leader.transform.position, leader.transform.right);
                    left = Mathf.Min(left, lateral);
                    right = Mathf.Max(right, lateral);
                    Assert.LessOrEqual(Mathf.Abs(lateral), 1f, "The visible weave stays mild.");
                    var lane = main.GetRoadLane(fireball.NetPosition, fireball.FollowedDistance,
                        main.GetWidth(fireball.FollowedDistance));
                    Assert.That(lane, Is.InRange(0f, 1f), "The physical projectile stays inside the road, including on a narrow track.");
                    Assert.LessOrEqual(Vector3.Distance(previous, fireball.NetPosition), fireball.speed * dt + 0.02f);
                }
                Assert.Less(left, -0.15f, "The moving projectile must visibly pass to the leader's left.");
                Assert.Greater(right, 0.15f, "The moving projectile must visibly pass to the leader's right.");
                Assert.Greater(fireball.NetPosition.z, 50f, "Side changes happen during forward pursuit, not stationary oscillation.");
                Assert.Less(fireball.NetPosition.z, leader.transform.position.z, "Cruising pursuit remains behind the leader.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void ClosePursuitWeavesAgainstASolidSideWallWithoutCrossingIt()
        {
            var owner = KartAt(new Vector3(0, 0, 10), 2);
            var leader = KartAt(new Vector3(0, 0, 16), 1);
            var profile = CruisingProfile(leader);
            try
            {
                var fireball = Launch(owner);
                fireball.speed = 36.125f;
                fireball.hitRadius = 0f;
                Tick(fireball);
                var wall = Box(new Vector3(0.4f, 2f, 50f), new Vector3(0.04f, 4f, 140f));
                Physics.SyncTransforms();
                var start = fireball.NetPosition;
                var distance = 16f;
                var cruise = leader.NormalTopSpeed;
                var dt = Time.fixedDeltaTime;
                var nearest = float.NegativeInfinity;
                var left = float.PositiveInfinity;
                for (var i = 0; i < Mathf.CeilToInt(2.8f / dt); i++)
                {
                    distance += cruise * dt;
                    Move(leader, distance, cruise);
                    Tick(fireball);
                    nearest = Mathf.Max(nearest, fireball.NetPosition.x);
                    left = Mathf.Min(left, fireball.NetPosition.x - leader.transform.position.x);
                    Assert.LessOrEqual(fireball.NetPosition.x, wall.bounds.min.x - 0.18f,
                        "Every weaving tick must keep the projectile's collision volume on the original side of the thin wall.");
                    Assert.IsTrue(fireball.gameObject.activeSelf);
                }
                Assert.Greater(nearest, wall.bounds.min.x - 0.27f, "The weave must actually approach the wall, not avoid exercising collision.");
                Assert.Less(left, -0.15f, "A wall on one side must not suppress the clear half of the weave.");
                Assert.Greater(fireball.NetPosition.z - start.z, 50f, "Wall contact must allow continued forward pursuit.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [TestCase(0f, -2f, 0f, -1f)]
        [TestCase(1.25f, 0.25f, 1f, 0f)]
        public void LeaderContactRecognizesBodyTouchesOutsideTheRootRadius(
            float x, float z, float outwardX, float outwardZ)
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0f, 0f, 20f), 1);
            var profile = ContactProfile(leader);
            try
            {
                var fireball = Launch(owner);
                fireball.hitRadius = 0.3f;
                var touch = leader.transform.TransformPoint(new Vector3(x, 1.2f, z));
                var clear = touch + new Vector3(outwardX, 0f, outwardZ) * 0.1f;
                Physics.SyncTransforms();

                Assert.Greater(Vector3.Distance(touch, leader.transform.position), fireball.hitRadius,
                    "The rear/side body touch must exercise a hit the old root-centre test missed.");
                Assert.That(Vector3.Distance(touch, leader.Physics.Chassis.ClosestPoint(touch)),
                    Is.EqualTo(0.25f).Within(0.001f));
                Assert.IsFalse(Contact(fireball, leader, clear, clear),
                    "Being near the chassis is not enough when the whole sphere is clear.");
                Assert.IsTrue(Contact(fireball, leader, touch, touch),
                    "A stationary sphere touching the locked leader's body hits immediately.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void LeaderContactSweepsThroughTheChassisWhenBothEndpointsMiss()
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0f, 0f, 20f), 1);
            var profile = ContactProfile(leader);
            try
            {
                var fireball = Launch(owner);
                fireball.hitRadius = 0.3f;
                var from = leader.transform.TransformPoint(new Vector3(-3f, 1.2f, 0.25f));
                var to = leader.transform.TransformPoint(new Vector3(3f, 1.2f, 0.25f));
                Physics.SyncTransforms();

                Assert.IsFalse(Contact(fireball, leader, from, from));
                Assert.IsFalse(Contact(fireball, leader, to, to));
                Assert.IsTrue(Contact(fireball, leader, from, to),
                    "A full-step crossing cannot tunnel through the leader between clear endpoints.");
                Assert.IsTrue(Contact(fireball, leader, to, from),
                    "Swept body contact must not depend on which side the projectile approaches.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void LeaderContactUsesTheRotatedAndScaledBodyRatherThanItsWorldBounds()
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0f, 0f, 20f), 1);
            var profile = ContactProfile(leader);
            try
            {
                leader.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
                leader.transform.localScale = new Vector3(1.5f, 0.8f, 0.6f);
                var fireball = Launch(owner);
                fireball.hitRadius = 0.3f;
                var touch = leader.transform.TransformPoint(new Vector3(1.15f, 1.2f, 0.25f));
                var clear = leader.transform.TransformPoint(new Vector3(1.25f, 1.2f, 0.25f));
                Physics.SyncTransforms();

                Assert.That(Vector3.Distance(touch, leader.Physics.Chassis.ClosestPoint(touch)),
                    Is.EqualTo(0.225f).Within(0.001f));
                Assert.IsTrue(leader.Physics.Chassis.bounds.Contains(clear),
                    "The clear control must lie inside the enclosing AABB, but outside the actual oriented box.");
                Assert.IsFalse(Contact(fireball, leader, clear, clear),
                    "World bounds cannot stand in for the rotated chassis.");
                Assert.IsTrue(Contact(fireball, leader, touch, touch),
                    "Body contacts follow both rotation and nonuniform scale.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void LeaderContactCannotReachTheChassisThroughAnInterveningSolidWall()
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0f, 0f, 20f), 1);
            var profile = ContactProfile(leader);
            try
            {
                var fireball = Launch(owner);
                fireball.hitRadius = 0.3f;
                var from = leader.transform.TransformPoint(new Vector3(0f, 1.2f, -5f));
                var to = leader.transform.TransformPoint(new Vector3(0f, 1.2f, -2f));
                Physics.SyncTransforms();
                Assert.IsTrue(Contact(fireball, leader, from, to),
                    "The unobstructed sweep reaches the rear chassis.");

                var wall = Box(new Vector3(0f, 1.2f, 17f), new Vector3(4f, 3f, 0.1f));
                Physics.SyncTransforms();
                Assert.IsFalse(Contact(fireball, leader, from, to),
                    "A solid wall before body contact blocks the hit, even though the capsule reaches the chassis.");

                wall.enabled = false;
                Physics.SyncTransforms();
                Assert.IsTrue(Contact(fireball, leader, from, to),
                    "Removing the obstruction restores the same body contact.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void ClosePursuitReachesContactRangeAfterOneBriefPartialSlowdown()
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0, 0, 20), 1);
            var profile = CruisingProfile(leader);
            try
            {
                var fireball = Launch(owner);
                fireball.speed = 36.125f;
                // Measure pursuit independently of the runtime-only Destroy/boiling lifecycle.
                var contactRadius = fireball.hitRadius;
                fireball.hitRadius = 0f;
                var distance = 20f;
                var cruise = leader.NormalTopSpeed;
                var dt = Time.fixedDeltaTime;
                for (var i = 0; i < Mathf.CeilToInt(2f / dt); i++)
                {
                    distance += cruise * dt;
                    Move(leader, distance, cruise);
                    Tick(fireball);
                    Assert.Greater(ChassisDistance(leader, fireball.NetPosition), contactRadius,
                        "Every normal cruising step must remain clear of the actual chassis.");
                }
                Assert.Greater(ChassisDistance(leader, fireball.NetPosition),
                    contactRadius, "Cruising normally is not an unavoidable hit.");
                var closest = float.PositiveInfinity;
                for (var i = 0; i < Mathf.CeilToInt(0.4f / dt); i++)
                {
                    distance += cruise * 0.8f * dt;
                    Move(leader, distance, cruise * 0.8f);
                    Tick(fireball);
                    closest = Mathf.Min(closest, ChassisDistance(leader, fireball.NetPosition));
                }
                Assert.Less(closest, contactRadius,
                    "A 20% slowdown for 0.4 seconds reaches contact range without stopping the leader.");
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void AheadWaitingStepsRemainBlockedBySolidWalls()
        {
            var owner = KartAt(new Vector3(0, 0, 35), 2);
            KartAt(Vector3.zero, 1);
            var fireball = Launch(owner);
            var wall = Box(new Vector3(0, 2, 38), new Vector3(200, 4, 0.1f));
            Physics.SyncTransforms();
            for (var i = 0; i < Mathf.CeilToInt(8f / Time.fixedDeltaTime); i++)
            {
                Tick(fireball);
                Assert.LessOrEqual(fireball.NetPosition.z, wall.bounds.min.z - 0.18f);
            }
            Assert.IsTrue(fireball.gameObject.activeSelf);
        }

        [Test]
        public void MissingLeaderKeepsFollowingTrack()
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0, 0, 90), 1);
            var fireball = Launch(owner);
            owner.enabled = false;
            leader.enabled = false;
            var before = fireball.NetPosition;
            for (var i = 0; i < 10; i++) Tick(fireball);
            Assert.IsNull(fireball.ChosenTarget);
            Assert.Greater(fireball.NetPosition.z, before.z + 5f);
        }

        TrackPath Path(bool closed, params Vector3[] positions)
        {
            var go = new GameObject(closed ? "Main" : "Route");
            go.transform.SetParent(root.transform, false);
            var path = go.AddComponent<TrackPath>();
            path.closed = closed;
            var nodes = new TrackPath.Node[positions.Length];
            for (var i = 0; i < positions.Length; i++) nodes[i] = new TrackPath.Node { position = positions[i], width = 8f };
            path.SetNodes(nodes);
            track.InvalidateLines();
            return path;
        }

        Kart KartAt(Vector3 position, int rank)
        {
            var go = new GameObject("Racer");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
            var kart = go.AddComponent<Kart>();
            Invoke(kart, "Awake");
            Invoke(kart.Physics, "Awake");
            Invoke(kart.Effects, "Awake");
            Invoke(kart.Progress, "Awake");
            Invoke(kart, "OnEnable");
            karts.Add(kart);
            kart.Progress.Begin(track, 3);
            // These fixtures are racing on lap one, not the grid's nearest side of the start/finish line.
            kart.Progress.ApplyRemote(1, kart.Progress.PathDistance);
            Rank(kart, rank);
            return kart;
        }

        static KartPhysicsProfile CruisingProfile(Kart kart)
        {
            kart.gameObject.layer = MHK.Core.GameLayers.Kart;
            var profile = ScriptableObject.CreateInstance<KartPhysicsProfile>();
            kart.Physics.Profile = profile;
            kart.Physics.SpeedScale = KartPhysics.SpeedScaleFor(profile, kart.Physics.ClassFactor, 26f, 1f);
            return profile;
        }

        static KartPhysicsProfile ContactProfile(Kart kart)
        {
            kart.gameObject.layer = MHK.Core.GameLayers.Kart;
            var profile = ScriptableObject.CreateInstance<KartPhysicsProfile>();
            // Profile axes are forward/up/side; the resulting box is 2m wide and 4m long.
            profile.chassisSize = new Vector3(2f, 0.5f, 1f);
            profile.chassisCollOffset = new Vector3(0.25f, 1.2f, 0f);
            kart.Physics.Profile = profile;
            return profile;
        }

        static float ChassisDistance(Kart kart, Vector3 position) =>
            Vector3.Distance(position, kart.Physics.Chassis.ClosestPoint(position));

        static bool Contact(TrackFireball fireball, Kart target, Vector3 from, Vector3 to)
        {
            var method = typeof(TrackFireball).GetMethod("LeaderContact", PrivateInstance);
            Assert.IsNotNull(method, "TrackFireball must expose its side-effect-free private contact query.");
            return (bool)method.Invoke(fireball, new object[] { target, from, to, Vector3.zero });
        }

        void Move(Kart kart, float distance, float speed)
        {
            // Keep the profile-backed chassis above the road, as in an actual race.
            kart.transform.SetPositionAndRotation(main.GetPoint(distance) + Vector3.up * 0.4f,
                Quaternion.LookRotation(main.GetFlatTangent(distance)));
            kart.Physics.Body.position = kart.transform.position;
            kart.Physics.Body.rotation = kart.transform.rotation;
            kart.Physics.Body.linearVelocity = kart.transform.forward * speed;
            kart.Progress.Begin(track, 3);
            kart.Progress.ApplyRemote(1, distance);
            Physics.SyncTransforms();
        }

        static void Rank(Kart kart, int rank) => typeof(RacerProgress).GetProperty("Position").SetValue(kart.Progress, rank);

        TrackFireball Launch(Kart owner, bool backwards = false, float selfTargetDelay = 0f)
        {
            var go = new GameObject("Fireball");
            go.transform.SetParent(root.transform, false);
            var fireball = go.AddComponent<TrackFireball>();
            fireball.speed = 72.25f;
            fireball.lifetime = 30f;
            fireball.hitRadius = 1f;
            typeof(TrackFireball).GetField("selfTargetDelay", PrivateInstance).SetValue(fireball, selfTargetDelay);
            Physics.SyncTransforms();
            fireball.Launch(new PowerupContext(owner, null, backwards, 0));
            return fireball;
        }

        BoxCollider Box(Vector3 centre, Vector3 size)
        {
            var go = new GameObject("Solid world collider") { layer = GroundLayer };
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = centre;
            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            return box;
        }

        static void Tick(TrackFireball fireball) => typeof(PowerupBehaviour).GetMethod("StepOnce", PrivateInstance).Invoke(fireball, null);
        static void Invoke(object component, string method) => component.GetType().GetMethod(method, PrivateInstance).Invoke(component, null);
    }
}
