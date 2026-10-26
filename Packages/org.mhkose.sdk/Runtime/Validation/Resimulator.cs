using System.Collections.Generic;
using MHK.Karts;
using UnityEngine;

namespace MHK.Validation
{
    /// <summary>
    /// A stretch of observed ticks to re-simulate: per tick (the first one only gives the velocity into the second) pose,
    /// velocity at the start of the tick, keys and steering command. Ticks without a sample are interpolated.
    /// </summary>
    public sealed class ResimWindow
    {
        public KartValidator owner;
        public KartSpec spec;
        public float tickSeconds;
        /// <summary>Tick of index 0.</summary>
        public int firstTick;
        /// <summary>Ticks driven from the recorded poses (input model, gearbox and wheel loads settle) before the free run.</summary>
        public int warmup;
        public Vector3[] position, velocity, angularVelocity;
        public Quaternion[] rotation;
        public KartKeys[] keys;
        public float[] steerCommand;
        /// <summary>
        /// Index whose observed position the re-simulation is compared with: the last one, or the one before something
        /// pushed the observed kart (a collision the static copy may not reproduce).
        /// </summary>
        public int compareIndex;

        public int Count => position.Length;
        public int LastTick => firstTick + Count - 1;
        /// <summary>Path length (m) of the free run from the release up to index <paramref name="end"/>.</summary>
        public float TravelledTo(int end)
        {
            var d = 0f;
            for (var i = warmup + 2; i <= end && i < Count; i++) d += Vector3.Distance(position[i - 1], position[i]);
            return d;
        }
    }

    /// <summary>Result of one <see cref="ResimWindow"/>.</summary>
    public readonly struct ResimOutcome
    {
        /// <summary>Distance (m) between the re-simulated and the observed position at <see cref="index"/>.</summary>
        public readonly float error;
        /// <summary>Window index compared: the end, or where the comparison had to stop early.</summary>
        public readonly int index;
        /// <summary>
        /// The comparison stopped early: the re-simulated chassis hit a wall, or something pushed the observed kart.
        /// PhysX contacts make small differences grow, so what follows says nothing about the inputs.
        /// </summary>
        public readonly bool touched;

        public ResimOutcome(float error, int index, bool touched)
        {
            this.error = error;
            this.index = index;
            this.touched = touched;
        }
    }

    /// <summary>
    /// Runs <see cref="ResimWindow"/>s in a <see cref="TrackContext"/>'s physics scene one tick per <see cref="Step"/>, so the
    /// caller spreads them over frames: a stripped kart is driven along the recorded poses for the warm-up, then steps
    /// freely with the recorded keys through <see cref="PhysicsScene.Simulate"/>, and its position is compared with the
    /// recorded one at the end of the window or when the chassis first hits a wall.
    /// </summary>
    public sealed class Resimulator
    {
        readonly TrackContext track;
        readonly Queue<ResimWindow> queue = new();
        ResimWindow current;
        ResimKart kart;
        int index;

        public Resimulator(TrackContext track) => this.track = track;

        /// <summary>Windows queued or running.</summary>
        public int Pending => queue.Count + (current != null ? 1 : 0);

        public void Enqueue(ResimWindow window) => queue.Enqueue(window);

        /// <summary>Advances the running window by one tick (or finishes it). False when there is nothing to do.</summary>
        public bool Step()
        {
            if (current == null)
            {
                if (queue.Count == 0) return false;
                current = queue.Dequeue();
                kart = track.KartFor(current.spec);
                index = 1;
            }
            var w = current;
            var phys = kart.physics;
            if (index >= w.compareIndex)
            {
                var at = w.compareIndex;
                Finish(Vector3.Distance(phys.Body.position, w.position[at]), at, at < w.Count - 1);
                return true;
            }
            var h = w.tickSeconds;
            if (index <= w.warmup + 1) Drive(w, phys);
            phys.Step(Input(w, index), h);
            if (index == w.warmup + 1) kart.contacts.Touched = false;
            if (index > w.warmup)
            {
                track.Physics.Simulate(h);
                if (kart.contacts.Touched)
                {
                    Finish(Vector3.Distance(phys.Body.position, w.position[index + 1]), index + 1, true);
                    return true;
                }
            }
            index++;
            return true;
        }

        void Finish(float error, int at, bool touched)
        {
            var w = current;
            current = null;
            w.owner.ReportWindow(w, new ResimOutcome(error, at, touched));
        }

        // Warm-up and release tick: the kart is put on the recorded pose with the velocity it entered the tick with.
        void Drive(ResimWindow w, KartPhysics phys)
        {
            if (index == 1)
            {
                phys.Teleport(w.position[1], w.rotation[1]);
                var k = w.keys[1];
                phys.PrimeInput(w.steerCommand[1], (k & KartKeys.Throttle) != 0, (k & (KartKeys.Brake | KartKeys.Parked)) != 0);
            }
            // The transform too: one left behind would pull the body back when the simulation syncs transforms.
            phys.transform.SetPositionAndRotation(w.position[index], w.rotation[index]);
            var body = phys.Body;
            body.position = w.position[index];
            body.rotation = w.rotation[index];
            body.linearVelocity = w.velocity[index];
            body.angularVelocity = w.angularVelocity[index];
        }

        static KartInput Input(ResimWindow w, int i) => new KartSample { keys = w.keys[i] }.Input;
    }
}
