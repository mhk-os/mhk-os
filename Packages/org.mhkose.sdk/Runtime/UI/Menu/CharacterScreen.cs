using System.Collections.Generic;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Driver choice (Character.uxml): a card per character and the info panel below the showcase. The hovered or
    /// focused card is described and put on the turntable, picking one goes on to the kart.
    /// </summary>
    public class CharacterScreen : CardGridScreen<CharacterDefinition>
    {
        [SerializeField, Tooltip("Special fact value of characters without a special powerup.")]
        string noSpecialName = "Random item";

        VisualElement kartLine, specialThumb, specialSymbol, kartThumb;
        Label specialText, kartText;

        protected override IEnumerable<CharacterDefinition> Items => Content.All<CharacterDefinition>();

        protected override void BindPage()
        {
            specialText = Q<Label>("info-special-text");
            specialThumb = Q<VisualElement>("info-special-thumb");
            specialSymbol = Q<VisualElement>("info-special-symbol");
            kartLine = Q<VisualElement>("info-kart");
            kartText = Q<Label>("info-kart-text");
            kartThumb = Q<VisualElement>("info-kart-thumb");
        }

        protected override string CardTag(CharacterDefinition c) => "";

        protected override Texture2D CardImage(CharacterDefinition c) => c.portrait != null ? c.portrait : c.Icon;

        protected override bool IsSelected(CharacterDefinition c) => c == Selection.character;

        protected override void OnShown(CharacterDefinition c) => Flow.ShowOnTurntable(c, MenuSelection.LoadedDefaultKart(c));

        protected override void Describe(CharacterDefinition c)
        {
            var special = c.specialPowerup;
            specialText.text = special != null ? special.DisplayName : noSpecialName;
            var specialIcon = special != null ? special.Icon : null;
            specialThumb.SetImage(specialIcon);
            specialSymbol.SetVisible(specialIcon == null);
            var kart = MenuSelection.LoadedDefaultKart(c);
            kartLine.SetVisible(kart != null);
            if (kart != null)
            {
                kartText.text = kart.DisplayName;
                kartThumb.SetImage(kart.Icon);
            }
        }

        protected override void Pick(CharacterDefinition c)
        {
            Selection.character = c;
            Settings.lastCharacterId = c.Id;
            Selection.kart = MenuSelection.LoadedDefaultKart(c);
            Flow.Show<KartScreen>();
        }
    }
}
