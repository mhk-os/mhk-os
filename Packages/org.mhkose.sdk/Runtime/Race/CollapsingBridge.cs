using System;
using System.Collections.Generic;
using MHK.Audio;
using MHK.Ghosts;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Audio;

namespace MHK.Race
{
    /// <summary>
    /// One-time bridge (MHK Extra Trigger.ini): the first kart on it sets it off. After <see cref="triggerWait"/> the
    /// rumble plays and the pieces fall one by one on a fixed schedule; <see cref="dropWait"/> later every kart on the
    /// bridge falls (<see cref="Core.GameConfig.dropEffect"/>), as on drop geometry, for the rest of the race. The road
    /// collision under the pieces stays: the bridge area is the corridor between its <see cref="gates"/>.
    /// Driven by the race clock in physics steps, so the collapse comes at the same race time in every run with the same
    /// driving; a replay sets it off at the tick the watched ghost reached the bridge.
    /// </summary>
    [DefaultExecutionOrder(900)] // after karts moved and respawned this step, before GhostRecorder (1000): it sees the pose a ghost records
    public partial class CollapsingBridge : MonoBehaviour
    {
        /// <summary>A line across the road; the bridge area is the corridor between consecutive gates.</summary>
        [Serializable]
        public struct Gate
        {
            [Tooltip("Left end of the gate line (local space of the bridge object).")]
            public Vector3 left;
            [Tooltip("Right end of the gate line (local space of the bridge object).")]
            public Vector3 right;
        }

        /// <summary>A falling piece and its delay.</summary>
        [Serializable]
        public struct Segment
        {
            [Tooltip("Render object that falls. Must not be static-batched; its colliders (if any) do not move.")]
            public Transform piece;
            [Tooltip("Delay (s) from the start of the collapse until this piece falls.")]
            [Min(0f)] public float delay;
        }

        [Tooltip("Alternative route the bridge lies on. Once the bridge is set off, computer drivers no longer take the " +
                 "route and respawns that would have to cross the bridge go to the main line. Empty for a bridge on no route.")]
        public TrackPath route;
        [Tooltip("Gate lines across the road in driving order (at least two). A kart in the corridor between them sets the " +
                 "bridge off, and falls once it is down.")]
        public List<Gate> gates = new();
        [Tooltip("The corridor reaches this far (m) beyond each end of a gate line (the road collision is wider than the gates).")]
        [Min(0f)] public float sideMargin = 1f;
        [Tooltip("A kart counts as on the bridge within this height (m) above or below its gate lines.")]
        [Min(0f)] public float heightRange = 3f;
        [Tooltip("Time (s) from the first kart on the bridge to the rumble and the start of the collapse.")]
        [Min(0f)] public float triggerWait = 1f;
        [Tooltip("Time (s) from the start of the collapse until karts on the bridge fall.")]
        [Min(0f)] public float dropWait = 0.5f;
        [Tooltip("Downward acceleration (m/s²) of a falling piece.")]
        [Min(0.01f)] public float fallAcceleration = 150f;
        [Tooltip("Depth (m) a piece falls before it is hidden.")]
        [Min(0f)] public float fallDepth = 40f;
        [Tooltip("Pieces that fall, each after its own delay.")]
        public List<Segment> segments = new();

        [Header("Sound")]
        [Tooltip("Played at the bridge when the collapse starts. Empty = silent.")]
        public AudioResource sound;
        [Tooltip("Volume of the collapse sound.")]
        [Range(0f, 1f)] public float soundVolume = 1f;
        [Tooltip("How far the collapse sound carries.")]
        public SoundRange soundRange = new();

        // A collapse sound more than this late (replay seeking past it) is not played.
        const float SoundLateness = 0.5f;

        enum Phase { Rest, Falling, Gone }

        [AutoStaticsCleanup] static readonly List<CollapsingBridge> Active = new();

        // Corridor corners in world XZ: left, right per gate (widened by sideMargin), and their bounds.
        Vector2[] corners = Array.Empty<Vector2>();
        Vector2 boundsMin, boundsMax;
        float minY, maxY;
        float routeStart, routeEnd;
        Vector3 centre;
        Vector3[] restPositions = Array.Empty<Vector3>();
        Renderer[] renderers = Array.Empty<Renderer>();
        float goneAfter;
        Phase shown = Phase.Rest;
        bool soundPlayed;
        int liveTrigger = -1;
        GhostData replayData;
        int replayTrigger = -1;

