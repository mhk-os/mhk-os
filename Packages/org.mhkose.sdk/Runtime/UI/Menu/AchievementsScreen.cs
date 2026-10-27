using System.Collections.Generic;
using System.Globalization;
using MHK.Achievements;
using MHK.Online;
using UnityEngine;
using UnityEngine.UIElements;
using AchievementList = MHK.Achievements.Achievements;

namespace MHK.UI
{
    /// <summary>
    /// Achievements page (Achievements.uxml): the achievements of the enabled mods as cards in their order (icon, title,
    /// description, progress, unlock date and the share of players who have it), the unlocked count and whether they
    /// sync with the online account (Log in opens the <see cref="AccountScreen"/>). Opening it asks the server for the
    /// shares; the cards follow <see cref="MHK.Achievements.Achievements.Changed"/>.
    /// </summary>
    public class AchievementsScreen : MenuScreen
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        [SerializeField, Tooltip("One achievement (Templates/AchievementCard.uxml).")]
        VisualTreeAsset cardTemplate;
        [SerializeField, Tooltip("Header count. {0} = unlocked, {1} = shown.")]
        string countFormat = "{0} / {1} unlocked";
        [SerializeField, Tooltip("Sync line: no account, a stored login not online yet ({0} = username), syncing ({0}), synced ({0}).")]
        string loggedOutText = "Log in to sync your achievements", offlineFormat = "Not connected: they sync with {0} once online",
            syncingFormat = "Syncing with {0}...", syncedFormat = "Synced with {0}";
        [SerializeField, Tooltip("Unlock date on a card ({0} = date) and its format (invariant culture).")]
        string unlockedFormat = "Unlocked {0}", dateFormat = "d MMM yyyy";
        [SerializeField, Tooltip("Share of the players who unlocked it ({0} = percentage) and its tooltip.")]
        string shareFormat = "{0}% of players", shareTip = "Of all players who synced achievements";
        [SerializeField, Tooltip("Icon (Material Symbols name) of a secret achievement until it is unlocked.")]
        string secretIcon = "lock";

        readonly List<(string id, VisualElement card)> cards = new();
        VisualElement list, sync, syncIcon;
        Label count, syncLabel, state;
        Button login;

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            list = Q<VisualElement>("ach-list");
            var scroll = Q<ScrollView>("ach-scroll");
            count = Q<Label>("ach-count");
            sync = Q<VisualElement>("ach-sync");
            syncIcon = Q<VisualElement>("ach-sync-icon");
            syncLabel = Q<Label>("ach-sync-label");
            state = Q<Label>("ach-state");
            login = OnClick("ach-login", () => Flow.Show<AccountScreen>());
            KeepFocusedInView(scroll, list);
            LeaderRows.NoScrollbarFocus(scroll);
            AchievementList.Changed += Show;
            OnlineService.Changed += ShowSync;
            Show();
            if (cards.Count > 0) FocusTarget = cards[0].card;
            AchievementList.RefreshShares();
        }

        protected override void Unbind()
        {
            base.Unbind();
            AchievementList.Changed -= Show;
            OnlineService.Changed -= ShowSync;
            cards.Clear();
            list = sync = syncIcon = null;
            count = syncLabel = state = null;
            login = null;
        }

        // Same achievements as shown: the cards are refreshed in place (scroll and focus stay), else built again.
        void Show()
        {
            if (list == null) return;
            var views = AchievementList.Visible;
            if (!SameCards(views))
            {
                var focusedId = FocusedCardId();
                list.Clear();
                cards.Clear();
                foreach (var v in views)
                {
                    var card = UiTree.Spawn<VisualElement>(cardTemplate, list);
                    if (card == null) continue;
                    cards.Add((v.Id, card));
                    if (v.Id == focusedId) FocusLater(card);
                }
            }
            var i = 0;
            foreach (var v in views)
                if (i < cards.Count && cards[i].id == v.Id) Fill(cards[i++].card, v);
            state.SetVisible(views.Count == 0);
            count.text = string.Format(Invariant, countFormat, AchievementList.UnlockedVisibleCount, views.Count);
            ShowSync();
        }

        bool SameCards(IReadOnlyList<AchievementView> views)
        {
            if (views.Count != cards.Count) return false;
            for (var i = 0; i < views.Count; i++)
                if (views[i].Id != cards[i].id) return false;
            return true;
        }

        string FocusedCardId()
        {
            var focused = list.focusController?.focusedElement as VisualElement;
            foreach (var (id, card) in cards)
                if (card == focused) return id;
            return null;
        }

        void Fill(VisualElement card, AchievementView v)
        {
            var hidden = v.Secret && !v.Unlocked;
            card.EnableInClassList("unlocked", v.Unlocked);
            card.EnableInClassList("secret", hidden);
            SetIcon(card.Q("ach-icon"), hidden ? null : v.Icon, hidden ? secretIcon : v.IconName);
            card.SetText("ach-title", v.Title);
            card.SetText("ach-desc", v.Description);
            card.SetText("ach-date", v.Unlocked && v.UnlockedAt != default
                ? string.Format(unlockedFormat, v.UnlockedAt.ToString(dateFormat, Invariant)) : "");
            card.Q("ach-bar-fill").style.width = Length.Percent(Mathf.Clamp01(v.Progress) * 100f);
            card.SetText("ach-progress", v.ProgressText);
            var share = card.Q<Label>("ach-share");
            share.SetVisible(v.PlayerShare.HasValue);
            if (v.PlayerShare.HasValue) share.text = string.Format(shareFormat, Percent(v.PlayerShare.Value));
            share.tooltip = shareTip;
        }

        /// <summary>The mod's picture (class picture, untinted) when there is one, else the icon class i-<paramref name="iconName"/>.</summary>
        internal static void SetIcon(VisualElement icon, Texture2D picture, string iconName)
        {
            icon.EnableInClassList("picture", picture != null);
            icon.SetIcon(iconName);
            // The inline picture covers the class's icon.
            if (picture != null) icon.SetImage(picture);
            else icon.style.backgroundImage = StyleKeyword.Null;
        }

        static string Percent(float share)
        {
            var p = Mathf.Clamp01(share) * 100f;
            if (p > 0f && p < 0.1f) return "<0.1";
            return p.ToString(p < 10f ? "0.#" : "0", Invariant);
        }

        void ShowSync()
        {
            if (sync == null) return;
            var name = OnlineService.Username;
            var synced = AchievementList.Synced;
            sync.EnableInClassList("synced", synced);
            login.SetVisible(name == null);
            if (name == null)
            {
                syncIcon.SetIcon("cloud_off");
                syncLabel.text = loggedOutText;
            }
            else if (!OnlineService.IsLoggedIn)
            {
                syncIcon.SetIcon("cloud_off");
                syncLabel.text = string.Format(offlineFormat, name);
            }
            else
            {
                syncIcon.SetIcon(synced ? "cloud_done" : "sync");
                syncLabel.text = string.Format(synced ? syncedFormat : syncingFormat, name);
            }
        }
    }
}
