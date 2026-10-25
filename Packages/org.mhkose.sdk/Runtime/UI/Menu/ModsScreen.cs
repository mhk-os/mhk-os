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
    /// Mods and asset packs (Mods.uxml), one section each: a row per installed mod or asset pack in load order (the top
    /// one wins) with an Enabled / Disabled toggle and a drag handle to reorder (<see cref="DragReorder"/>), then a row per
    /// source (GitHub repository) not installed yet. Items from a source download and update here. Apply reloads content and
    /// asset packs and restarts the menu, Reload does the same and comes back here (finds new mods and packs), Open Folder
    /// shows the section's folder.
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
        [SerializeField, Tooltip("Line of a mod built for an older game version (another mod API level), which is not loaded.")]
        string needsModUpdateText = "Made for an older game version. Ask its author for an update.";
        [SerializeField, Tooltip("Line of a mod built for a newer game version, which is not loaded.")]
        string needsNewerGameText = "Made for a newer game version. Update the game.";
        [SerializeField, Tooltip("Line of a mod that failed to load. {0} = one-line reason.")]
        string loadFailedText = "Could not be loaded. {0}";
        [SerializeField, Tooltip("Tooltip of Report on a mod that is not loaded. {0} = source repository (owner/name).")]
        string reportTip = "Opens the issues page of {0} in the browser to ask its author for an update.";
        [SerializeField, Tooltip("Page Report opens. {0} = source repository (owner/name).")]
        string issuesUrlFormat = "https://github.com/{0}/issues";
        [SerializeField, Tooltip("Chip texts of the sections.")]
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
        [SerializeField, Tooltip("Drag handle tooltip in the Mods section.")]
        string modMoveTip = "Load order. A mod higher in the list replaces content with the same id of the mods below.";
        [SerializeField, Tooltip("Drag handle tooltip in the Asset Packs section.")]
        string packMoveTip = "Load order. A pack higher in the list replaces the same files of the packs below.";

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
            DragReorder.Attach(list, "mod-drag", OnRowMoved, scroll);
            sections.RegisterCallback<NavigationMoveEvent>(OnSectionsNavigate);
            ChipGroup.Fill(sections, chipTemplate, new[] { modsTab, packsTab }, (int)section, index =>
            {
                Click();
                section = (Section)index;
                Fill(true);
            });
            BindSources();
            Fill(true);
            FocusTarget = list.childCount > 0 ? FirstButton(list[0]) : sourceInput;
            OnClick("apply", Apply);
            OnClick("open-mods", () => GhostUi.Reveal(section == Section.AssetPacks ? AssetPacks.UserFolder : ModLoader.UserFolder));
            OnClick("reload", Reload);
        }

        protected override void Unbind()
        {
            base.Unbind();
            UnbindSources();
            list = null;
            sections = null;
        }

        // Up and down keep the column (drag handle, toggle, GitHub, ...) from row to row: a hidden or disabled button falls
        // back to the row's first one, up from the first row goes to the source tools. Below the last row MenuScreen takes over.
        void OnListNavigate(NavigationMoveEvent e)
        {
            var up = e.direction == NavigationMoveEvent.Direction.Up;
            if ((!up && e.direction != NavigationMoveEvent.Direction.Down) || e.target is not Button from) return;
            VisualElement row = from;
            while (row.parent != null && row.parent != list) row = row.parent;
            var next = list.IndexOf(row) + (up ? -1 : 1);
            if (next >= list.childCount) return;
            Navigate(e, from, next < 0 ? FirstTool() : RowButton(list[next], from.name));
        }

        void OnSectionsNavigate(NavigationMoveEvent e)
        {
            if (e.direction != NavigationMoveEvent.Direction.Down || e.target is not VisualElement from) return;
            Navigate(e, from, sourceInput);
        }

        VisualElement FirstRowButton() => list.childCount > 0 ? RowButton(list[0], null) : null;

        static Button FirstButton(VisualElement row)
        {
            var toggle = row.Q<Button>("mod-toggle");
            if (toggle == null || toggle.enabledSelf) return toggle ?? row.Q<Button>("src-action");
            if (row.ClassListContains("mod-reportable")) return row.Q<Button>("mod-report");
            return RowButton(row, null);
        }

        // The row's button named like the one the focus comes from, else its first shown and enabled button.
        static Button RowButton(VisualElement row, string name)
        {
            var same = name != null ? row.Q<Button>(name) : null;
            if (same != null && Navigable(same)) return same;
            return row.Query<Button>().Where(Navigable).First();
        }

        static bool Navigable(Button b) =>
            b.enabledInHierarchy && b.resolvedStyle.display != DisplayStyle.None && b.resolvedStyle.visibility == Visibility.Visible;

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
            sourceViews.Clear();
            var packsShown = section == Section.AssetPacks;
            if (packsShown) FillPacks();
            else FillMods();
            FillSources();
            var empty = Q<Label>("mod-empty");
            empty.text = packsShown ? packsEmpty : modsEmpty;
            empty.SetVisible(list.childCount == 0);
            Root.SetText("open-mods-label", packsShown ? packsFolderText : modsFolderText);
            Q<Button>("reload").tooltip = packsShown ? packsReloadTip : modsReloadTip;
            ShowTools();
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
                BindMove(row, mods.Count);
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
                BindInstalledSource(row, p.id);
                var toggle = row.Q<Button>("mod-toggle");
                if (toggle != null) BindToggle(toggle, p.id, Settings.disabledAssetPacks);
                BindMove(row, packs.Count);
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
            BindInstalledSource(row, pack.packId);
            var replaced = ReplacedCount(pack);
            if (replaced > 0) row.tooltip = string.Format(replacedTip, replaced);
            var toggle = row.Q<Button>("mod-toggle");
            if (toggle == null) return;
            BindToggle(toggle, pack.packId, Settings.disabledPacks);
            var withoutKart = DriversWithoutKart(pack);
            if (withoutKart > 0) toggle.tooltip = string.Format(missingKartsTip, withoutKart);
            ShowProblem(row, toggle, pack);
        }

        // A mod that is not loaded: the reason and no toggle, Report when its author can fix it and its repository is known.
        void ShowProblem(VisualElement row, Button toggle, ContentPack pack)
        {
            var problem = ModLoader.ProblemOf(pack);
            row.Q("mod-problem")?.SetVisible(problem != ModProblem.None);
            if (problem == ModProblem.None) return;
            row.SetText("mod-problem-label", problem switch
            {
                ModProblem.NeedsModUpdate => needsModUpdateText,
                ModProblem.NeedsNewerGame => needsNewerGameText,
                _ => string.Format(loadFailedText, ModLoader.ProblemDetail(pack)),
            });
            toggle.SetEnabled(false);
            var repo = SourceOf(pack.packId)?.repo;
            var report = row.Q<Button>("mod-report");
            if (report == null || repo == null || problem == ModProblem.NeedsNewerGame) return;
            row.AddToClassList("mod-reportable");
            report.tooltip = string.Format(reportTip, repo);
            report.clicked += () =>
            {
                Click();
                Application.OpenURL(string.Format(issuesUrlFormat, repo));
            };
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

        void BindMove(VisualElement row, int count)
        {
            var handle = row.Q<Button>("mod-drag");
            if (handle == null) return;
            handle.tooltip = section == Section.Mods ? modMoveTip : packMoveTip;
            handle.SetEnabled(count > 1);
        }

        // The rows are already in their new order (installed rows come first, so row and item indices match).
        void OnRowMoved(int from, int to)
        {
            if (section == Section.Mods)
            {
                Reorder(mods, from, to);
                Settings.modOrder = ModOrder.ToSave(mods.ConvertAll(p => p.packId), Settings.modOrder);
            }
            else
            {
                Reorder(packs, from, to);
                Settings.assetPackOrder = ModOrder.ToSave(packs.ConvertAll(p => p.id), Settings.assetPackOrder);
            }
        }

        static void Reorder<T>(List<T> items, int from, int to)
        {
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
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
