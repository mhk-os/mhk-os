using System.Collections.Generic;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>How a notice of <see cref="OnlineNotices"/> looks.</summary>
    public enum NoticeKind
    {
        /// <summary>Dark plate: something about another racer.</summary>
        Info,
        /// <summary>Accent plate: something that happened to the player.</summary>
        Bad,
    }

    /// <summary>
    /// Online races: short notices stacked at the top centre of the race HUD, above the countdown: racers removed from
    /// the race (cheating detected, desync, kicked by a moderator, left), the player's own removal, and stand-ins for
    /// content this game does not have. Notices posted before the HUD exists wait for it.
    /// </summary>
    [AddComponentMenu("")]
    public partial class OnlineNotices : MonoBehaviour
    {
        const float ShowSeconds = 6f, OwnShowSeconds = 10f, FadeSeconds = 0.5f;
        const int MaxShown = 4;
        const float PopScale = 1.15f;
        const int PopMs = 260;

        [AutoStaticsCleanup] static readonly List<(string text, NoticeKind kind)> pending = new();

        sealed class Shown
        {
            public VisualElement plate;
            public float until;
        }

        OnlineRace race;
        RaceHud hud;
        VisualElement root, anchor;
        readonly List<Shown> shown = new();

        /// <summary>Shows <paramref name="text"/> for a few seconds (queued until the race HUD is there).</summary>
        public static void Post(string text, NoticeKind kind)
        {
            if (!string.IsNullOrEmpty(text)) pending.Add((text, kind));
        }

        void Start()
        {
            race = OnlineRace.Current;
            if (race != null) race.Pushed += OnPushed;
        }

        void OnDestroy()
        {
            if (race != null) race.Pushed -= OnPushed;
            pending.Clear();
            anchor?.RemoveFromHierarchy();
        }

        void OnPushed(IRacePush push)
        {
            if (push is not PlayerLeftRace left || left.raceId != race.RaceId) return;
            if (left.slot == race.LocalSlot)
            {
                var own = OwnText(left.outcome);
                if (own != null) Post(string.IsNullOrEmpty(left.message) ? own : $"{own} ({left.message})", NoticeKind.Bad);
                return;
            }
            var text = OtherText(left.outcome, NameOf(left.slot));
            if (text != null) Post(text, NoticeKind.Info);
        }

        static string OwnText(int outcome) => outcome switch
        {
            PlayerLeftRace.KickedCheating => "You were removed from the race: cheating detected",
            PlayerLeftRace.KickedModerator => "You were kicked by a moderator",
            PlayerLeftRace.Desync => "You were removed from the race: your game is out of sync with the others (desync)",
            _ => null,
        };

        static string OtherText(int outcome, string name) => outcome switch
        {
            PlayerLeftRace.KickedCheating => $"{name} was removed: cheating detected",
            PlayerLeftRace.KickedModerator => $"{name} was kicked by a moderator",
            PlayerLeftRace.Desync => $"{name} was removed: desync",
            PlayerLeftRace.Left => $"{name} left the race",
            _ => null,
        };

        string NameOf(int slot)
        {
            var grid = race.Setup != null ? race.Setup.grid : null;
            if (grid != null)
                foreach (var s in grid)
                    if (s.slot == slot && !string.IsNullOrEmpty(s.username)) return s.username;
            return $"Racer {slot + 1}";
        }

        void Update()
        {
            if (pending.Count == 0 && shown.Count == 0) return;
            if (!Attach()) return;
            var now = Time.unscaledTime;
            foreach (var (text, kind) in pending) Add(text, kind, now);
            pending.Clear();
            for (var i = shown.Count - 1; i >= 0; i--)
            {
                var s = shown[i];
                var left = s.until - now;
                if (left <= 0f)
                {
                    s.plate.RemoveFromHierarchy();
                    shown.RemoveAt(i);
                    continue;
                }
                s.plate.style.opacity = Mathf.Clamp01(left / FadeSeconds);
            }
        }

        // The HUD document can be rebuilt (layout changes, UI reloads): the stack follows its current root.
        bool Attach()
        {
            if (hud == null) hud = FindAnyObjectByType<RaceHud>();
            if (hud == null || !hud.IsBound) return false;
            if (root == hud.Root && anchor != null && anchor.panel != null) return true;
            root = hud.Root;
            anchor?.RemoveFromHierarchy();
            anchor = new VisualElement { name = "online-notices", pickingMode = PickingMode.Ignore };
            var st = anchor.style;
            st.position = Position.Absolute;
            st.left = 0;
            st.right = 0;
            st.top = 18;
            st.alignItems = Align.Center;
            root.Add(anchor);
            foreach (var s in shown) anchor.Add(s.plate);
            return true;
        }

        void Add(string text, NoticeKind kind, float now)
        {
            while (shown.Count >= MaxShown)
            {
                shown[0].plate.RemoveFromHierarchy();
                shown.RemoveAt(0);
            }
            var plate = new VisualElement { pickingMode = PickingMode.Ignore };
            plate.AddToClassList("hud-flash");
            if (kind == NoticeKind.Bad) plate.AddToClassList("bad");
            var ps = plate.style;
            ps.paddingTop = 4;
            ps.paddingBottom = 10;
            ps.paddingLeft = 28;
            ps.paddingRight = 28;
            ps.marginBottom = 6;
            ps.maxWidth = Length.Percent(70);
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("hud-flash-label");
            label.style.fontSize = 26;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            plate.Add(label);
            anchor.Add(plate);
            UiAnim.Pop(plate, PopScale, PopMs);
            shown.Add(new Shown { plate = plate, until = now + (kind == NoticeKind.Bad ? OwnShowSeconds : ShowSeconds) });
        }
    }
}