        void Awake()
        {
            BakeCorridor();
            BakeRoute();
            BakeSegments();
        }

        void OnEnable() => Active.Add(this);
        void OnDisable() => Active.Remove(this);

        void BakeCorridor()
        {
            var n = gates.Count;
            corners = new Vector2[2 * n];
            boundsMin = new Vector2(float.MaxValue, float.MaxValue);
            boundsMax = new Vector2(float.MinValue, float.MinValue);
            minY = float.MaxValue;
            maxY = float.MinValue;
            var sum = Vector3.zero;
            for (var i = 0; i < n; i++)
            {
                var l = transform.TransformPoint(gates[i].left);
                var r = transform.TransformPoint(gates[i].right);
                var across = new Vector2(r.x - l.x, r.z - l.z);
                var length = across.magnitude;
                var grow = length > 1e-4f ? across * (sideMargin / length) : Vector2.zero;
                corners[2 * i] = new Vector2(l.x, l.z) - grow;
                corners[2 * i + 1] = new Vector2(r.x, r.z) + grow;
                for (var k = 2 * i; k <= 2 * i + 1; k++)
                {
                    boundsMin = Vector2.Min(boundsMin, corners[k]);
                    boundsMax = Vector2.Max(boundsMax, corners[k]);
                }
                minY = Mathf.Min(minY, Mathf.Min(l.y, r.y));
                maxY = Mathf.Max(maxY, Mathf.Max(l.y, r.y));
                sum += l + r;
            }
            minY -= heightRange;
            maxY += heightRange;
            centre = n > 0 ? sum / (2 * n) : transform.position;
        }

        // Route distances of the gate midpoints: the stretch of the route the bridge covers.
        void BakeRoute()
        {
            routeStart = float.MaxValue;
            routeEnd = float.MinValue;
            if (route == null || route.Count < 2) return;
            foreach (var g in gates)
            {
                var d = route.Project(transform.TransformPoint((g.left + g.right) * 0.5f));
                routeStart = Mathf.Min(routeStart, d);
                routeEnd = Mathf.Max(routeEnd, d);
            }
        }

        void BakeSegments()
        {
            var n = segments.Count;
            restPositions = new Vector3[n];
            renderers = new Renderer[n];
            var lastDelay = 0f;
            for (var i = 0; i < n; i++)
            {
                var piece = segments[i].piece;
                if (piece == null) continue;
                restPositions[i] = piece.position;
                renderers[i] = piece.GetComponent<Renderer>();
                lastDelay = Mathf.Max(lastDelay, segments[i].delay);
            }
            goneAfter = lastDelay + Mathf.Sqrt(2f * fallDepth / fallAcceleration);
        }

        /// <summary>Race tick at which the bridge was set off as of tick <paramref name="ticks"/>, -1 while it stands.</summary>
        public int TriggerTickAt(int ticks)
        {
            if (TryGetReplay(out var data))
            {
                if (data != replayData) FindReplayTrigger(data);
                return replayTrigger >= 0 && ticks >= replayTrigger ? replayTrigger : -1;
            }
            return liveTrigger >= 0 && ticks >= liveTrigger ? liveTrigger : -1;
        }

        /// <summary>The bridge has been set off (it is coming down or down).</summary>
        public bool Triggered => TriggerTickAt(RaceClock.Ticks) >= 0;

        /// <summary>Karts on the bridge fall: <see cref="triggerWait"/> + <see cref="dropWait"/> after it was set off.</summary>
        public bool IsDown
        {
            get
            {
                var ticks = RaceClock.Ticks;
                var trigger = TriggerTickAt(ticks);
                return trigger >= 0 && RaceClock.SecondsAt(ticks - trigger) > triggerWait + dropWait;
            }
        }

        /// <summary>True when <paramref name="position"/> lies in the bridge corridor.</summary>
        public bool Contains(Vector3 position)
        {
            if (corners.Length < 4 || position.y < minY || position.y > maxY) return false;
            var p = new Vector2(position.x, position.z);
            if (p.x < boundsMin.x || p.y < boundsMin.y || p.x > boundsMax.x || p.y > boundsMax.y) return false;
            for (var i = 0; i + 3 < corners.Length; i += 2)
                if (InQuad(p, corners[i], corners[i + 1], corners[i + 3], corners[i + 2])) return true;
            return false;
        }

