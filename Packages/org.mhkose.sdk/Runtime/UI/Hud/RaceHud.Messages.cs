using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>Centre messages: the flash plate, the "Final lap!" ribbon and the pulsing "Wrong way!" plate.</summary>
        sealed class Messages
        {
            const float BannerHoldSeconds = 1.9f, BannerOutSeconds = 0.6f;
            const float FlashPopScale = 1.6f;
            const int FlashPopMs = 380;
            const float WarnPulseAmount = 0.07f, WarnPulsePeriod = 0.55f;

            readonly RaceHud hud;
            VisualElement banner, warnAnchor, flashAnchor, flashPlate;
            Label flashLabel;
            IVisualElementScheduledItem warnPulse;
            int bannerStage;
            float bannerTimer, flashTimer;
            bool shownWrongWay;

            public Messages(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                banner = hud.Q<VisualElement>("banner");
                banner.RemoveFromClassList("in");
                banner.RemoveFromClassList("out");
                warnAnchor = hud.Q<VisualElement>("warn");
                warnAnchor.RemoveFromClassList("shown");
                warnPulse = UiAnim.Pulse(hud.Q<VisualElement>("warn-plate"), WarnPulseAmount, WarnPulsePeriod);
                warnPulse.Pause();
                flashAnchor = hud.Q<VisualElement>("flash");
                flashPlate = hud.Q<VisualElement>("flash-plate");
                flashLabel = hud.Q<Label>("flash-label");
                flashAnchor.RemoveFromClassList("shown");
                bannerStage = 0;
                flashTimer = 0f;
                shownWrongWay = false;
            }

            public void Unbind()
            {
                StopPulse();
                warnPulse = null;
            }

            public void StopPulse() => warnPulse?.Pause();

            /// <summary>Pops a message plate in below the centre. <paramref name="variant"/>: null, "good" or "bad".</summary>
            public void Flash(string text, float seconds, string variant)
            {
                flashLabel.text = text;
                flashPlate.EnableInClassList("good", variant == "good");
                flashPlate.EnableInClassList("bad", variant == "bad");
                flashAnchor.AddToClassList("shown");
                UiAnim.Pop(flashPlate, FlashPopScale, FlashPopMs);
                flashTimer = seconds;
            }

            public void ShowBanner()
            {
                banner.RemoveFromClassList("out");
                banner.AddToClassList("in");
                bannerStage = 1;
                bannerTimer = BannerHoldSeconds;
            }

            public void SetWrongWay(bool wrongWay)
            {
                if (wrongWay == shownWrongWay) return;
                shownWrongWay = wrongWay;
                warnAnchor.EnableInClassList("shown", wrongWay);
                if (wrongWay) warnPulse.Resume();
                else warnPulse.Pause();
            }

            public void Tick(float dt)
            {
                if (flashTimer > 0f)
                {
                    flashTimer -= dt;
                    if (flashTimer <= 0f) flashAnchor.RemoveFromClassList("shown");
                }
                if (bannerStage == 0) return;
                bannerTimer -= dt;
                if (bannerTimer > 0f) return;
                banner.RemoveFromClassList(bannerStage == 1 ? "in" : "out");
                if (bannerStage == 1)
                {
                    // Slide out to the other side, then jump back (invisible) to the start position.
                    banner.AddToClassList("out");
                    bannerStage = 2;
                    bannerTimer = BannerOutSeconds;
                }
                else bannerStage = 0;
            }
        }
    }
}
