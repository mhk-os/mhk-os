using System.Collections.Generic;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Gameplay root of a track scene: racing lines, start grid, kill height and race camera settings. The racing lines
    /// are the <see cref="TrackPath"/>s below this object: the closed one is the main line, every open one an
    /// alternative route. Looked up through <see cref="Current"/>.
    /// </summary>
    public partial class TrackRuntime : MonoBehaviour
    {
        /// <summary>Least share of the lap an alternative route bypasses.</summary>
        public const float MinBypassLapFraction = 0.01f;
        /// <summary>Largest share of the lap an alternative route bypasses.</summary>
        public const float MaxBypassLapFraction = 0.5f;

        [Tooltip("Start slots, pole position first. Forward (+Z) = driving direction.")]
        public List<Transform> startGrid = new();
        [Tooltip("A kart in the air with no ground within this distance (m) below it falls (GameConfig.dropEffect), as " +
                 "on drop geometry. For tracks whose original respawned karts that left the ground. 0 = off.")]
        [Min(0f)] public float fallDepth;
        [Tooltip("With fallDepth: minimum time (s) in the air before a kart can fall (bumps and kerbs never count).")]
        [Min(0f)] public float fallMinAirTime = 0.1f;
        [Tooltip("Karts below this world height are respawned.")]
        public float killHeight = -30f;
        [Tooltip("Layers considered ground when placing respawned karts and for the fallDepth test.")]
        public LayerMask groundMask = ~0;

        [Header("Race camera")]
        [Tooltip("Far clip plane (m) of the race camera.")]
        public float farClip = 1000f;
        [Tooltip("Background colour where nothing is drawn (used when the scene's lighting settings have no skybox).")]
        public Color backgroundColor = new(0.5f, 0.67f, 0.85f);

        /// <summary>Name of the child group holding the render geometry.</summary>
        public const string VisualGroup = "Visual";
        /// <summary>Name of the child group holding the collision geometry.</summary>
        public const string CollisionGroup = "Collision";

        [AutoStaticsCleanup] public static TrackRuntime Current { get; private set; }

        [AutoStaticsCleanup] static readonly List<TrackPath> scratchPaths = new();

        // The racing lines below this object, found on first use (again in the editor after hierarchy and path edits).
        TrackPath mainPath;
        readonly List<TrackPath> routes = new();
        bool linesFound;
        // Fork distance and bypassed main-line length of every alternative route (projection of its end nodes).
        float[] joinStart;
        float[] joinSpan;

        void Awake() => Current = this;

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

#if UNITY_EDITOR
        // Bumped by hierarchy and racing-line edits outside play mode: every track finds its racing lines again.
        static int editorLinesVersion;
        int foundLinesVersion = -1;

        [UnityEditor.InitializeOnLoadMethod]
        static void WatchHierarchy() => UnityEditor.EditorApplication.hierarchyChanged += MarkLinesEdited;

        /// <summary>Editor: a racing line or the hierarchy changed, every track finds its racing lines again (not in play mode).</summary>
        public static void MarkLinesEdited()
        {
            if (!Application.isPlaying) editorLinesVersion++;
        }
#endif

        /// <summary>The main racing line: the first closed <see cref="TrackPath"/> below this object (node 0 on the start/finish line).</summary>
        public TrackPath MainPath
        {
            get
            {
                FindLines();
                return mainPath;
            }
        }

        /// <summary>
        /// Alternative routes (shortcuts, branches): every open <see cref="TrackPath"/> below this object in hierarchy
        /// order whose first node forks off the main line and whose last node rejoins it (<see cref="RouteProblem"/>).
        /// </summary>
        public IReadOnlyList<TrackPath> Routes
        {
            get
            {
                FindLines();
                return routes;
            }
        }

        /// <summary>Index of <paramref name="route"/> in <see cref="Routes"/>, -1 when it is none.</summary>
        public int RouteIndex(TrackPath route)
        {
            FindLines();
            return route != null ? routes.IndexOf(route) : -1;
        }

        /// <summary>The loaded track's main racing line when it has at least one segment.</summary>
        public static bool TryGetMainPath(out TrackPath mainPath)
        {
            var track = Current;
            mainPath = track != null && track.HasMainPath ? track.MainPath : null;
            return mainPath != null;
        }

        /// <summary>The main racing line exists and has at least one segment.</summary>
        public bool HasMainPath => MainPath != null && MainPath.Count > 1;

        /// <summary>True when a track is loaded and <paramref name="position"/> lies below its kill height.</summary>
        public static bool IsBelowKillHeight(Vector3 position)
        {
            var track = Current;
            return track != null && position.y < track.killHeight;
        }

        /// <summary><see cref="groundMask"/> without karts and items: static geometry that blocks driving and sight.</summary>
        public int ObstacleMask => groundMask & ~GameLayers.KartAndItemMask;

        /// <summary>Main-line distance (from the finish line) where alternative route <paramref name="index"/> forks off.</summary>
        public float BranchJoinStart(int index)
        {
            BakeJoins();
            return joinStart[index];
        }

        /// <summary>Main-line distance between the fork and the merge of alternative route <paramref name="index"/>.</summary>
        public float BranchSpan(int index)
        {
            BakeJoins();
            return joinSpan[index];
        }

        /// <summary>Main-line distance where alternative route <paramref name="index"/> rejoins the main line.</summary>
        public float BranchJoinEnd(int index) => MainPath.WrapDistance(BranchJoinStart(index) + BranchSpan(index));

        /// <summary>
        /// Main-line distance equivalent of <paramref name="branchDistance"/> on alternative route
        /// <paramref name="index"/>: the route length is mapped linearly onto the main-line interval it bypasses.
        /// </summary>
        public float MapToMain(int index, float branchDistance)
        {
            BakeJoins();
            var branch = routes[index];
            var t = branch != null && branch.Length > 0.01f ? Mathf.Clamp01(branchDistance / branch.Length) : 0f;
            return mainPath.WrapDistance(joinStart[index] + joinSpan[index] * t);
        }

        /// <summary>
        /// Alternative route <paramref name="index"/> is cut at or beyond <paramref name="fromDistance"/> (route metres):
        /// a <see cref="CollapsingBridge"/> on it has been set off. Computer drivers do not take it, respawns that would
        /// have to cross it go to the main line.
        /// </summary>
        public bool IsRouteCut(int index, float fromDistance = 0f) =>
            index >= 0 && index < Routes.Count && CollapsingBridge.CutsRoute(routes[index], fromDistance);

        /// <summary>Finds the racing lines again and drops the route junctions (after editing paths in the editor).</summary>
        public void InvalidateLines()
        {
            linesFound = false;
            joinStart = null;
        }

        // The first closed path is the main line; open paths that cannot be routes are left out (logged in races).
        // Inactive objects hold no racing lines.
        void FindLines()
        {
#if UNITY_EDITOR
            if (foundLinesVersion != editorLinesVersion)
            {
                foundLinesVersion = editorLinesVersion;
                linesFound = false;
            }
#endif
            if (linesFound) return;
            linesFound = true;
            joinStart = null;
            mainPath = null;
            routes.Clear();
            GetComponentsInChildren(false, scratchPaths);
            foreach (var p in scratchPaths)
                if (p.closed)
                {
                    mainPath = p;
                    break;
                }
            foreach (var p in scratchPaths)
            {
                if (p.closed) continue;
                var problem = RouteProblem(mainPath, p);
                if (problem == null) routes.Add(p);
                else if (Application.isPlaying) Debug.LogWarning($"[Track] '{p.name}' is not used as an alternative route: {problem}.", p);
            }
            scratchPaths.Clear();
        }

        void BakeJoins()
        {
            FindLines();
            if (joinStart != null && joinStart.Length == routes.Count) return;
            var n = routes.Count;
            joinStart = new float[n];
            joinSpan = new float[n];
            for (var i = 0; i < n; i++) ComputeJoin(mainPath, routes[i], out joinStart[i], out joinSpan[i]);
        }

        /// <summary><see cref="RouteProblem"/> of a route whose nodes run against the driving direction.</summary>
        public const string ReversedRouteProblem = "it is drawn against the driving direction";

        /// <summary>
        /// Why the open <paramref name="route"/> cannot be an alternative route of <paramref name="main"/>, null when it
        /// can: it needs two nodes and must bypass <see cref="MinBypassLapFraction"/> to <see cref="MaxBypassLapFraction"/>
        /// of the lap in driving direction.
        /// </summary>
        public static string RouteProblem(TrackPath main, TrackPath route)
        {
            if (main == null || main.Count < 2) return "the track has no closed main racing line";
            if (route.Count < 2) return "it needs at least 2 nodes";
            ComputeJoin(main, route, out var start, out var span);
            var lap = main.Length;
            if (span >= MinBypassLapFraction * lap && span <= MaxBypassLapFraction * lap) return null;
            var reverse = lap - span;
            if (reverse >= MinBypassLapFraction * lap && reverse <= MaxBypassLapFraction * lap) return ReversedRouteProblem;
            return $"it joins the main line at {start:0} -> {main.WrapDistance(start + span):0} m and must bypass " +
                   $"{MinBypassLapFraction:P0} to {MaxBypassLapFraction:P0} of the lap in driving direction";
        }

        /// <summary>
        /// Fork distance on <paramref name="main"/> (projection of the route's first node) and the main-line length
        /// the route bypasses (up to the projection of its last node, in driving direction).
        /// </summary>
        public static void ComputeJoin(TrackPath main, TrackPath branch, out float start, out float span)
        {
            start = 0f;
            span = 0f;
            if (main == null || branch == null || main.Count < 2 || branch.Count < 2) return;
            start = main.Project(branch.GetNodePosition(0));
            var end = main.Project(branch.GetNodePosition(branch.Count - 1));
            span = main.WrapDistance(end - start);
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (MainPath != null) mainPath.DrawGizmoLines();
            foreach (var route in routes)
                if (route != null) route.DrawGizmoLines();
        }
#endif
    }
}
