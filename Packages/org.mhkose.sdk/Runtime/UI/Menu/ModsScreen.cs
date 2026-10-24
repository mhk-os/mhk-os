using System;
using System.Collections.Generic;
using MHK.Assets;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Mods and asset packs (Mods.uxml), one section each: a row per mod or asset pack in load order (the top one wins)
    /// with an Enabled / Disabled toggle and move up and down. Apply reloads content and asset packs and restarts the
    /// menu, Reload does the same and comes back here (finds new mods and packs), Open Folder shows the section's folder.
    /// </summary>
    public partial class ModsScreen : MenuScreen
    {
        enum Section { Mods, AssetPacks }

        const string FallbackLogoClass = "mod-logo-fallback";

        [SerializeField, Tooltip("Section chip (Templates/Chip.uxml).")]
        VisualTreeAsset chipTemplate;
        [SerializeField, Tooltip("Mod and asset pack row (Templates/ModRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.06f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.12f;
        [SerializeField, Tooltip("Title line of a pack built with a commit. {0} = title, {1} = commit. A pack without one shows only its title.")]
        string titleFormat = "{0} | {1}";
        [SerializeField, Tooltip("Toggle text of an enabled mod or asset pack.")]
        string enabledText = "Enabled";
        [SerializeField, Tooltip("Toggle text of a disabled mod or asset pack.")]
        string disabledText = "Disabled";
        [SerializeField, Tooltip("Toggle tooltip of a loaded pack whose drivers have no loaded kart. {0} = number of drivers.")]
        string missingKartsTip = "{0} drivers have no kart. Enable the pack with their karts.";
        [SerializeField, Tooltip("Tooltip of a loaded mod whose definitions are replaced by mods above it. {0} = number of definitions.")]
        string replacedTip = "{0} of its definitions are replaced by mods above it.";
        [SerializeField, Tooltip("Chip texts of the two sections.")]
        string modsTab = "Mods", packsTab = "Asset Packs";
        [SerializeField, Tooltip("Empty line of the Mods section.")]
        string modsEmpty = "No mods found.";
        [SerializeField, Tooltip("Empty line of the Asset Packs section.")]
        string packsEmpty = "No asset packs found.";
        [SerializeField, Tooltip("Folder button text per section.")]
        string modsFolderText = "Open Mod Folder", packsFolderText = "Open Asset Pack Folder";
        [SerializeField, Tooltip("Reload button tooltip in the Mods section.")]
        string modsReloadTip = "Looks for mods added to the mod folder. Also applies the changes above.";
        [SerializeField, Tooltip("Reload button tooltip in the Asset Packs section.")]
        string packsReloadTip = "Looks for asset packs added to the asset pack folder. Also applies the changes above.";
        [SerializeField, Tooltip("Move up and down tooltips in the Mods section.")]
        string modUpTip = "Move up. A mod higher in the list replaces content with the same id of the mods below.", modDownTip = "Move down";
        [SerializeField, Tooltip("Move up and down tooltips in the Asset Packs section.")]
        string packUpTip = "Move up. A pack higher in the list replaces the same files of the packs below.", packDownTip = "Move down";

        [AutoStaticsCleanup] static Section section;

        readonly List<ContentPack> mods = new();
        readonly List<AssetPacks.Info> packs = new();
        VisualElement list;
        VisualElement sections;

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            list = Q<VisualElement>("mod-list");
            sections = Q<VisualElement>("sections");
            var scroll = Q<ScrollView>("mod-scroll");
            list.RegisterCallback<FocusInEvent>(e =>
            {
                if (e.target is VisualElement v) scroll.ScrollTo(v);
            });
            list.RegisterCallback<NavigationMoveEvent>(OnListNavigate);
            sections.RegisterCallback<NavigationMoveEvent>(OnSectionsNavigate);
            ChipGroup.Fill(sections, chipTemplate, new[] { modsTab, packsTab }, (int)section, index =>
            {
                Click();
                section = (Section)index;
                Fill(true);
            });
            Fill(true);
            OnClick("apply", Apply);
            OnClick("open-mods", () => GhostUi.Reveal(section == Section.Mods ? ModLoader.UserFolder : AssetPacks.UserFolder));
            OnClick("reload", Reload);
        }

        protected override void Unbind()
        {
            list = null;
            sections = null;
        }

        // Up and down keep the column (toggle, move up, move down) from row to row: a disabled move button falls back
        // to the row's toggle, up from the first row goes to the section chips. Below the last row MenuScreen takes over.
        void OnListNavigate(NavigationMoveEvent e)
        {
            var up = e.direction == NavigationMoveEvent.Direction.Up;
            if ((!up && e.direction != NavigationMoveEvent.Direction.Down) || e.target is not Button from) return;
            VisualElement row = from;
            while (row.parent != null && row.parent != list) row = row.parent;
            var next = list.IndexOf(row) + (up ? -1 : 1);
            if (next >= list.childCount) return;
            VisualElement to = next < 0 ? SelectedChip() : list[next].Q<Button>(from.name);
            if (to != null && !to.enabledSelf) to = list[next].Q<Button>("mod-toggle");
            Navigate(e, from, to);
        }

        void OnSectionsNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Down || e.target is not VisualElement from) return;
            Navigate(e, from, list.childCount > 0 ? list[0].Q<Button>("mod-toggle") : null);
        }

        VisualElement SelectedChip() => sections.Q(className: "selected") ?? (sections.childCount > 0 ? sections[0] : null);

        static void Navigate(NavigationMoveEvent e, VisualElement from, VisualElement to)
        {
            if (to == null) return;
            to.Focus();
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        void Fill(bool animate)
        {
            list.Clear();
            var modsShown = section == Section.Mods;
            if (modsShown) FillMods();
            else FillPacks();
            var empty = Q<Label>("mod-empty");
            empty.text = modsShown ? modsEmpty : packsEmpty;
            empty.SetVisible(list.childCount == 0);
            Root.SetText("open-mods-label", modsShown ? modsFolderText : packsFolderText);
            Q<Button>("reload").tooltip = modsShown ? modsReloadTip : packsReloadTip;
            if (animate) UiAnim.Stagger(list, rowStep, rowFirst, "from-left");
        }

        void FillMods()
        {
            mods.Clear();
            mods.AddRange(ModOrder.Sort(ModLoader.AvailablePacks, Settings.modOrder));
            for (var i = 0; i < mods.Count; i++)
            {
                var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
                if (row == null) break;
                FillPackRow(row, mods[i]);
                BindMove(row, i, mods.Count);
            }
        }

        void FillPacks()
        {
            PendingPackOrder(packs);
            for (var i = 0; i < packs.Count; i++)
            {
                var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
                if (row == null) break;
                var p = packs[i];
                SetLogo(row.Q("mod-logo"), p.icon, "palette");
                row.SetText("mod-title", string.IsNullOrEmpty(p.title) ? p.id : p.title);
                SetDescription(row, p.description);
                var toggle = row.Q<Button>("mod-toggle");
                if (toggle != null) BindToggle(toggle, p.id, Settings.disabledAssetPacks);
                BindMove(row, i, packs.Count);
            }
        }

        // AssetPacks.Available is the order of the last load. Moves since then are in the settings, which list every pack after a move.
        void PendingPackOrder(List<AssetPacks.Info> result)
        {
            result.Clear();
            result.AddRange(AssetPacks.Available);
            var saved = Settings.assetPackOrder;
            foreach (var p in result)
                if (IndexOf(saved, p.id) < 0) return;
            result.Sort((a, b) => IndexOf(saved, a.id).CompareTo(IndexOf(saved, b.id)));
        }

        static int IndexOf(List<string> ids, string id)
        {
            for (var i = 0; i < ids.Count; i++)
                if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void FillPackRow(VisualElement row, ContentPack pack)
        {
            SetLogo(row.Q("mod-logo"), pack.logo, "extension");
            row.SetText("mod-title", string.IsNullOrEmpty(pack.version) ? pack.title : string.Format(titleFormat, pack.title, pack.version));
            SetDescription(row, pack.description);
            var replaced = ReplacedCount(pack);
            if (replaced > 0) row.tooltip = string.Format(replacedTip, replaced);
            var toggle = row.Q<Button>("mod-toggle");
            if (toggle == null) return;
            BindToggle(toggle, pack.packId, Settings.disabledPacks);
            var withoutKart = DriversWithoutKart(pack);
            if (withoutKart > 0) toggle.tooltip = string.Format(missingKartsTip, withoutKart);
        }

        static void SetDescription(VisualElement row, string description)
        {
            var text = row.Q<Label>("mod-text");
            if (text == null) return;
            text.text = description;
            text.SetVisible(!string.IsNullOrEmpty(description));
        }

        static void SetLogo(VisualElement logo, Texture2D image, string fallbackIcon)
        {
            if (logo == null) return;
            var fallback = image == null;
            if (fallback) logo.SetIcon(fallbackIcon);
            else logo.SetImage(image);
            logo.EnableInClassList(FallbackLogoClass, fallback);
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

        static int ReplacedCount(ContentPack pack)
        {
            if (Content.RootOf(pack) == null) return 0;
            var count = 0;
            foreach (var d in pack.GetAllIncluded<ContentDefinition>())
                if (d != null && Content.Get<ContentDefinition>(d.Id) != d) count++;
            return count;
        }

        void BindToggle(Button toggle, string id, List<string> disabled)
        {
            void Show()
            {
                var enabled = !disabled.Contains(id);
                toggle.text = enabled ? enabledText : disabledText;
                toggle.EnableInClassList("on", enabled);
                toggle.EnableInClassList("off", !enabled);
            }
            toggle.clicked += () =>
            {
                if (!disabled.Remove(id)) disabled.Add(id);
                Show();
            };
            Show();
        }

        void BindMove(VisualElement row, int index, int count)
        {
            var modsShown = section == Section.Mods;
            var up = row.Q<Button>("mod-up");
            var down = row.Q<Button>("mod-down");
            if (up == null || down == null) return;
            up.tooltip = modsShown ? modUpTip : packUpTip;
            down.tooltip = modsShown ? modDownTip : packDownTip;
            up.SetEnabled(index > 0);
            down.SetEnabled(index < count - 1);
            up.clicked += () => Move(index, index - 1, "mod-up", "mod-down");
            down.clicked += () => Move(index, index + 1, "mod-down", "mod-up");
        }

        void Move(int from, int to, string focusName, string otherName)
        {
            Click();
            if (section == Section.Mods)
            {
                (mods[from], mods[to]) = (mods[to], mods[from]);
                Settings.modOrder = ModOrder.ToSave(mods.ConvertAll(p => p.packId), Settings.modOrder);
            }
            else
            {
                (packs[from], packs[to]) = (packs[to], packs[from]);
                Settings.assetPackOrder = ModOrder.ToSave(packs.ConvertAll(p => p.id), Settings.assetPackOrder);
            }
            Fill(false);
            if (to < 0 || to >= list.childCount) return;
            var target = list[to].Q<Button>(focusName);
            FocusLater(target != null && target.enabledSelf ? target : list[to].Q<Button>(otherName));
        }

        void Apply()
        {
            GameRoot.Instance.SaveSettings();
            SceneFlow.ReloadContent();
        }

        void Reload()
        {
            MenuFlow.OpenOnStart = typeof(ModsScreen);
            Apply();
        }
    }
}
