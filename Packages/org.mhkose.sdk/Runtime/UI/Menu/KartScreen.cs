using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Kart choice (Kart.uxml): a card per kart and the info panel with the three stat bars below the showcase. The
    /// hovered or focused kart carries the selected driver on the turntable, picking one goes on to the track or cup.
    /// For the online lobby (<see cref="LobbyPick.Loadout"/>) it lists the lobby's karts and picking one sets the
    /// player's loadout and returns to the lobby.
    /// </summary>
    public class KartScreen : CardGridScreen<KartDefinition>
    {
        VisualElement speed, acceleration, handling;

        protected override IEnumerable<KartDefinition> Items =>
            Selection.lobbyPick == LobbyPick.Loadout ? LobbyScreen.LobbyContent<KartDefinition>() : Content.All<KartDefinition>();

        protected override void BindPage()
        {
            speed = Q<VisualElement>("stat-speed");
            acceleration = Q<VisualElement>("stat-acceleration");
            handling = Q<VisualElement>("stat-handling");
        }

        protected override string GameName(string game) =>
            Selection.lobbyPick == LobbyPick.Loadout ? OnlineUi.PackName(game) : UiText.GameName(game);

        protected override string CardTag(KartDefinition k) => GameName(k.SourceGame);

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
            if (Selection.lobbyPick == LobbyPick.Loadout) LobbyScreen.ChooseLoadout(Flow, Selection.character, k);
            else Flow.ShowTrackChoice();
        }

        static void SetFill(VisualElement bar, float value) => bar.style.width = Length.Percent(Mathf.Clamp01(value) * 100f);
    }
}
