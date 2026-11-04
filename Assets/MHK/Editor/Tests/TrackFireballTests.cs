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
        public void AheadOfActiveLeaderWaitsButRespawningLeaderDoesNotParkIt()
        {
            var owner = KartAt(new Vector3(0, 0, 35), 1);
            KartAt(new Vector3(0, 0, 90), 2);
            var fireball = Launch(owner);
            var waitingAt = fireball.NetPosition;
            for (var i = 0; i < 10; i++) Tick(fireball);
            Assert.AreEqual(waitingAt, fireball.NetPosition);
            Assert.AreEqual(Vector3.zero, fireball.NetVelocity);
            owner.Effects.ProtectedUntilTick = int.MaxValue;
            for (var i = 0; i < 10; i++) Tick(fireball);
            Assert.Greater(fireball.NetPosition.z, waitingAt.z + 5f);
            owner.Effects.ProtectedUntilTick = 0;
            Tick(fireball);
            Assert.AreEqual(Vector3.zero, fireball.NetVelocity, "Once active again, the leader behind it is waited for.");
        }

        [Test]
        public void ClosePursuitKeepsReferenceSpeedWhenLeaderBrakes()
        {
            var owner = KartAt(Vector3.zero, 2);
            var leader = KartAt(new Vector3(0, 0, 7), 1);
            var cruise = leader.NormalTopSpeed;
            leader.Physics.Body.linearVelocity = Vector3.forward * cruise;
            var fireball = Launch(owner);
            Tick(fireball);
            Assert.Less(fireball.NetVelocity.z, leader.Physics.Body.linearVelocity.z, "A cruising leader pulls away at close range.");
            leader.Physics.Body.linearVelocity = Vector3.zero;
            Tick(fireball);
            Assert.Greater(fireball.NetVelocity.z, leader.Physics.Body.linearVelocity.z, "Braking lets the fireball close the gap.");
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

        static void Rank(Kart kart, int rank) => typeof(RacerProgress).GetProperty("Position").SetValue(kart.Progress, rank);

        TrackFireball Launch(Kart owner, bool backwards = false)
        {
            var go = new GameObject("Fireball");
            go.transform.SetParent(root.transform, false);
            var fireball = go.AddComponent<TrackFireball>();
            fireball.speed = 72.25f;
            fireball.lifetime = 30f;
            fireball.hitRadius = 1f;
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