        /// <summary>True when <paramref name="position"/> is on a bridge that is down: a kart there falls.</summary>
        public static bool IsDownAt(Vector3 position)
        {
            for (var i = 0; i < Active.Count; i++)
                if (Active[i].Contains(position) && Active[i].IsDown) return true;
            return false;
        }

        /// <summary>
        /// A bridge on <paramref name="line"/> has been set off and ends at or beyond <paramref name="fromDistance"/>
        /// (route metres): a kart there would have to cross it.
        /// </summary>
        public static bool CutsRoute(TrackPath line, float fromDistance)
        {
            if (line == null) return false;
            for (var i = 0; i < Active.Count; i++)
            {
                var b = Active[i];
                if (b.route == line && b.routeEnd >= fromDistance && b.Triggered) return true;
            }
            return false;
        }

        void FixedUpdate()
        {
            var ticks = RaceClock.Ticks;
            if (liveTrigger > ticks || !RaceClock.Running) liveTrigger = -1; // the clock was reset
            if (liveTrigger >= 0 || !RaceClock.Running || TryGetReplay(out _)) return;
            var race = RaceManager.Current;
            var karts = race.Karts;
            for (var i = 0; i < karts.Count; i++)
            {
                var kart = karts[i];
                var body = kart != null && kart.Physics != null ? kart.Physics.Body : null;
                if (body == null || !Contains(body.position)) continue;
                liveTrigger = ticks;
                return;
            }
        }

        // Replays have no physics karts: the watched ghost's recorded ticks set the bridge off.
        static bool TryGetReplay(out GhostData data)
        {
            var race = RaceManager.Current;
            var run = race != null ? race.TimeTrial : null;
            var ghost = run != null && run.IsReplay ? run.Spectated : null;
            data = ghost != null ? ghost.Data : null;
            return data != null;
        }

        // The first tick the ghost was on the bridge, as the live check sees it (tick 0 is GO, before any step).
        void FindReplayTrigger(GhostData data)
        {
            replayData = data;
            replayTrigger = -1;
            var frames = data.frames;
            for (var i = 1; i < frames.Length; i++)
            {
                if (!Contains(frames[i].position)) continue;
                replayTrigger = i;
                return;
            }
        }

        void Update()
        {
            var race = RaceManager.Current;
            var trigger = race != null && race.ClockRunning ? TriggerTickAt(race.RaceTicks) : -1;
            var t = trigger >= 0 ? RaceClock.RenderSeconds - RaceClock.SecondsAt(trigger) - triggerWait : double.NegativeInfinity;
            UpdateSound(t);
            var phase = t <= 0.0 ? Phase.Rest : t >= goneAfter ? Phase.Gone : Phase.Falling;
            if (phase == shown && phase != Phase.Falling) return;
            shown = phase;
            PlacePieces(t);
        }

        void UpdateSound(double t)
        {
            if (t < 0.0)
            {
                soundPlayed = false;
                return;
            }
            if (soundPlayed) return;
            soundPlayed = true;
            if (t < SoundLateness) AudioManager.PlaySfx(sound, centre, soundVolume, 1f, soundRange);
        }

        // Pure function of the collapse time t, so replay seeking shows the pieces as they were then.
        void PlacePieces(double t)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var piece = segments[i].piece;
                if (piece == null) continue;
                var u = (float)(t - segments[i].delay);
                var drop = u > 0f ? Mathf.Min(0.5f * fallAcceleration * u * u, fallDepth) : 0f;
                piece.position = restPositions[i] + Vector3.down * drop;
                if (renderers[i] != null) renderers[i].enabled = drop < fallDepth;
            }
        }

        // Even-odd test against the quad a-b-c-d.
        static bool InQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
            Crosses(p, a, b) ^ Crosses(p, b, c) ^ Crosses(p, c, d) ^ Crosses(p, d, a);

        static bool Crosses(Vector2 p, Vector2 a, Vector2 b) =>
            (a.y > p.y) != (b.y > p.y) && p.x < a.x + (p.y - a.y) * (b.x - a.x) / (b.y - a.y);

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.4f, 0.1f);
            for (var i = 0; i < gates.Count; i++)
            {
                var l = transform.TransformPoint(gates[i].left);
                var r = transform.TransformPoint(gates[i].right);
                Gizmos.DrawLine(l, r);
                if (i == 0) continue;
                Gizmos.DrawLine(transform.TransformPoint(gates[i - 1].left), l);
                Gizmos.DrawLine(transform.TransformPoint(gates[i - 1].right), r);
            }
        }
#endif
    }
}
