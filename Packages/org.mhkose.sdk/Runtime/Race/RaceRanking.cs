using System.Collections.Generic;
using MHK.Karts;

namespace MHK.Race
{
    /// <summary>
    /// Race order: the mode's own order (<see cref="BattleRules"/>, <see cref="LastManStandingRules"/>) when it has one,
    /// else finishers by time, then by race distance.
    /// </summary>
    sealed class RaceRanking : IComparer<Kart>
    {
        readonly List<Kart> order = new();
        IComparer<Kart> modeOrder;

        public IReadOnlyList<Kart> Order => order;

        /// <summary>Sorts <paramref name="karts"/> and writes each racer's position (allocation-free).</summary>
        public void Update(IReadOnlyList<Kart> karts, IComparer<Kart> rules)
        {
            order.Clear();
            for (var i = 0; i < karts.Count; i++) order.Add(karts[i]);
            modeOrder = rules;
            order.Sort(this);
            for (var i = 0; i < order.Count; i++) order[i].Progress.Position = i + 1;
        }

        public int Compare(Kart a, Kart b)
        {
            if (modeOrder != null) return modeOrder.Compare(a, b);
            var pa = a.Progress;
            var pb = b.Progress;
            if (pa.Finished && pb.Finished) return pa.FinishTime.CompareTo(pb.FinishTime);
            if (pa.Finished) return -1;
            if (pb.Finished) return 1;
            return pb.RaceDistance.CompareTo(pa.RaceDistance);
        }
    }
}
