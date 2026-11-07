using System;
using MHK.Modding;
using MHK.Powerups;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>
        /// Item slot (top left): the held powerup's icon over the empty slot, its name and charges, the roulette while an
        /// item box rolls. The roulette runs on scaled time, so it freezes with the pause. A battle item that still loads
        /// shows grayed under a pie timer: it pops in small when revealed and with the full pop and sunburst once usable.
        /// </summary>
        sealed class ItemSlot
        {
            const int Empty = 0, Rolling = 1, Held = 2;
            const float FramePopScale = 1.5f, RevealPopScale = 1.15f, ChargesPopScale = 1.4f;
            const int FramePopMs = 420, RevealPopMs = 260, ChargesPopMs = 300, BurstMs = 750;
            const float RouletteIconsPerSecond = 12f;
            const float WobbleSpeed = 31f, WobbleDegrees = 7f, PulseSpeed = 19f, PulseAmount = 0.06f;
            const float BurstAlpha = 0.9f, BurstStartScale = 0.55f, BurstGrowth = 0.65f, BurstDegrees = 80f;

            static readonly Action<VisualElement, float> BurstFrame = (e, v) =>
            {
                e.style.opacity = (1f - v) * BurstAlpha;
                var s = BurstStartScale + BurstGrowth * v;
                e.style.scale = new Scale(new Vector3(s, s, 1f));
                e.style.rotate = new Rotate(Angle.Degrees(BurstDegrees * v));
            };

            readonly RaceHud hud;
            VisualElement frame, icon, burst, charges;
            HudItemLoading loader;
            Label chargesText, itemName;
            int shownState, shownCooling, shownLoading, shownCharges;
            bool iconValid, iconShown;
            Texture2D shownIcon;
            PowerupDefinition shownItem;

            public ItemSlot(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                burst = hud.Q<VisualElement>("item-burst");
                frame = hud.Q<VisualElement>("item-frame");
                icon = hud.Q<VisualElement>("item-icon");
                charges = hud.Q<VisualElement>("item-charges");
                chargesText = hud.Q<Label>("item-charges-label");
                itemName = hud.Q<Label>("item-name");
                loader = hud.Q<HudItemLoading>("item-loading");
                burst.style.opacity = 0f;
                charges.SetVisible(false);
                itemName.SetVisible(false);
                loader.SetVisible(false);
                shownState = shownCooling = shownLoading = shownCharges = -1;
                iconValid = iconShown = false;
                shownIcon = null;
                shownItem = null;
            }

            public void Refresh(PowerupInventory inventory)
            {
                var held = inventory.Held;
                // Online the roulette spins before the server granted an item (nothing held yet).
                var state = inventory.Rolling ? Rolling : held == null ? Empty : Held;
                var loading = state == Held && inventory.Loading ? 1 : 0;
                if (state != shownState) OnStateChanged(shownState, state, loading == 1);
                else if (loading < shownLoading && state == Held) Appear();
                if (state == Empty)
                {
                    SetIcon(false, null);
                    SetLabel(null, 0);
                }
                else if (state == Rolling) ShowRoulette();
                else
                {
                    SetIcon(true, held.Icon != null ? held.Icon : hud.unknownItemIcon);
                    SetLabel(held, inventory.Charges);
                }
                if (loading == 1) loader.SetProgress(inventory.LoadProgress01);
                var cooling = state == Held && (inventory.CoolingDown || loading == 1) ? 1 : 0;
                if (loading != shownLoading)
                {
                    shownLoading = loading;
                    loader.SetVisible(loading == 1);
                }
                if (cooling == shownCooling) return;
                shownCooling = cooling;
                icon.EnableInClassList("cooling", cooling == 1);
            }

            // A loading battle item only pops in small: the full pop and sunburst come when it can be used (Appear).
            void OnStateChanged(int previous, int state, bool loading)
            {
                shownState = state;
                if (previous == Rolling)
                {
                    frame.style.rotate = StyleKeyword.Null;
                    frame.style.scale = StyleKeyword.Null;
                }
                if (state != Held || previous < 0) return;
                if (loading) UiAnim.Pop(frame, RevealPopScale, RevealPopMs);
                else Appear();
            }

            void Appear()
            {
                UiAnim.Pop(frame, FramePopScale, FramePopMs);
                burst.experimental.animation.Start(0f, 1f, BurstMs, BurstFrame).Ease(Easing.OutCubic);
            }

            void ShowRoulette()
            {
                var all = PowerupRoller.Pool;
                var t = Time.time;
                var pick = all.Count > 0 ? all[(int)(t * RouletteIconsPerSecond) % all.Count] : null;
                SetIcon(true, pick != null && pick.Icon != null ? pick.Icon : hud.unknownItemIcon);
                SetLabel(null, 0);
                frame.style.rotate = new Rotate(Angle.Degrees(Mathf.Sin(t * WobbleSpeed) * WobbleDegrees));
                var s = 1f + PulseAmount * Mathf.Sin(t * PulseSpeed);
                frame.style.scale = new Scale(new Vector3(s, s, 1f));
            }

            void SetIcon(bool show, Texture2D texture)
            {
                if (iconValid && show == iconShown && texture == shownIcon) return;
                iconValid = true;
                iconShown = show;
                shownIcon = texture;
                icon.style.backgroundImage = show ? new StyleBackground(texture) : StyleKeyword.None;
            }

            void SetLabel(PowerupDefinition item, int count)
            {
                if (item == shownItem && (item == null || count == shownCharges)) return;
                var sameItem = item != null && item == shownItem;
                shownItem = item;
                shownCharges = count;
                itemName.SetVisible(item != null);
                itemName.text = item != null ? item.DisplayName : "";
                var multi = item != null && count > 1;
                charges.SetVisible(multi);
                if (multi) chargesText.SetPrefixedNumber('x', count);
                if (sameItem && multi) UiAnim.Pop(charges, ChargesPopScale, ChargesPopMs);
            }
        }
    }
}
