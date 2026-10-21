using System.Collections.Generic;
using System.Linq;
using MHK.Race;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Lighting
{
    /// <summary>Light-probe rings along every racing line of a track.</summary>
    internal static class RacingLineProbes
    {
        const string ProbeGroupName = "Light Probes";
        const float MinProbeSpacing = 1f;
        const float MergeCellMargin = 1.01f;

        /// <summary>
        /// Samples every TrackPath under <paramref name="root"/> every probe spacing and writes probe rings (centre and
        /// both sides, at each probe height above the ground) to the track's light-probe group. Returns the centre points.
        /// The ground is the higher of collision and visual geometry: a probe under a visual road would bake fully shadowed.
        /// </summary>
        public static List<Vector3> Place(Transform root, TrackBakeSettings settings, out int probeCount)
        {
            var temporary = AddVisualColliders(root);
            List<Vector3> centres;
            ProbeSet probes;
            try
            {
                centres = SampleRacingLines(root, settings, out probes);
            }
            finally
            {
                foreach (var c in temporary) Object.DestroyImmediate(c);
            }
            probeCount = SetProbes(root, probes.Positions);
            return centres;
        }

        static List<MeshCollider> AddVisualColliders(Transform root)
        {
            var temporary = new List<MeshCollider>();
            var visual = root.Find(TrackRuntime.VisualGroup);
            if (visual == null) return temporary;
            foreach (var filter in visual.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.TryGetComponent<Collider>(out _) || !filter.TryGetComponent<MeshRenderer>(out _)) continue;
                var c = filter.gameObject.AddComponent<MeshCollider>();
                c.hideFlags = HideFlags.DontSave;
                c.sharedMesh = filter.sharedMesh;
                temporary.Add(c);
            }
            return temporary;
        }

        static List<Vector3> SampleRacingLines(Transform root, TrackBakeSettings settings, out ProbeSet probes)
        {
            Physics.SyncTransforms();
            var centres = new List<Vector3>();
            probes = new ProbeSet(settings.ProbeMergeDistance);
            var spacing = Mathf.Max(MinProbeSpacing, settings.ProbeSpacing);
            foreach (var path in root.GetComponentsInChildren<TrackPath>(true))
            {
                if (path.Count < 2) continue;
                var length = path.Length;
                var steps = Mathf.Max(1, Mathf.CeilToInt(length / spacing));
                for (var s = 0; s <= steps; s++)
                {
                    if (path.closed && s == steps) break;
                    var d = length * s / steps;
                    var centre = path.GetPoint(d);
                    centres.Add(centre);
                    AddProbeRing(path, d, centre, settings, probes);
                }
            }
            return centres;
        }

        static void AddProbeRing(TrackPath path, float d, Vector3 centre, TrackBakeSettings settings, ProbeSet probes)
        {
            var side = Vector3.Cross(Vector3.up, path.GetTangent(d)).normalized;
            if (side == Vector3.zero) side = Vector3.right;
            var half = path.GetWidth(d) * settings.ProbeLateral;
            var ground = Ground(centre, settings);
            foreach (var offset in new[] { 0f, -half, half })
            {
                var p = ground + side * offset;
                if (offset != 0f)
                {
                    // Walls between the centre and the side, checked at kart height (lower, camber and kerbs would drop side probes).
                    var up = Vector3.up * settings.ProbeWallCheckHeight;
                    if (Physics.Linecast(ground + up, p + up, Physics.AllLayers, QueryTriggerInteraction.Ignore)) continue;
                    p = Ground(p, settings);
                }
                foreach (var h in settings.ProbeHeights) probes.TryAdd(p + Vector3.up * h);
            }
        }

        static Vector3 Ground(Vector3 p, TrackBakeSettings settings) =>
            Physics.Raycast(p + Vector3.up * settings.GroundSearchLift, Vector3.down, out var hit, settings.GroundSearchDepth, Physics.AllLayers,
                            QueryTriggerInteraction.Ignore) ? hit.point : p;

        static int SetProbes(Transform root, List<Vector3> positions)
        {
            var lighting = FindOrCreateChild(root, TrackBakePreparer.LightingObjectName);
            var groupTransform = FindOrCreateChild(lighting, ProbeGroupName);
            if (!groupTransform.TryGetComponent(out LightProbeGroup group)) group = groupTransform.gameObject.AddComponent<LightProbeGroup>();
            var local = positions.Select(p => groupTransform.InverseTransformPoint(p)).ToArray();
            if (!group.probePositions.SequenceEqual(local))
            {
                Undo.RecordObject(group, "Light probes");
                group.probePositions = local;
            }
            return local.Length;
        }

        static Transform FindOrCreateChild(Transform parent, string name)
        {
            var child = parent.Find(name);
            if (child != null) return child;
            child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        // Probes closer than the merge distance to an earlier one are dropped, found through a grid of merge-sized cells.
        sealed class ProbeSet
        {
            public readonly List<Vector3> Positions = new();
            readonly Dictionary<Vector3Int, List<Vector3>> cells = new();
            readonly float merge2;
            readonly float cellSize;

            public ProbeSet(float mergeDistance)
            {
                merge2 = mergeDistance * mergeDistance;
                cellSize = mergeDistance != 0f ? Mathf.Abs(mergeDistance) * MergeCellMargin : 1f;
            }

            public void TryAdd(Vector3 probe)
            {
                var cell = Vector3Int.FloorToInt(probe / cellSize);
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                for (var z = -1; z <= 1; z++)
                    if (cells.TryGetValue(cell + new Vector3Int(x, y, z), out var near) && near.Any(q => (q - probe).sqrMagnitude < merge2))
                        return;
                Positions.Add(probe);
                if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<Vector3>();
                list.Add(probe);
            }
        }
    }
}
