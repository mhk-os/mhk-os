using System.Collections.Generic;
using System.Reflection;
using MHK.Karts;
using MHK.Race;
using NUnit.Framework;
using UnityEngine;

namespace MHK.Tests
{
    /// <summary>Lap progress through alternative routes, driven step by step along given lines.</summary>
    public class RacerProgressRouteTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        const float Speed = 25f;
        // Projections weight heights: isolate the fixture by height from anything else in the test scene.
        static readonly Vector3 Origin = new(0f, 1000f, 0f);
        static readonly FieldInfo LapDistance = typeof(RacerProgress).GetField("lapDistance", PrivateInstance);
        static readonly FieldInfo LineSide = typeof(RacerProgress).GetField("lineSide", PrivateInstance);
        static readonly MethodInfo Locate = typeof(RacerProgress).GetMethod("Locate", PrivateInstance);
        static readonly MethodInfo UpdateLap = typeof(RacerProgress).GetMethod("UpdateLap", PrivateInstance);
        GameObject root;
        TrackRuntime track;
        Kart kart;
        Vector3 at;

        // A 920 m lap with the finish at node 0 and one key checkpoint at 500 m on the east side.
        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Route progress test");
            root.transform.position = Origin;
            track = root.AddComponent<TrackRuntime>();
            Invoke(track, "Awake");
            Path("Main",
                Node(0, 0, TrackNodeFlags.Finish), Node(0, 100), Node(0, 200), Node(200, 200),
                Node(200, 100, TrackNodeFlags.Checkpoint), Node(200, -60), Node(0, -60));
        }

        [TearDown]
        public void TearDown()
        {
            if (kart != null) Invoke(kart, "OnDisable");
            Invoke(track, "OnDestroy");
            Object.DestroyImmediate(root);
        }

        [Test]
        public void CuttingIntoAShortcutThatForksWithAnotherRouteFollowsTheShortcut()
        {
            // Like Ventilation's Routes 1 and 5: both fork at about 100 m and cross each other right after. The detour
            // rejoins at 180 m, the shortcut only at 450 m.
            Path("Detour", Node(0, 100), Node(15, 110), Node(25, 140), Node(15, 170), Node(0, 180));
            var shortcut = Path("Shortcut", Node(0, 107), Node(10, 112), Node(40, 130), Node(100, 140), Node(160, 150), Node(200, 150));
            Begin(new Vector3(0, 0, 40));
            // Cutting the corner leaves the main road closer to the detour's fork than to the shortcut's.
            Drive(new Vector3(0, 0, 95), new Vector3(40, 0, 130), new Vector3(100, 0, 140));
            Assert.AreSame(shortcut, kart.Progress.CurrentBranch, "The kart is on the shortcut, not the detour it passed at the fork.");
            Drive(new Vector3(160, 0, 150), new Vector3(200, 0, 150), new Vector3(200, 0, 60));
            Assert.IsNull(kart.Progress.CurrentBranch);
            Assert.AreEqual(540f, kart.Progress.PathDistance, 5f, "Back on the main line where the kart really is.");
            Drive(new Vector3(200, 0, -60), new Vector3(0, 0, -60), new Vector3(0, 0, 20));
            Assert.AreEqual(2, kart.Progress.Lap, "The key checkpoint was passed, so the finish crossing counts the lap.");
        }

        [Test]
        public void KartCarriedOffARouteIsFoundOnTheMainLineAgain()
        {
            var detour = Path("Detour", Node(0, 100), Node(15, 110), Node(25, 140), Node(15, 170), Node(0, 180));
            Begin(new Vector3(0, 0, 60));
            Drive(new Vector3(0, 0, 100), new Vector3(15, 0, 110), new Vector3(25, 0, 140));
            Assert.AreSame(detour, kart.Progress.CurrentBranch);
            // Straight across open ground to the far side of the lap, nowhere near another racing line on the way.
            Drive(new Vector3(200, 0, 140), new Vector3(200, 0, 100));
            Assert.IsNull(kart.Progress.CurrentBranch);
            Assert.AreEqual(500f, kart.Progress.PathDistance, 5f);
        }

        [Test]
        public void LapValidationCountsTheSameShortcutLap()
        {
            // The validator replays RacerProgress's lap logic over observed states: it must count the lap the race counted.
            Path("Detour", Node(0, 100), Node(15, 110), Node(25, 140), Node(15, 170), Node(0, 180));
            Path("Shortcut", Node(0, 107), Node(10, 112), Node(40, 130), Node(100, 140), Node(160, 150), Node(200, 150));
            var replay = new Validation.LapReplay(track, 3, 3f);
            var position = new Vector3(0, 0, 40);
            replay.Begin(Origin + position + Vector3.up * 0.4f, Vector3.forward);
            var events = new List<Validation.LapEventKind>();
            var step = Speed * Time.fixedDeltaTime;
            var clock = 0f;
            foreach (var target in new[] { new Vector3(0, 0, 95), new Vector3(40, 0, 130), new Vector3(100, 0, 140), new Vector3(160, 0, 150),
                         new Vector3(200, 0, 150), new Vector3(200, 0, -60), new Vector3(0, 0, -60), new Vector3(0, 0, 20) })
            {
                var heading = (target - position).normalized;
                var steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(position, target) / step));
                var from = position;
                for (var i = 1; i <= steps; i++)
                {
                    clock += Time.fixedDeltaTime;
                    var e = replay.Step(Origin + Vector3.Lerp(from, target, (float)i / steps) + Vector3.up * 0.4f, heading, Speed, true,
                        clock, Time.fixedDeltaTime, false);
                    if (e.kind != Validation.LapEventKind.None) events.Add(e.kind);
                }
                position = target;
            }
            CollectionAssert.AreEqual(new[] { Validation.LapEventKind.Lap }, events);
        }

        static TrackPath.Node Node(float x, float z, TrackNodeFlags flags = TrackNodeFlags.None) =>
            new() { position = new Vector3(x, 0f, z), width = 8f, flags = flags };

        // The first path is the closed main line, every later one an open alternative route.
        TrackPath Path(string name, params TrackPath.Node[] nodes)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var path = go.AddComponent<TrackPath>();
            path.closed = track.GetComponentInChildren<TrackPath>() == path;
            path.SetNodes(nodes);
            track.InvalidateLines();
            return path;
        }

        void Begin(Vector3 position)
        {
            var go = new GameObject("Racer");
            go.transform.SetParent(root.transform, false);
            kart = go.AddComponent<Kart>();
            Invoke(kart, "Awake");
            Invoke(kart.Physics, "Awake");
            Invoke(kart.Effects, "Awake");
            Invoke(kart.Progress, "Awake");
            Invoke(kart, "OnEnable");
            at = position;
            Place(position, Vector3.forward);
            kart.Progress.Begin(track, 3);
        }

        // Drives straight lines through the waypoints at Speed, one progress update per physics step.
        void Drive(params Vector3[] waypoints)
        {
            var step = Speed * Time.fixedDeltaTime;
            foreach (var target in waypoints)
            {
                var heading = target - at;
                var steps = Mathf.Max(1, Mathf.CeilToInt(heading.magnitude / step));
                var from = at;
                for (var i = 1; i <= steps; i++)
                {
                    Place(Vector3.Lerp(from, target, (float)i / steps), heading);
                    var prevLap = (float)LapDistance.GetValue(kart.Progress);
                    var prevSide = (float)LineSide.GetValue(kart.Progress);
                    // FixedUpdate without its wrong-way and respawn parts, which need the game's config.
                    Locate.Invoke(kart.Progress, new object[] { false });
                    UpdateLap.Invoke(kart.Progress, new object[] { prevLap, prevSide, Time.fixedDeltaTime });
                }
                at = target;
            }
        }

        void Place(Vector3 local, Vector3 heading)
        {
            heading.y = 0f;
            kart.transform.SetPositionAndRotation(Origin + local + Vector3.up * 0.4f, Quaternion.LookRotation(heading.normalized));
            kart.Physics.Body.position = kart.transform.position;
            kart.Physics.Body.linearVelocity = heading.normalized * Speed;
        }

        static void Invoke(object component, string method) => component.GetType().GetMethod(method, PrivateInstance).Invoke(component, null);
    }
}
