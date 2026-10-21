using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Kart choice (Kart.uxml): a card per kart and the info panel with the three stat bars below the showcase. The
    /// hovered or focused kart carries the selected driver on the turntable, picking one goes on to the track or cup.
    /// </summary>
    public class KartScreen : CardGridScreen<KartDefinition>
    {
        VisualElement speed, acceleration, handling;

        protected override IEnumerable<KartDefinition> Items => Content.All<KartDefinition>();

        protected override void BindPage()
        {
            speed = Q<VisualElement>("stat-speed");
            acceleration = Q<VisualElement>("stat-acceleration");
            handling = Q<VisualElement>("stat-handling");
        }

        protected override string CardTag(KartDefinition k) => UiText.GameName(k.SourceGame);

        protected override Texture2D CardImage(KartDefinition k) => k.Icon;

        protected override bool IsSelected(KartDefinition k) => k == Selection.kart;

        protected override void OnShown(KartDefinition k) => Flow.ShowOnTurntable(Selection.character, k);

        protected override void Describe(KartDefinition k)
        {
            SetFill(speed, k.speedRating);
            SetFill(acceleration, k.accelerationRating);
            SetFill(handling, k.handlingRating);
        }

        protected override void Pick(KartDefinition k)
        {
            Selection.kart = k;
            Flow.ShowTrackChoice();
        }

        static void SetFill(VisualElement bar, float value) => bar.style.width = Length.Percent(Mathf.Clamp01(value) * 100f);
    }
}
