using System.Collections.Generic;
using System.Linq;
using MHK.Race;
using UnityEngine;

namespace MHK.Editor.Tracks
{
    public enum TrackIssueSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>One validation finding. <see cref="ToString"/> gives "Error: ...", "Warning: ..." or "Info: ...".</summary>
    public readonly struct TrackIssue
    {
        public TrackIssueSeverity Severity { get; }
        public string Message { get; }

        public TrackIssue(TrackIssueSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }

        public static TrackIssue Error(string message) => new(TrackIssueSeverity.Error, message);
        public static TrackIssue Warning(string message) => new(TrackIssueSeverity.Warning, message);
        public static TrackIssue Info(string message) => new(TrackIssueSeverity.Info, message);

        public override string ToString() => $"{Severity}: {Message}";
    }

    /// <summary>Checks a track's racing lines for broken lap counting, progress and suspicious data.</summary>
    internal static class TrackPathValidator
    {
        const float NarrowWidthMetres = 1f;
        const float DuplicateNodeMetres = 0.01f;
        const float RouteEndSnapMetres = 2f;
        const float CheckpointFinishMetres = 10f;
        const float GridPastFinishMetres = 0.5f;

        /// <summary>Validation findings of <paramref name="runtime"/> in a fixed order.</summary>
        public static List<TrackIssue> Validate(TrackRuntime runtime)
        {
            var issues = new List<TrackIssue>();
            if (runtime == null) return issues;
            runtime.InvalidateLines();
            var lines = runtime.GetComponentsInChildren<TrackPath>();
            var path = runtime.MainPath;
            if (path == null)
            {
                issues.Add(TrackIssue.Error("no closed racing line below the TrackRuntime: tick Closed on the main line."));
                return issues;
            }
            var closed = lines.Where(l => l.closed).ToList();
            if (closed.Count > 1)
                issues.Add(TrackIssue.Error($"{closed.Count} closed racing lines ({string.Join(", ", closed.Select(l => l.name))}): only the first, " +
                                            $"{path.name}, is the main line. Untick Closed on the others to make them alternative routes."));
            path.Invalidate();
            if (!ValidateMainLine(path, issues)) return issues;

            var sections = new List<(int index, float start, float span)>();
            foreach (var line in lines)
                if (!line.closed && ValidateRoute(runtime, path, line, issues) is { } section)
                    sections.Add(section);
            ValidateCheckpoints(path, sections, issues);
            issues.Add(LapSummary(path));
            ValidateStartGrid(runtime, path, issues);
            ValidateKillHeight(runtime, path, issues);
            return issues;
        }

        static bool ValidateMainLine(TrackPath path, List<TrackIssue> issues)
        {
            if (!path.closed) issues.Add(TrackIssue.Error("the main path must be closed."));
            if (path.Count < 3)
            {
                issues.Add(TrackIssue.Error("the main path needs at least 3 nodes."));
                return false;
            }
            var nodes = path.Nodes;
            if ((nodes[0].flags & TrackNodeFlags.Finish) == 0)
                issues.Add(TrackIssue.Error("node 0 is not flagged Finish (node 0 must lie on the start/finish line; use 'Set as finish')."));
            var extraFinish = Enumerable.Range(1, nodes.Count - 1).Where(i => (nodes[i].flags & TrackNodeFlags.Finish) != 0).ToList();
            if (extraFinish.Count > 0) issues.Add(TrackIssue.Error($"only node 0 may be the finish; also flagged: {string.Join(", ", extraFinish)}."));
            var narrow = nodes.Count(n => n.width < NarrowWidthMetres);
            if (narrow > 0) issues.Add(TrackIssue.Warning($"{narrow} main-path nodes have a road width below 1 m."));
            var duplicates = Enumerable.Range(0, nodes.Count)
                .Count(i => Vector3.Distance(nodes[i].position, nodes[(i + 1) % nodes.Count].position) < DuplicateNodeMetres);
            if (duplicates > 0) issues.Add(TrackIssue.Warning($"{duplicates} zero-length main-path segments (duplicate nodes)."));
            return true;
        }

