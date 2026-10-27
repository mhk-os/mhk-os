using System.Collections.Generic;
using System.Globalization;
using MHK.Online;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// The small rank tags (<c>#12</c>, <c>.player-rank</c>) next to the player names of one screen: each tag asks
    /// <see cref="OnlineProfiles"/> for its account's summary, shows the cached rank (hidden while unknown or unranked)
    /// and follows <see cref="OnlineProfiles.Changed"/>. While attached, the shown accounts are asked again every
    /// <see cref="OnlineProfiles.SummaryMaxAge"/> seconds.
    /// </summary>
    public sealed class RankTags
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        readonly List<(Label tag, long accountId)> tags = new();
        PlayerCardTexts texts;
        IVisualElementScheduledItem refresh;

        /// <summary>Starts following the cache (in a screen's Bind); <paramref name="root"/> schedules the refreshes.</summary>
        public void Attach(VisualElement root)
        {
            Detach();
            texts = PlayerCard.Texts;
            OnlineProfiles.Changed += Refresh;
            refresh = root.schedule.Execute(WantAll).Every((long)(OnlineProfiles.SummaryMaxAge * 1000f));
        }

        /// <summary>Stops following the cache and forgets the tags (in a screen's Unbind).</summary>
        public void Detach()
        {
            OnlineProfiles.Changed -= Refresh;
            refresh?.Pause();
            refresh = null;
            tags.Clear();
        }

        /// <summary>Shows the rank of <paramref name="accountId"/> in <paramref name="tag"/> (now when cached, else once it arrived).</summary>
        public void Add(Label tag, long accountId)
        {
            if (tag == null) return;
            tag.AddToClassList("player-rank");
            tags.Add((tag, accountId));
            Show(tag, accountId);
            OnlineProfiles.Want(accountId);
        }

        /// <summary>A new tag next to <paramref name="name"/> (lists built in code).</summary>
        public Label AddAfter(VisualElement name, long accountId)
        {
            var tag = new Label { pickingMode = PickingMode.Ignore };
            name.parent.Insert(name.parent.IndexOf(name) + 1, tag);
            Add(tag, accountId);
            return tag;
        }

        void Refresh()
        {
            tags.RemoveAll(t => t.tag.panel == null);
            foreach (var (tag, id) in tags) Show(tag, id);
        }

        void WantAll()
        {
            tags.RemoveAll(t => t.tag.panel == null);
            foreach (var (_, id) in tags) OnlineProfiles.Want(id);
        }

        void Show(Label tag, long accountId)
        {
            var ranked = OnlineProfiles.TryGetSummary(accountId, out var s) && s.rank > 0;
            tag.SetVisible(ranked);
            if (!ranked) return;
            var x = texts ?? PlayerCard.Texts;
            tag.text = string.Format(Invariant, x.rankFormat, s.rank);
            tag.tooltip = string.Format(Invariant, x.rankTipFormat, s.rank, s.score);
        }
    }
}
