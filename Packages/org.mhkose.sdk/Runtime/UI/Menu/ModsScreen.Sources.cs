using System;
using System.Collections.Generic;
using System.Text;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class ModsScreen
    {
        const string ConfirmClass = "src-confirm";
        const string NoActionClass = "src-no-action";
        const string UpdateClass = "src-update";
        const string SourceClass = "src-managed";

        [SerializeField, Tooltip("Row of a source not installed yet (Templates/SourceRow.uxml).")]
        VisualTreeAsset sourceRowTemplate;
        [SerializeField, Tooltip("State, commit and size texts of sources.")]
        ModSourceTexts sourceTexts = new();
        [SerializeField, Tooltip("Placeholder of the repository link field.")]
        string sourcePlaceholder = "github.com/owner/repository";
        [SerializeField, Tooltip("Error line when Paste finds no text on the clipboard.")]
        string clipboardEmpty = "The clipboard is empty.";
        [SerializeField, Tooltip("Action button texts of a source not installed: before the download and after an error.")]
        string downloadText = "Download", retryText = "Retry";
        [SerializeField, Tooltip("Tooltip of the remove buttons.")]
        string removeTip = "Remove";
        [SerializeField, Tooltip("Tooltip of Update All while nothing in the section has an update.")]
        string nothingToUpdateTip = "Everything in this section is up to date.";
        [SerializeField, Tooltip("Tooltip of Update All. {0} = number of items with an update.")]
        string updateAllTip = "Updates {0} items of this section.";

        readonly List<(string repo, VisualElement row, bool installedRow)> sourceViews = new();
        string shownSources;
        TextField sourceInput;
        VisualElement sourceError;
        Button autoUpdate, updateAll;

        /// <summary>Opens the Mods page at its Mods section (the download of the built-in mods and other mods).</summary>
        public static void OpenModsSection(MenuFlow flow)
        {
            section = Section.Mods;
            flow.Show<ModsScreen>();
        }

        void BindSources()
        {
            sourceInput = Q<TextField>("source-input");
            sourceError = Q<VisualElement>("source-error");
            sourceInput.textEdition.placeholder = sourcePlaceholder;
            sourceInput.RegisterValueChangedCallback(_ => ShowSourceError(null));
            sourceInput.RegisterCallback<KeyDownEvent>(OnSourceKey, TrickleDown.TrickleDown);
            Q<VisualElement>("source-bar").RegisterCallback<NavigationMoveEvent>(OnSourceBarNavigate);
            OnClick("source-paste", PasteSource);
            OnClick("source-add", AddSource);
            OnClick("check-updates", ModInstalls.Check);
            updateAll = OnClick("update-all", UpdateAll);
            autoUpdate = OnClick("auto-update", () =>
            {
                ModSources.AutoUpdate = !ModSources.AutoUpdate;
                GameRoot.Instance.SaveSettings();
                ShowAutoUpdate();
            });
            ShowAutoUpdate();
            ModSources.Changed += OnSourcesChanged;
            if (ModSources.Enabled && NeedsCheck()) ModInstalls.Check();
        }

        void UnbindSources()
        {
            ModSources.Changed -= OnSourcesChanged;
            sourceViews.Clear();
            shownSources = null;
            sourceInput = null;
            sourceError = null;
            autoUpdate = null;
            updateAll = null;
        }

        // Only when nothing was checked yet: the start of the game checks (throttled), the button always does.
        static bool NeedsCheck()
        {
            foreach (var s in ModSources.All)
                if (s.state != ModSources.State.Unknown) return false;
            return ModSources.All.Count > 0;
        }

        ModSources.Kind SectionKind => section == Section.AssetPacks ? ModSources.Kind.AssetPack : ModSources.Kind.Mod;

        // A source belongs to the shown section, or to both while its kind is unknown (before its first check).
        bool InSection(ModSources.Source s) => s.kind == SectionKind || s.kind == ModSources.Kind.Unknown;

        void ShowAutoUpdate()
        {
            var on = ModSources.AutoUpdate;
            autoUpdate.EnableInClassList("on", on);
            autoUpdate.EnableInClassList("off", !on);
            autoUpdate.Q("auto-update-icon")?.SetIcon(on ? "check" : "close");
        }

        void ShowTools()
        {
            var count = 0;
            foreach (var s in ModSources.All)
                if (Updatable(s)) count++;
            updateAll.SetEnabled(count > 0 && !ModInstalls.Running);
            updateAll.tooltip = count > 0 ? string.Format(updateAllTip, count) : nothingToUpdateTip;
        }

        bool Updatable(ModSources.Source s) => InSection(s) && s.installed && s.state == ModSources.State.UpdateAvailable;

        VisualElement FirstTool() => updateAll.enabledSelf ? updateAll : Q<Button>("check-updates");

        void OnSourceKey(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
            e.StopPropagation();
            Click();
            AddSource();
        }

        void OnSourceBarNavigate(NavigationMoveEvent e)
        {
            if (e.target is not VisualElement from) return;
            var inTools = from.parent != null && from.parent.ClassListContains("src-tools");
            if (e.direction == NavigationMoveEvent.Direction.Up) Navigate(e, from, inTools ? sourceInput : SelectedChip());
            else if (e.direction == NavigationMoveEvent.Direction.Down)
                Navigate(e, from, !inTools ? FirstTool() : list.childCount > 0 ? FirstRowButton() : null);
        }

        void PasteSource()
        {
            var text = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowSourceError(clipboardEmpty);
                return;
            }
            sourceInput.value = text.Trim();
            FocusLater(Q<Button>("source-add"));
        }

        void AddSource()
        {
            if (!ModSources.TryAdd(sourceInput.value, out var error))
            {
                ShowSourceError(error);
                return;
            }
            GameRoot.Instance.SaveSettings();
            sourceInput.SetValueWithoutNotify("");
            ShowSourceError(null);
            Fill(false);
            FocusLater(list.childCount > 0 ? RowButton(list[list.childCount - 1], "src-action") : null);
        }

        void ShowSourceError(string error)
        {
            if (sourceError == null) return;
            sourceError.style.visibility = string.IsNullOrEmpty(error) ? Visibility.Hidden : Visibility.Visible;
            sourceError.SetText("source-error-label", error);
        }

        void FillSources()
        {
            foreach (var source in ModSources.All)
            {
                if (source.installed || !InSection(source)) continue;
                var row = UiTree.Spawn<VisualElement>(sourceRowTemplate, list);
                if (row == null) break;
                BindSourceRow(row, source.repo);
                sourceViews.Add((source.repo, row, false));
                ShowSource(row, source);
            }
            shownSources = SourcesKey();
        }

        string SourcesKey()
        {
            var key = new StringBuilder();
            foreach (var s in ModSources.All)
                if (InSection(s)) key.Append(s.repo).Append(s.installed ? '+' : '-').Append(s.packId).Append('|');
            return key.ToString();
        }

        void BindSourceRow(VisualElement row, string repo)
        {
            row.Q<Button>("src-action").clicked += () =>
            {
                Click();
                Install(repo);
            };
            BindRemove(row, row.Q<Button>("src-remove"), repo);
        }

        void BindInstalledSource(VisualElement row, string packId)
        {
            var source = SourceOf(packId);
            if (source == null) return;
            row.Q<Button>("mod-update").clicked += () =>
            {
                Click();
                Install(source.repo);
            };
            BindRemove(row, row.Q<Button>("mod-remove"), source.repo);
            sourceViews.Add((source.repo, row, true));
            ShowInstalled(row, source);
        }

        void BindRemove(VisualElement row, Button remove, string repo)
        {
            remove.tooltip = removeTip;
            remove.clicked += () => AskRemove(row, repo);
            row.Q<Button>("src-keep").clicked += () =>
            {
                Click();
                RemoveSource(repo, false);
            };
            row.Q<Button>("src-delete").clicked += () =>
            {
                Click();
                RemoveSource(repo, true);
            };
            row.RegisterCallback<FocusOutEvent>(e =>
            {
                if (e.relatedTarget is not VisualElement to || !row.Contains(to)) row.RemoveFromClassList(ConfirmClass);
            });
        }

        ModSources.Source SourceOf(string packId)
        {
            foreach (var s in ModSources.All)
                if (s.kind != OtherKind && string.Equals(s.packId, packId, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        ModSources.Kind OtherKind => section == Section.AssetPacks ? ModSources.Kind.Mod : ModSources.Kind.AssetPack;

        void ShowSource(VisualElement row, ModSources.Source source)
        {
            row.SetText("src-title", string.IsNullOrEmpty(source.title) ? source.repo : source.title);
            var details = sourceTexts.Details(source);
            var size = sourceTexts.Size(source.downloadBytes);
            row.SetText("src-details", size != null ? details + sourceTexts.separator + size : details);
            sourceTexts.ShowState(row.Q("src-state"), source);
            ModSourceTexts.ShowProgress(row.Q("src-bar"), source);
            var retry = source.state is ModSources.State.Offline or ModSources.State.Error;
            var action = row.Q<Button>("src-action");
            var remove = row.Q<Button>("src-remove");
            var shown = retry || source.state is ModSources.State.NotInstalled or ModSources.State.Unknown;
            if (!shown && action.focusController?.focusedElement == action) remove.Focus();
            row.EnableInClassList(NoActionClass, !shown);
            row.SetText("src-action-label", retry ? retryText : downloadText);
            action.Q(className: "icon")?.SetIcon(retry ? "replay" : "download");
            remove.SetEnabled(!ModSourceTexts.Busy(source));
        }

        // An installed item from a source, once a check told its state: the state with the installed commit, Update and Remove.
        void ShowInstalled(VisualElement row, ModSources.Source source)
        {
            var known = source.state != ModSources.State.Unknown;
            row.Q("mod-status").SetVisible(known);
            var state = row.Q("mod-source");
            sourceTexts.ShowState(state, source);
            if (!string.IsNullOrEmpty(source.installedCommit) && !ModSourceTexts.Busy(source))
                state.SetText(null, sourceTexts.State(source) + sourceTexts.separator + source.installedCommit);
            state.tooltip = sourceTexts.StateTip(source) ?? source.repo;
            ModSourceTexts.ShowProgress(row.Q("mod-bar"), source);
            var update = row.Q<Button>("mod-update");
            var updatable = source.state == ModSources.State.UpdateAvailable;
            if (!updatable && update.focusController?.focusedElement == update) row.Q<Button>("mod-toggle")?.Focus();
            row.EnableInClassList(UpdateClass, updatable);
            row.EnableInClassList(SourceClass, known);
            update.tooltip = sourceTexts.Details(source);
            row.Q<Button>("mod-remove").SetEnabled(!ModSourceTexts.Busy(source));
        }

        void OnSourcesChanged()
        {
            if (list == null) return;
            if (SourcesKey() != shownSources)
            {
                Fill(false);
                return;
            }
            foreach (var (repo, row, installedRow) in sourceViews)
            {
                var s = ModSources.Find(repo);
                if (s == null) continue;
                if (installedRow) ShowInstalled(row, s);
                else ShowSource(row, s);
            }
            ShowTools();
        }

        void AskRemove(VisualElement row, string repo)
        {
            Click();
            var source = ModSources.Find(repo);
            if (source == null || !source.installed)
            {
                RemoveSource(repo, false);
                return;
            }
            row.AddToClassList(ConfirmClass);
            FocusLater(row.Q<Button>("src-keep"));
        }

        void RemoveSource(string repo, bool deleteFiles)
        {
            ModSources.Remove(repo, deleteFiles);
            GameRoot.Instance.SaveSettings();
            if (deleteFiles)
            {
                ModInstalls.MarkChanged();
                ReloadWhenDone();
                return;
            }
            Fill(false);
            FocusLater(list.childCount > 0 ? FirstRowButton() : sourceInput);
        }

        async void Install(string repo)
        {
            var install = ModInstalls.InstallAsync(repo);
            ShowTools();
            await install;
            if (this != null) ReloadWhenDone();
        }

        async void UpdateAll()
        {
            var repos = new List<string>();
            foreach (var s in ModSources.All)
                if (Updatable(s)) repos.Add(s.repo);
            updateAll.SetEnabled(false);
            foreach (var repo in repos) await ModInstalls.InstallAsync(repo);
            if (this != null) ReloadWhenDone();
        }

        // Comes back here after the reload. Closed meanwhile: the title screen reloads (ModInstalls.ReloadIfPending).
        void ReloadWhenDone()
        {
            if (!ModInstalls.ReloadPending || !IsBound || Flow.Current != this) return;
            GameRoot.Instance.SaveSettings();
            MenuFlow.OpenOnStart = typeof(ModsScreen);
            ModInstalls.ReloadIfPending();
        }
    }
}
