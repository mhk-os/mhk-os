using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>Race position (top left): number, ordinal suffix, kart count, the podium colour and the danger colour.</summary>
        sealed class PositionView
        {
            const float PopScale = 1.45f;
            const int PopMs = 360;
            static readonly string[] PodiumClasses = { "p1", "p2", "p3" };

            readonly RaceHud hud;
            VisualElement row;
            Label number, suffix, total;
            int shownPosition, shownKartCount;
            bool shownDanger;

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
                row.RemoveFromClassList("danger");
                shownDanger = false;
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

            /// <summary>Shows the position in the danger colour (Last Man Standing: the player is the one going out).</summary>
            public void SetDanger(bool danger)
            {
                if (danger == shownDanger) return;
                shownDanger = danger;
                row.EnableInClassList("danger", danger);
            }
        }
    }
}