        static (int index, float start, float span)? ValidateRoute(TrackRuntime runtime, TrackPath path, TrackPath alt, List<TrackIssue> issues)
        {
            alt.Invalidate();
            if (alt.Count >= 2) ValidateRouteEnds(path, alt, issues);
            var problem = TrackRuntime.RouteProblem(path, alt);
            if (problem != null)
            {
                var hint = problem == TrackRuntime.ReversedRouteProblem ? " (use 'Reverse direction')" : "";
                issues.Add(TrackIssue.Error($"{alt.name} is no alternative route, races leave it out: {problem}{hint}."));
                return null;
            }
            var r = runtime.RouteIndex(alt);
            var len = path.Length;
            TrackRuntime.ComputeJoin(path, alt, out var start, out var span);
            if (start + span > len)
                issues.Add(TrackIssue.Warning($"route {r} ({alt.name}) spans the finish line ({start:0} -> {path.WrapDistance(start + span):0} m)."));
            if (alt.Nodes.Any(n => n.flags != TrackNodeFlags.None))
                issues.Add(TrackIssue.Warning($"route {r} ({alt.name}) has flagged nodes; finish and checkpoints only count on the main path."));
            issues.Add(TrackIssue.Info($"route {r} ({alt.name}): fork {start:0} m, merge {path.WrapDistance(start + span):0} m, bypasses {span:0} m with {alt.Length:0} m."));
            return (r, start, span);
        }

        static void ValidateRouteEnds(TrackPath path, TrackPath alt, List<TrackIssue> issues)
        {
            foreach (var end in new[] { 0, alt.Count - 1 })
            {
                var hint = -1;
                path.Project(alt.GetNodePosition(end), ref hint, out _, out var score);
                if (score > RouteEndSnapMetres)
                    issues.Add(TrackIssue.Warning($"{alt.name}: its {(end == 0 ? "first" : "last")} node is {score:0.0} m off the main line (use 'Snap ends to main line')."));
            }
        }

        static void ValidateCheckpoints(TrackPath path, List<(int index, float start, float span)> sections, List<TrackIssue> issues)
        {
            var len = path.Length;
            var checkpoints = path.CheckpointCount;
            if (checkpoints == 0) issues.Add(TrackIssue.Warning("no key checkpoints; every forward crossing of the finish line counts a lap."));
            for (var k = 0; k < checkpoints; k++)
            {
                var d = path.GetCheckpointDistance(k);
                if (d < CheckpointFinishMetres || d > len - CheckpointFinishMetres)
                    issues.Add(TrackIssue.Warning($"key checkpoint {k} at {d:0} m is very close to the finish line."));
                foreach (var s in sections)
                {
                    var rel = path.WrapDistance(d - s.start);
                    if (rel > 0f && rel < s.span)
                        issues.Add(TrackIssue.Error($"route {s.index} bypasses key checkpoint {k} at {d:0} m: laps driven via that route never count."));
                }
            }
        }

        static TrackIssue LapSummary(TrackPath path)
        {
            var checkpoints = path.CheckpointCount;
            return TrackIssue.Info($"lap {path.Length:0} m, {path.Count} nodes, key checkpoints at " +
                                   (checkpoints == 0 ? "-" : string.Join(", ", Enumerable.Range(0, checkpoints).Select(k => $"{path.GetCheckpointDistance(k):0} m"))) + ".");
        }

        static void ValidateStartGrid(TrackRuntime runtime, TrackPath path, List<TrackIssue> issues)
        {
            var len = path.Length;
            for (var g = 0; g < runtime.startGrid.Count; g++)
            {
                var slot = runtime.startGrid[g];
                if (slot == null) continue;
                var hint = -1;
                var d = path.Project(slot.position, ref hint, out var lateral);
                if (d > GridPastFinishMetres && d < len * 0.5f) issues.Add(TrackIssue.Warning($"grid slot {g} is {d:0.0} m past the finish line."));
                if (lateral > path.GetWidth(d)) issues.Add(TrackIssue.Warning($"grid slot {g} is {lateral:0.0} m from the racing line."));
            }
        }

        static void ValidateKillHeight(TrackRuntime runtime, TrackPath path, List<TrackIssue> issues)
        {
            var lowest = Enumerable.Range(0, path.Count).Min(i => path.GetNodePosition(i).y);
            if (runtime.killHeight > lowest)
                issues.Add(TrackIssue.Warning($"kill height {runtime.killHeight:0.0} is above the lowest racing-line point ({lowest:0.0})."));
        }
    }
}
