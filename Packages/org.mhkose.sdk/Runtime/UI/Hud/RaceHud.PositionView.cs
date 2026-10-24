using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>Race position (top left): number, ordinal suffix, kart count and the podium colour.</summary>
        sealed class PositionView
        {
            const float PopScale = 1.45f;
            const int PopMs = 360;
            static readonly string[] PodiumClasses = { "p1", "p2", "p3" };

            readonly RaceHud hud;
            VisualElement row;
            Label number, suffix, total;
            int shownPosition, shownKartCount;

            public PositionView(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                row = hud.Q<VisualElement>("position");
                number = hud.Q<Label>("position-number");
                suffix = hud.Q<Label>("position-suffix");
                total = hud.Q<Label>("position-total");
                number.text = "-";
                suffix.text = "";
                total.text = "";
                row.SetExclusiveClass(-1, PodiumClasses);
                shownPosition = int.MinValue;
                shownKartCount = -1;
            }

            public void Refresh(int position, int kartCount)
            {
                if (position != shownPosition)
                {
                    var pop = shownPosition != int.MinValue;
                    shownPosition = position;
                    number.SetText(position);
                    suffix.text = UiText.OrdinalSuffix(position);
                    row.SetExclusiveClass(position - 1, PodiumClasses);
                    if (pop) UiAnim.Pop(number, PopScale, PopMs);
                }
                if (kartCount == shownKartCount) return;
                shownKartCount = kartCount;
                total.SetPrefixedNumber('/', kartCount);
            }
        }
    }
}
