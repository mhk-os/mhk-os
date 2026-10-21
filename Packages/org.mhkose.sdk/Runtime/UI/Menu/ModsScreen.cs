using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Content packs (Mods.uxml): one row per top-level pack found by the <see cref="ModLoader"/>, disabled ones
    /// included, each with an Enabled / Disabled toggle. Apply reloads mods and content and restarts the menu.
    /// </summary>
    public class ModsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Pack row (Templates/ModRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.06f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.12f;
        [SerializeField, Tooltip("Pack title line. {0} = title, {1} = version.")]
        string titleFormat = "{0}  v{1}";
        [SerializeField, Tooltip("Toggle text of an enabled pack.")]
        string enabledText = "Enabled";
        [SerializeField, Tooltip("Toggle text of a disabled pack.")]
        string disabledText = "Disabled";
        [SerializeField, Tooltip("Toggle tooltip of a loaded pack whose drivers have no loaded kart. {0} = number of drivers.")]
        string missingKartsTip = "{0} drivers have no kart. Enable the pack with their karts.";

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            var list = Q<VisualElement>("mod-list");
            list.Clear();
            var packs = ModLoader.AvailablePacks;
            foreach (var pack in packs)
            {
                var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
                if (row == null) break;
                FillPackRow(row, pack);
            }
            SetVisible("mod-empty", packs.Count == 0);
            UiAnim.Stagger(list, rowStep, rowFirst, "from-left");
            OnClick("apply", Apply);
        }

        void FillPackRow(VisualElement row, ContentPack pack)
        {
            row.Q("mod-logo")?.SetImage(pack.logo);
            row.SetText("mod-title", string.Format(titleFormat, pack.title, pack.version));
            var text = row.Q<Label>("mod-text");
            if (text != null)
            {
                text.text = pack.description;
                text.SetVisible(!string.IsNullOrEmpty(pack.description));
            }
            var toggle = row.Q<Button>("mod-toggle");
            if (toggle == null) return;
            BindPackToggle(toggle, pack.packId);
            var withoutKart = DriversWithoutKart(pack);
            if (withoutKart > 0) toggle.tooltip = string.Format(missingKartsTip, withoutKart);
        }

        // Only a loaded pack can tell: a disabled one is a metadata stand-in without content.
        static int DriversWithoutKart(ContentPack pack)
        {
            if (Content.RootOf(pack) == null) return 0;
            var count = 0;
            foreach (var c in pack.GetAllIncluded<CharacterDefinition>())
                if (!c.Hidden && (c.defaultKart == null || Content.Get<KartDefinition>(c.defaultKart.Id) == null)) count++;
            return count;
        }

        void BindPackToggle(Button toggle, string packId)
        {
            void Show()
            {
                var enabled = !Settings.disabledPacks.Contains(packId);
                toggle.text = enabled ? enabledText : disabledText;
                toggle.EnableInClassList("on", enabled);
                toggle.EnableInClassList("off", !enabled);
            }
            toggle.clicked += () =>
            {
                if (!Settings.disabledPacks.Remove(packId)) Settings.disabledPacks.Add(packId);
                Show();
            };
            Show();
        }

        void Apply()
        {
            GameRoot.Instance.SaveSettings();
            SceneFlow.ReloadContent();
        }
    }
}
