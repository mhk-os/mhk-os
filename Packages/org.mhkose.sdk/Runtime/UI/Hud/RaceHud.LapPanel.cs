using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>Top-right panel: lap (or alive count and health in battles), race clock and best lap.</summary>
        sealed class LapPanel
        {
            const float LapPopScale = 1.6f, BestPopScale = 1.25f, HealthPopScale = 1.12f;
            const int LapPopMs = 380, BestPopMs = 320, HealthPopMs = 280;
            const float LowHealth = 0.3f;
            const int MaxClockMinutes = 99;

            readonly RaceHud hud;
            VisualElement lapIcon, bestRow, healthBar, healthFill;
            Label lapCaption, lapValue, timeMain, timeFrac, bestText, healthText;
            string lapText;
            int shownCentis, shownSecond, shownLapA, shownLapB, shownHealth, shownHealthMax;
            bool shownBattle, shownOut;
            float shownBest, poppedBest;

            public LapPanel(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                lapIcon = hud.Q<VisualElement>("lap-icon");
                lapCaption = hud.Q<Label>("lap-caption");
                lapText = lapCaption.text;
                lapValue = hud.Q<Label>("lap-value");
                timeMain = hud.Q<Label>("time-main");
                timeFrac = hud.Q<Label>("time-frac");
                bestRow = hud.Q<VisualElement>("best-row");
                bestText = hud.Q<Label>("best-time");
                healthBar = hud.Q<VisualElement>("health");
                healthFill = hud.Q<VisualElement>("health-fill");
                healthText = hud.Q<Label>("health-text");
                bestRow.SetVisible(false);
                healthBar.SetVisible(false);
                shownCentis = shownSecond = shownHealth = int.MinValue;
                shownLapA = shownLapB = shownHealthMax = -1;
                shownBattle = shownOut = false;
                shownBest = float.NaN;
                poppedBest = -1f;
            }

            /// <summary>Race clock as "mm:ss" plus a smaller ".cc". The main part only changes once a second.</summary>
            public void RefreshClock(float clock)
            {
                var centis = UiText.Centis(clock);
                if (centis == shownCentis) return;
                shownCentis = centis;
                if (centis < 0)
                {
                    shownSecond = -1;
                    timeMain.text = "--:--";
                    timeFrac.text = ".--";
                    return;
                }
                Span<char> text = stackalloc char[16];
                var second = centis / 100;
                if (second != shownSecond)
                {
                    shownSecond = second;
                    var n = UiText.WriteMinutesSeconds(text, Mathf.Min(second / 60, MaxClockMinutes), second % 60);
                    timeMain.SetText(text.Slice(0, n));
                }
                text[0] = '.';
                UiText.WriteTwoDigits(text.Slice(1), centis % 100);
                timeFrac.SetText(text.Slice(0, 3));
            }

            public void RefreshLaps(int currentLap, int laps, float bestLap)
            {
                var lap = Mathf.Clamp(currentLap, 1, laps);
                if (shownBattle || lap != shownLapA || laps != shownLapB)
                {
                    var pop = !shownBattle && shownLapA >= 0 && lap != shownLapA;
                    if (shownBattle || shownLapA < 0) SetBattleMode(false);
                    shownBattle = false;
                    shownLapA = lap;
                    shownLapB = laps;
                    lapValue.SetRatio(lap, laps);
                    shownBest = float.NaN;
                    if (pop) UiAnim.Pop(lapValue, LapPopScale, LapPopMs);
                }
                if (bestLap.Equals(shownBest)) return;
                shownBest = bestLap;
                var has = bestLap > 0;
                bestRow.SetVisible(has);
                bestText.text = has ? UiText.Clock(bestLap) : "";
                if (has && !bestLap.Equals(poppedBest)) UiAnim.Pop(bestRow, BestPopScale, BestPopMs);
                poppedBest = bestLap;
            }

            public void RefreshBattle(int alive, int kartCount, int health, int maxHealth, bool eliminated)
            {
                if (!shownBattle || alive != shownLapA || kartCount != shownLapB)
                {
                    var pop = shownBattle && alive != shownLapA;
                    if (!shownBattle) SetBattleMode(true);
                    shownBattle = true;
                    shownLapA = alive;
                    shownLapB = kartCount;
                    lapValue.SetRatio(alive, kartCount);
                    if (pop) UiAnim.Pop(lapValue, LapPopScale, LapPopMs);
                }
                if (health == shownHealth && maxHealth == shownHealthMax && eliminated == shownOut) return;
                var hit = shownHealth != int.MinValue && health < shownHealth;
                shownHealth = health;
                shownHealthMax = maxHealth;
                shownOut = eliminated;
                var frac = Mathf.Clamp01(health / (float)Mathf.Max(1, maxHealth));
                healthFill.style.width = Length.Percent(frac * 100f);
                healthFill.EnableInClassList("low", frac <= LowHealth);
                healthBar.EnableInClassList("out", eliminated);
                if (eliminated) healthText.text = hud.outStatus;
                else healthText.SetText(health);
                if (hit) UiAnim.Pop(healthBar, HealthPopScale, HealthPopMs);
            }

            void SetBattleMode(bool battle)
            {
                lapIcon.EnableInClassList("i-flag", !battle);
                lapIcon.EnableInClassList("i-swords", battle);
                lapCaption.text = battle ? hud.aliveCaption : lapText;
                healthBar.SetVisible(battle);
                if (battle) bestRow.SetVisible(false);
            }
        }
    }
}
