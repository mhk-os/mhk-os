using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Powerups;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.AI
{
    /// <summary>What an item box gives, as far as a computer driver in a battle cares.</summary>
    internal enum BattleBoxKind : byte
    {
        /// <summary>A powerup for the item slot.</summary>
        Item,
        /// <summary>A status effect that restores health (Thunder health pickup).</summary>
        Health,
        /// <summary>A status effect that raises the damage the kart deals (Thunder quad damage).</summary>
        Power,
        /// <summary>Any other status effect.</summary>
        Other,
        /// <summary>No ground under the box (outside the drivable area): never driven to.</summary>
        Unreachable,
    }

    /// <summary>
    /// Item boxes of the current track for battles, cached per track: what each gives (boxes without ground under them
    /// are unreachable), where it lies on the main line, and stations (groups of powerup boxes whose centres are at least
    /// <see cref="GameConfig.aiBattleStationSpacing"/> apart).
    /// </summary>
    internal static partial class AIBattleBoxes
    {
        const float GroundAbove = 0.5f, GroundBelow = 3f;

        [AutoStaticsCleanup] static readonly Dictionary<ItemBox, BattleBoxKind> kinds = new();
        [AutoStaticsCleanup] static readonly Dictionary<ItemBox, float> lineDistances = new();
        [AutoStaticsCleanup] static TrackRuntime cacheTrack;
        [AutoStaticsCleanup] static readonly List<ItemBox> stationBoxes = new();
        [AutoStaticsCleanup] static readonly List<int> stationOfBox = new();
        [AutoStaticsCleanup] static readonly List<Vector3> sums = new();
        [AutoStaticsCleanup] static readonly List<int> counts = new();
        [AutoStaticsCleanup] static readonly List<Vector3> centres = new();
        [AutoStaticsCleanup] static readonly List<float> centreLine = new();
        [AutoStaticsCleanup] static int stationActiveBoxes = -1;
        [AutoStaticsCleanup] static float stationSpacing = -1f;

        /// <summary>What <paramref name="box"/> gives.</summary>
        public static BattleBoxKind KindOf(ItemBox box)
        {
            Refresh();
            if (kinds.TryGetValue(box, out var kind)) return kind;
            kind = Classify(box);
            kinds[box] = kind;
            return kind;
        }

        /// <summary>Distance of <paramref name="box"/> along the main line of <paramref name="track"/>.</summary>
        public static float LineDistance(TrackRuntime track, ItemBox box)
        {
            Refresh();
            if (lineDistances.TryGetValue(box, out var d)) return d;
            d = track.path.Project(box.transform.position);
            lineDistances[box] = d;
            return d;
        }

        /// <summary>Number of stations on <paramref name="track"/> for a station spacing of <paramref name="spacing"/> metres.</summary>
        public static int StationCount(TrackRuntime track, float spacing)
        {
            Refresh();
            if (ItemBox.Active.Count != stationActiveBoxes || spacing != stationSpacing) BuildStations(track, spacing);
            return centres.Count;
        }

        /// <summary>Centre of station <paramref name="station"/> (after <see cref="StationCount"/>).</summary>
        public static Vector3 StationCentre(int station) => centres[station];

        /// <summary>Main-line distance of the centre of station <paramref name="station"/>.</summary>
        public static float StationLineDistance(int station) => centreLine[station];

        /// <summary>The available box of station <paramref name="station"/> nearest to <paramref name="from"/>, null when all are taken.</summary>
        public static ItemBox NearestAvailable(int station, Vector3 from)
        {
            ItemBox best = null;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < stationBoxes.Count; i++)
            {
                var b = stationBoxes[i];
                if (stationOfBox[i] != station || b == null || !b.Available || !b.isActiveAndEnabled) continue;
                var distance = (b.transform.position - from).sqrMagnitude;
                if (distance >= bestDistance) continue;
                best = b;
                bestDistance = distance;
            }
            return best;
        }

        static void Refresh()
        {
            if (cacheTrack == TrackRuntime.Current) return;
            kinds.Clear();
            lineDistances.Clear();
            stationActiveBoxes = -1;
            cacheTrack = TrackRuntime.Current;
        }

        static BattleBoxKind Classify(ItemBox box)
        {
            var track = TrackRuntime.Current;
            if (track != null && !GroundProbe.Down(box.transform.position, GroundAbove, GroundBelow, track.ObstacleMask, out _)) return BattleBoxKind.Unreachable;
            if (box.GivesPowerups()) return BattleBoxKind.Item;
            var effect = GameRoot.Instance.Content.Get<StatusEffectDefinition>(box.fixedPowerupId);
            if (effect == null) return BattleBoxKind.Other;
            if (effect.heal > 0) return BattleBoxKind.Health;
            return effect.damageDealtMultiplier > 1f ? BattleBoxKind.Power : BattleBoxKind.Other;
        }

        // Every powerup box starts as its own group, then the two groups with the closest centres merge until all are at
        // least spacing apart. Built once per track.
        static void BuildStations(TrackRuntime track, float spacing)
        {
            stationActiveBoxes = ItemBox.Active.Count;
            stationSpacing = spacing;
            stationBoxes.Clear();
            stationOfBox.Clear();
            sums.Clear();
            counts.Clear();
            for (var i = 0; i < ItemBox.Active.Count; i++)
            {
                var b = ItemBox.Active[i];
                if (KindOf(b) != BattleBoxKind.Item) continue;
                stationBoxes.Add(b);
                stationOfBox.Add(sums.Count);
                sums.Add(b.transform.position);
                counts.Add(1);
            }
            while (MergeClosest(spacing)) { }
            Compact(track);
        }

        static bool MergeClosest(float spacing)
        {
            int a = -1, b = -1;
            var best = spacing * spacing;
            for (var i = 0; i < sums.Count; i++)
            {
                if (counts[i] == 0) continue;
                for (var j = i + 1; j < sums.Count; j++)
                {
                    if (counts[j] == 0) continue;
                    var d = (sums[i] / counts[i] - sums[j] / counts[j]).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    a = i;
                    b = j;
                }
            }
            if (a < 0) return false;
            sums[a] += sums[b];
            counts[a] += counts[b];
            counts[b] = 0;
            for (var i = 0; i < stationOfBox.Count; i++)
                if (stationOfBox[i] == b) stationOfBox[i] = a;
            return true;
        }

        static void Compact(TrackRuntime track)
        {
            centres.Clear();
            centreLine.Clear();
            for (var g = 0; g < sums.Count; g++)
            {
                if (counts[g] == 0) continue;
                var station = centres.Count;
                centres.Add(sums[g] / counts[g]);
                centreLine.Add(track.path.Project(centres[station]));
                for (var i = 0; i < stationOfBox.Count; i++)
                    if (stationOfBox[i] == g) stationOfBox[i] = -1 - station;
            }
            for (var i = 0; i < stationOfBox.Count; i++) stationOfBox[i] = -1 - stationOfBox[i];
        }
    }
}
