using System.Collections.Generic;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Race
{
    /// <summary>
    /// Gameplay root of a track scene: racing line and routes, start grid, kill height and race camera settings.
    /// Looked up through <see cref="Current"/>.
    /// </summary>
    public partial class TrackRuntime : MonoBehaviour
    {
        [Tooltip("Main racing line (closed, node 0 on the start/finish line).")]
        public TrackPath path;
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
        [Tooltip("Open alternative routes (shortcuts / branches): first node on the main line where the route forks " +
                 "off, last node where it rejoins.")]
        public List<TrackPath> alternatePaths = new();

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

        // Fork distance and bypassed main-line length of every alternative route (projection of its end nodes).
        float[] joinStart;
        float[] joinSpan;

        void Awake() => Current = this;

        void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        /// <summary>The loaded track's main racing line when it has at least one segment.</summary>
        public static bool TryGetMainPath(out TrackPath mainPath)
        {
            var track = Current;
            mainPath = track != null && track.HasMainPath ? track.path : null;
            return mainPath != null;
        }

        /// <summary>The main racing line exists and has at least one segment.</summary>
        public bool HasMainPath => path != null && path.Count > 1;

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
        public float BranchJoinEnd(int index) => path.WrapDistance(BranchJoinStart(index) + BranchSpan(index));

        /// <summary>
        /// Main-line distance equivalent of <paramref name="branchDistance"/> on alternative route
        /// <paramref name="index"/>: the route length is mapped linearly onto the main-line interval it bypasses.
        /// </summary>
        public float MapToMain(int index, float branchDistance)
        {
            BakeJoins();
            var branch = alternatePaths[index];
            var t = branch != null && branch.Length > 0.01f ? Mathf.Clamp01(branchDistance / branch.Length) : 0f;
            return path.WrapDistance(joinStart[index] + joinSpan[index] * t);
        }

        /// <summary>Drops the cached route junctions (after editing paths in the editor).</summary>
        public void InvalidateJoins() => joinStart = null;

        void BakeJoins()
        {
            if (joinStart != null && joinStart.Length == alternatePaths.Count) return;
            var n = alternatePaths.Count;
            joinStart = new float[n];
            joinSpan = new float[n];
            for (var i = 0; i < n; i++) ComputeJoin(path, alternatePaths[i], out joinStart[i], out joinSpan[i]);
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
            if (path != null) path.DrawGizmoLines();
            foreach (var alt in alternatePaths)
                if (alt != null) alt.DrawGizmoLines();
        }
#endif
    }
}
