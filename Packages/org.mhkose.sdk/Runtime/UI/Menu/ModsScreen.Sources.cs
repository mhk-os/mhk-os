using System;
using System.Collections.Generic;
using System.Text;
using MHK.Assets;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class ModsScreen
    {
        const string NoActionClass = "src-no-action";
        const string UpdateClass = "src-update";
        const string SourceClass = "src-managed";
        const string LinkedClass = "src-linked";

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
        [SerializeField, Tooltip("Question of Remove on a source not installed yet. {0} = title or repository.")]
        string removeSourceQuestion = "Remove {0} from the list?";
        [SerializeField, Tooltip("Question of Remove on an installed item. {0} = title.")]
        string removeInstalledQuestion = "Remove {0}? Its files are deleted.";
        [SerializeField, Tooltip("Confirm texts of the remove questions: a source not installed yet (leaves the list), an installed item (deletes its files).")]
        string removeText = "Remove", deleteFilesText = "Delete";
        [SerializeField, Tooltip("Tooltip of the GitHub link after the title. {0} = repository (owner/name).")]
        string githubTip = "Opens {0} on GitHub";
        [SerializeField, Tooltip("Page the GitHub button opens. {0} = repository (owner/name).")]
        string repoUrlFormat = "https://github.com/{0}";
        [SerializeField, Tooltip("State of a downloaded item the content has not loaded yet, and its tooltip.")]
        string downloadedText = "Downloaded", downloadedTip = "Apply loads it.";
        [SerializeField, Tooltip("Check for Updates: its text, its text while the check runs, and its tooltip.")]
        string checkText = "Check for Updates", checkingText = "Checking", checkTip = "Checks every mod and asset pack for a new release.";
        [SerializeField, Tooltip("Error line when Check for Updates could not reach GitHub.")]
        string checkOfflineText = "GitHub could not be reached. Check the internet connection.";
        [SerializeField, Tooltip("Tooltip of Update All with several updates. {0} = number of items with an update.")]
        string updateAllTip = "Updates {0} items of this section.";
        [SerializeField, Tooltip("Tooltip of Update All with a single update.")]
        string updateOneTip = "Updates 1 item of this section.";

        readonly List<(string repo, VisualElement row, bool installedRow)> sourceViews = new();
        string shownSources;
        TextField sourceInput;
        VisualElement sourceError;
        Button updateAll, checkUpdates;
        bool checkRequested;

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
            updateAll = OnClick("update-all", UpdateAll);
            checkUpdates = OnClick("check-updates", CheckUpdates);
            ModSources.Changed += OnSourcesChanged;
            ModSources.RetryFailedChecks();
        }

        void UnbindSources()
        {
            ModSources.Changed -= OnSourcesChanged;
            sourceViews.Clear();
            shownSources = null;
            sourceInput = null;
            sourceError = null;
            updateAll = checkUpdates = null;
            checkRequested = false;
        }

        ModSources.Kind SectionKind => section == Section.AssetPacks ? ModSources.Kind.AssetPack : ModSources.Kind.Mod;

        // A source belongs to the shown section, or to both while its kind is unknown (before its first check).
        bool InSection(ModSources.Source s) => s.kind == SectionKind || s.kind == ModSources.Kind.Unknown;

        void ShowTools()
        {
            var count = 0;
            foreach (var s in ModSources.All)
                if (Updatable(s)) count++;
            var updates = count > 0;
            var checking = ModSources.Checking;
            var focused = updateAll.focusController?.focusedElement;
            updateAll.SetVisible(updates);
            checkUpdates.SetVisible(!updates);
            if (focused == updateAll && !updates) FocusLater(checkUpdates);
            else if (focused == checkUpdates && updates) FocusLater(updateAll);
            updateAll.tooltip = count == 1 ? updateOneTip : string.Format(updateAllTip, count);
            checkUpdates.SetText("check-updates-label", checking ? checkingText : checkText);
            checkUpdates.EnableInClassList("checking", checking);
            checkUpdates.tooltip = checking ? null : checkTip;
            if (checkRequested && !checking) CheckFinished();
            ShowApply();
        }

        // Stays focusable while the check runs (only looks disabled), so a gamepad keeps its place.
        void CheckUpdates()
        {
            if (ModSources.Checking) return;
            ShowSourceError(null);
            checkRequested = ModSources.CheckAll();
            ShowTools();
        }

        void CheckFinished()
        {
            checkRequested = false;
            foreach (var s in ModSources.All)
                if (s.state == ModSources.State.Offline)
                {
                    ShowSourceError(checkOfflineText);
                    return;
                }
        }

        Button ToolButton() => updateAll.resolvedStyle.display != DisplayStyle.None ? updateAll : checkUpdates;

        bool Updatable(ModSources.Source s) => InSection(s) && s.installed && s.state == ModSources.State.UpdateAvailable;

        VisualElement FirstTool() => ToolButton();

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
                Navigate(e, from, !inTools ? ToolButton() : list.childCount > 0 ? FirstRowButton() : null);
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
                if ((source.installed && Loaded(source)) || !InSection(source)) continue;
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

        // Installed by the last content load. A download since then shows as a source row until Apply loads it.
        static bool Loaded(ModSources.Source s)
        {
            if (s.kind == ModSources.Kind.AssetPack)
            {
                foreach (var p in AssetPacks.Available)
                    if (string.Equals(p.id, s.packId, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            foreach (var p in ModLoader.AvailablePacks)
                if (string.Equals(p.packId, s.packId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        void BindSourceRow(VisualElement row, string repo)
        {
            row.Q<Button>("src-action").clicked += () =>
            {
                Click();
                Install(repo);
            };
            BindGitHub(row.Q<Button>("src-github"), repo);
            ModSourceTexts.BindPause(row.Q("src-progress"), repo, Click);
            BindRemove(row.Q<Button>("src-remove"), repo);
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
            BindGitHub(row.Q<Button>("mod-github"), source.repo);
            row.AddToClassList(LinkedClass);
            BindRemove(row.Q<Button>("mod-remove"), source.repo);
            ModSourceTexts.BindPause(row.Q("mod-progress"), source.repo, Click);
            sourceViews.Add((source.repo, row, true));
            ShowInstalled(row, source);
        }

        void BindGitHub(Button button, string repo)
        {
            if (button == null) return;
            button.tooltip = string.Format(githubTip, repo);
            button.clicked += () =>
            {
                Click();
                Application.OpenURL(string.Format(repoUrlFormat, repo));
            };
        }

        void BindRemove(Button remove, string repo)
        {
            remove.tooltip = removeTip;
            remove.clicked += () => AskRemove(remove, repo);
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
            var state = row.Q("src-state");
            sourceTexts.ShowState(state, source);
            if (source.installed && !ModSourceTexts.InProgress(source)) ShowDownloaded(state);
            sourceTexts.ShowProgress(row.Q("src-progress"), source);
            var retry = !source.installed && source.state is ModSources.State.Offline or ModSources.State.Error;
            var action = row.Q<Button>("src-action");
            var remove = row.Q<Button>("src-remove");
            var shown = retry || (!source.installed && source.state is ModSources.State.NotInstalled or ModSources.State.Unknown);
            if (!shown && action.focusController?.focusedElement == action)
                (ModSourceTexts.InProgress(source) ? row.Q<Button>("src-pause") : remove).Focus();
            row.EnableInClassList(NoActionClass, !shown);
            row.SetText("src-action-label", retry ? retryText : downloadText);
            action.Q(className: "icon")?.SetIcon(retry ? "replay" : "download");
            remove.SetEnabled(!ModSourceTexts.Busy(source));
        }

        void ShowDownloaded(VisualElement state)
        {
            state.SetText(null, downloadedText);
            state.Q(className: "icon")?.SetIcon("cloud_done");
            state.RemoveFromClassList("src-muted");
            state.RemoveFromClassList("src-accent");
            state.RemoveFromClassList("src-bad");
            state.AddToClassList("src-good");
            state.tooltip = downloadedTip;
        }

        // An installed item from a source, once a check told its state: the state with the installed commit, Update and Remove.
        void ShowInstalled(VisualElement row, ModSources.Source source)
        {
            var known = source.state != ModSources.State.Unknown;
            row.Q("mod-status").SetVisible(known);
            var state = row.Q("mod-source");
            sourceTexts.ShowState(state, source);
            if (!string.IsNullOrEmpty(source.installedCommit) && !ModSourceTexts.InProgress(source))
                state.SetText(null, sourceTexts.State(source) + sourceTexts.separator + source.installedCommit);
            state.tooltip = sourceTexts.StateTip(source) ?? source.repo;
            sourceTexts.ShowProgress(row.Q("mod-progress"), source);
            var update = row.Q<Button>("mod-update");
            var updatable = source.state == ModSources.State.UpdateAvailable;
            if (!updatable && update.focusController?.focusedElement == update)
                (ModSourceTexts.InProgress(source) ? row.Q<Button>("mod-pause") : row.Q<Button>("mod-toggle"))?.Focus();
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

        // A source not installed yet only leaves the list, an installed item is deleted with its files.
        void AskRemove(Button remove, string repo)
        {
            Click();
            var source = ModSources.Find(repo);
            var title = string.IsNullOrEmpty(source?.title) ? repo : source.title;
            var installed = source != null && source.installed;
            ConfirmPrompt.Show(remove, string.Format(installed ? removeInstalledQuestion : removeSourceQuestion, title),
                installed ? deleteFilesText : removeText, () => RemoveSource(repo, installed));
        }

        void RemoveSource(string repo, bool deleteFiles)
        {
            ModSources.Remove(repo, deleteFiles);
            GameRoot.Instance.SaveSettings();
            Fill(false);
            FocusLater(list.childCount > 0 ? FirstRowButton() : sourceInput);
        }

        async void Install(string repo)
        {
            var install = ModInstalls.InstallAsync(repo);
            ShowTools();
            await install;
        }

        async void UpdateAll()
        {
            var repos = new List<string>();
            foreach (var s in ModSources.All)
                if (Updatable(s)) repos.Add(s.repo);
            foreach (var repo in repos) await ModInstalls.InstallAsync(repo);
        }
    }
}
