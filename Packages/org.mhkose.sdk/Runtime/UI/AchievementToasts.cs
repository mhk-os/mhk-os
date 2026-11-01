using System.Collections.Generic;
using MHK.Achievements;
using UnityEngine;
using UnityEngine.UIElements;
using AchievementList = MHK.Achievements.Achievements;

namespace MHK.UI
{
    /// <summary>
    /// "Achievement unlocked" toasts (icon, head line, title) at the top centre of whatever is on screen: the open menu
    /// page, else the race results, else the race HUD. Unlocks queue and show one at a time for a few seconds; a toast
    /// moves along when the page changes (rebuilt there: a page's elements are released with it). Lives on the
    /// <see cref="Core.GameRoot"/>.
    /// </summary>
    [AddComponentMenu("")]
    public class AchievementToasts : MonoBehaviour
    {
        const float ShowSeconds = 4.5f, FadeSeconds = 0.4f, SearchSeconds = 0.5f;
        const float PopScale = 1.12f;
        const int PopMs = 280;

        [SerializeField, Tooltip("Line above the achievement's title.")]
        string headText = "Achievement unlocked";

        readonly Queue<AchievementView> queue = new();
        UiScreen host;
        VisualElement anchor, toast;
        // The achievement the toast shows until `until`, null when none shows.
        AchievementView shown;
        float until, nextSearch;

        void OnEnable() => AchievementList.Unlocked += Enqueue;

        void OnDisable()
        {
            AchievementList.Unlocked -= Enqueue;
            queue.Clear();
            anchor?.RemoveFromHierarchy();
            anchor = toast = null;
            shown = null;
            host = null;
        }

        void Enqueue(AchievementView view)
        {
            if (view != null) queue.Enqueue(view);
        }

        void Update()
        {
            if (shown == null && queue.Count == 0) return;
            if (!Attach()) return;
            var now = Time.unscaledTime;
            if (shown == null) Show(queue.Dequeue(), now);
            var left = until - now;
            if (left <= 0f)
            {
                toast.RemoveFromHierarchy();
                toast = null;
                shown = null;
                return;
            }
            toast.style.opacity = Mathf.Clamp01(left / FadeSeconds);
        }

        // The anchor follows the screen on top (pages are swapped and rebuilt); searched at most every SearchSeconds. The
        // old page released its elements, the running toast among them, so the new anchor gets a new one.
        bool Attach()
        {
            if (host != null && host.IsBound && host.isActiveAndEnabled && anchor != null && anchor.parent == host.Root) return true;
            var now = Time.unscaledTime;
            if (now < nextSearch) return false;
            nextSearch = now + SearchSeconds;
            host = FindHost();
            anchor?.RemoveFromHierarchy();
            anchor = toast = null;
            if (host == null) return false;
            anchor = new VisualElement { name = "achievement-toasts", pickingMode = PickingMode.Ignore };
            anchor.AddToClassList("ach-toast-anchor");
            anchor.EnableInClassList("in-race", host is RaceHud);
            host.Root.Add(anchor);
            if (shown != null) toast = Build(shown);
            return true;
        }

        static UiScreen FindHost()
        {
            UiScreen best = null;
            var bestRank = 0;
            foreach (var s in FindObjectsByType<UiScreen>())
            {
                if (!s.IsBound) continue;
                var rank = s is MenuScreen ? 3 : s is RaceResults ? 2 : s is RaceHud ? 1 : 0;
                if (rank <= bestRank) continue;
                best = s;
                bestRank = rank;
            }
            return best;
        }

        void Show(AchievementView view, float now)
        {
            shown = view;
            toast = Build(view);
            UiAnim.Pop(toast, PopScale, PopMs);
            until = now + ShowSeconds;
        }

        // The toast of `view` in the anchor.
        VisualElement Build(AchievementView view)
        {
            var t = new VisualElement();
            t.AddToClassList("ach-toast");
            var medal = new VisualElement();
            medal.AddToClassList("ach-medal");
            var icon = new VisualElement();
            icon.AddToClassList("icon");
            icon.AddToClassList("ach-icon");
            AchievementsScreen.SetIcon(icon, view.Icon, view.IconName);
            medal.Add(icon);
            t.Add(medal);
            var text = new VisualElement();
            text.AddToClassList("ach-text");
            var head = new Label(headText);
            head.AddToClassList("ach-toast-head");
            var title = new Label(view.Title);
            title.AddToClassList("ach-toast-title");
            text.Add(head);
            text.Add(title);
            t.Add(text);
            t.NoPick();
            anchor.Add(t);
            return t;
        }
    }
}
