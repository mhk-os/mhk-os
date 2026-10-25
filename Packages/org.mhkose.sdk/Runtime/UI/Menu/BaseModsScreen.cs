using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Built-in mods download (BaseMods.uxml): after the graphics first run screen while built-in mods are missing, and
    /// from the title screen. Each row downloads (pauses, resumes) its mod, Download All the missing ones one after the
    /// other. Downloads never reload: Continue loads what was downloaded once.
    /// </summary>
    public partial class BaseModsScreen : MenuScreen
    {
        [SerializeField, Tooltip("Built-in mod row (Templates/BaseModRow.uxml).")]
        VisualTreeAsset rowTemplate;
        [SerializeField, Tooltip("State and size texts of the built-in mods.")]
        ModSourceTexts texts = new();
        [SerializeField, Tooltip("State text of a built-in mod that is installed.")]
        string installedText = "Installed";
        [SerializeField, Tooltip("Overall line before the download. {0} = number of mods, {1} = total size.")]
        string readyFormat = "{0} mods | {1}";
        [SerializeField, Tooltip("Overall line before the download while the sizes are unknown. {0} = number of mods.")]
        string readyNoSizeFormat = "{0} mods";
        [SerializeField, Tooltip("Overall line while downloading. {0} = number of the current mod, {1} = number of mods, {2} = percent of all.")]
        string progressFormat = "Mod {0} of {1} | {2}";
        [SerializeField, Tooltip("Overall line when a download failed because GitHub could not be reached.")]
        string offlineLine = "No internet connection.";
        [SerializeField, Tooltip("Overall line when a download failed for another reason (the mod's state tooltip tells why).")]
        string failedLine = "Some mods could not be downloaded.";
        [SerializeField, Tooltip("Overall line when every built-in mod is installed.")]
        string doneLine = "All mods installed.";
        [SerializeField, Tooltip("Overall line when the missing mods have no release this game can use yet.")]
        string unavailableLine = "No downloads available yet.";
        [SerializeField, Tooltip("Download button texts: before the first try and after a failed one.")]
        string downloadAllText = "Download All", retryText = "Retry";
        [SerializeField, Tooltip("Row button texts: before the download, after a failed one, once installed. It hides while the mod downloads.")]
        string rowDownloadText = "Download", rowRetryText = "Retry", rowInstalledText = "Installed";
        [SerializeField, Tooltip("Footer button texts: before anything was downloaded (core game only) and after a download (loads the mods).")]
        string continueWithoutText = "Continue Without", continueText = "Continue";
        [SerializeField, Tooltip("Footer button tooltips: before anything was downloaded and after a download.")]
        string continueWithoutTip = "Starts with the core game only. The title screen offers the download later.",
            continueTip = "Loads the downloaded mods.";
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.06f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.12f;

        // The download outlives the screen (Back while it runs), the next open shows its progress.
        [AutoStaticsCleanup] static bool downloading;
        [AutoStaticsCleanup] static int current;
        [AutoStaticsCleanup] static readonly List<string> targets = new();
        [AutoStaticsCleanup] static readonly HashSet<string> rowDownloads = new(StringComparer.OrdinalIgnoreCase);
        [AutoStaticsCleanup] static readonly HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);

        readonly List<(string repo, VisualElement row)> rows = new();
        Button download;

        void Reset() => showcaseVisible = false;

        protected override void Bind()
        {
            var list = Q<VisualElement>("base-list");
            list.Clear();
            foreach (var s in ModSources.All)
            {
                if (!s.BaseMod) continue;
                var row = UiTree.Spawn<VisualElement>(rowTemplate, list);
                if (row == null) break;
                rows.Add((s.repo, row));
                var repo = s.repo;
                row.Q<Button>("base-action").clicked += () =>
                {
                    Click();
                    DownloadOne(repo);
                };
                ModSourceTexts.BindPause(row.Q("base-progress"), repo, Click);
            }
            UiAnim.Stagger(list, rowStep, rowFirst, "from-left");
            list.RegisterCallback<NavigationMoveEvent>(OnRowsNavigate);
            download = OnClick("download", DownloadAll);
            OnClick("skip", Continue);
            FocusTarget = download;
            ModSources.Changed += Refresh;
            Refresh();
        }

        protected override void Unbind()
        {
            base.Unbind();
            ModSources.Changed -= Refresh;
            rows.Clear();
            download = null;
        }

        // Up and down go to the nearest row with a usable button (the same one when it can). Nothing above the first, below
        // the last MenuScreen goes to the footer.
        void OnRowsNavigate(NavigationMoveEvent e)
        {
            var up = e.direction == NavigationMoveEvent.Direction.Up;
            if ((!up && e.direction != NavigationMoveEvent.Direction.Down) || e.target is not Button from) return;
            var index = -1;
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].row.Contains(from)) index = i;
            for (var i = index + (up ? -1 : 1); i >= 0 && i < rows.Count; i += up ? -1 : 1)
            {
                var to = RowButton(rows[i].row, from.name);
                if (to == null) continue;
                to.Focus();
                e.StopPropagation();
                from.focusController?.IgnoreEvent(e);
                return;
            }
            if (!up) return;
            e.StopPropagation();
            from.focusController?.IgnoreEvent(e);
        }

        static Button RowButton(VisualElement row, string name)
        {
            var same = row.Q<Button>(name);
            return same != null && Usable(same) ? same : row.Query<Button>().Where(Usable).First();
        }

        static bool Usable(Button b) =>
            b.enabledInHierarchy && b.resolvedStyle.display != DisplayStyle.None && b.resolvedStyle.visibility == Visibility.Visible;

        static bool Needed(ModSources.Source s) => s.BaseMod && !s.installed && !ModSourceTexts.Busy(s) && Downloadable(s);

        static bool Downloadable(ModSources.Source s) =>
            s.state is not (ModSources.State.NoRelease or ModSources.State.NeedsNewerGame or ModSources.State.NeedsModUpdate);

        async void DownloadAll()
        {
            if (downloading) return;
            downloading = true;
            targets.Clear();
            foreach (var s in ModSources.All)
                if (Needed(s) && !rowDownloads.Contains(s.repo)) targets.Add(s.repo);
            current = 0;
            Refresh();
            foreach (var repo in targets)
            {
                current++;
                if (rowDownloads.Contains(repo) || ModSources.Find(repo) is not { installed: false }) continue;
                tried.Add(repo);
                await ModInstalls.InstallAsync(repo);
            }
            downloading = false;
            AfterDownload();
        }

        async void DownloadOne(string repo)
        {
            if (rowDownloads.Contains(repo) || ModSources.Find(repo) is not { } s || s.installed || ModSourceTexts.InProgress(s)) return;
            rowDownloads.Add(repo);
            tried.Add(repo);
            Refresh();
            await ModInstalls.InstallAsync(repo);
            rowDownloads.Remove(repo);
            AfterDownload();
        }

        void AfterDownload()
        {
            if (this != null && IsBound) Refresh();
        }

        static bool MissingDownloadable()
        {
            foreach (var s in ModSources.All)
                if (s.BaseMod && !s.installed && Downloadable(s)) return true;
            return false;
        }

        static bool Failed(bool offlineOnly)
        {
            foreach (var s in ModSources.All)
                if (s.BaseMod && FailedDownload(s) && (!offlineOnly || s.state == ModSources.State.Offline)) return true;
            return false;
        }

        static bool FailedDownload(ModSources.Source s) =>
            !s.installed && tried.Contains(s.repo) && s.state is ModSources.State.Offline or ModSources.State.Error;

        // Remembers a skip while mods are still missing (the title screen then offers the download) and loads what was
        // downloaded. Downloads still running go on, the Mods page's Apply loads them.
        void Continue()
        {
            Settings.baseModsSkipped = MissingDownloadable();
            if (ModSources.PendingChanges) ModInstalls.ReloadContent();
            else
            {
                GameRoot.Instance.SaveSettings();
                Flow.ShowTitle();
            }
        }

        void Refresh()
        {
            if (download == null) return;
            int needed = 0, unavailable = 0;
            long bytes = 0;
            foreach (var (repo, row) in rows)
            {
                var s = ModSources.Find(repo);
                if (s == null) continue;
                ShowRow(row, s);
                if (s.installed) continue;
                if (!Downloadable(s)) unavailable++;
                else
                {
                    needed++;
                    bytes += Math.Max(0, s.downloadBytes);
                }
            }
            ShowTotal(needed, unavailable, bytes);
            var canDownload = !downloading && needed > 0;
            if (!canDownload && download.focusController?.focusedElement == download) FocusLater(Q<Button>("skip"));
            download.SetEnabled(canDownload);
            var retry = Failed(false) && !downloading;
            download.SetText("download-label", retry ? retryText : downloadAllText);
            download.Q("download-icon")?.SetIcon(retry ? "replay" : "download");
            var skip = Q<Button>("skip");
            var loads = ModSources.PendingChanges;
            skip.SetText("skip-label", loads ? continueText : continueWithoutText);
            skip.tooltip = loads ? continueTip : continueWithoutTip;
        }

        void ShowRow(VisualElement row, ModSources.Source s)
        {
            row.SetText("base-title", string.IsNullOrEmpty(s.title) ? s.repo : s.title);
            var present = s.installed && !ModSourceTexts.InProgress(s);
            var state = row.Q("base-state");
            texts.ShowState(state, s);
            if (present)
            {
                state.SetText(null, installedText);
                state.Q(className: "icon")?.SetIcon("cloud_done");
                state.SetExclusiveClass(Array.IndexOf(Tones, "src-good"), Tones);
                state.tooltip = null;
            }
            texts.ShowProgress(row.Q("base-progress"), s);
            if (!s.installed && rowDownloads.Contains(s.repo)) row.Q("base-progress").SetVisible(true);
            row.SetText("base-size", present ? "" : texts.Size(s.downloadBytes) ?? "");
            ShowRowAction(row.Q<Button>("base-action"), s, present);
        }

        void ShowRowAction(Button action, ModSources.Source s, bool present)
        {
            var busy = !present && (ModSourceTexts.InProgress(s) || rowDownloads.Contains(s.repo));
            var retry = !busy && FailedDownload(s);
            string text = rowDownloadText, icon = "download";
            if (present) (text, icon) = (rowInstalledText, "check");
            else if (retry) (text, icon) = (rowRetryText, "replay");
            action.SetText("base-action-label", text);
            action.Q("base-action-icon")?.SetIcon(icon);
            var enabled = !present && !busy && Downloadable(s);
            action.tooltip = enabled || present ? null : texts.StateTip(s);
            action.style.visibility = busy ? Visibility.Hidden : Visibility.Visible;
            if ((!enabled || busy) && action.focusController?.focusedElement == action) FocusLater(FocusAfter(action, busy));
            action.SetEnabled(enabled);
        }

        // A row button that goes away hands the focus to the row's pause button while it downloads, else to the footer.
        VisualElement FocusAfter(Button action, bool busy)
        {
            var pause = busy ? action.parent?.Q<Button>(className: "src-pause") : null;
            if (pause != null) return pause;
            return download.enabledSelf ? download : Q<Button>("skip");
        }

        void ShowTotal(int needed, int unavailable, long bytes)
        {
            var total = Q<VisualElement>("base-total");
            var bar = Q<VisualElement>("base-total-bar");
            bar.SetVisible(downloading);
            string text, icon, tone;
            if (downloading)
            {
                var progress = OverallProgress();
                (text, icon, tone) = (string.Format(progressFormat, current, targets.Count, UiText.Percent(Mathf.Round(progress * 100f))), "download", "src-accent");
                bar.Q(className: "src-bar-fill").style.width = Length.Percent(progress * 100f);
            }
            else if (Failed(true)) (text, icon, tone) = (offlineLine, "cloud_off", "src-bad");
            else if (Failed(false)) (text, icon, tone) = (failedLine, "error", "src-bad");
            else if (needed == 0 && unavailable > 0) (text, icon, tone) = (unavailableLine, "warning", "src-muted");
            else if (needed == 0) (text, icon, tone) = (doneLine, "cloud_done", "src-good");
            else
            {
                var size = texts.Size(bytes);
                (text, icon, tone) = (size != null ? string.Format(readyFormat, needed, size) : string.Format(readyNoSizeFormat, needed), "cloud_download", "src-muted");
            }
            total.SetText("base-total-label", text);
            total.Q(className: "icon")?.SetIcon(icon);
            total.SetExclusiveClass(Array.IndexOf(Tones, tone), Tones);
        }

        static readonly string[] Tones = { "src-muted", "src-good", "src-accent", "src-bad" };

        // Bytes of the finished and the running download over all, a mod of unknown size weighs the average. Failed ones drop out.
        static float OverallProgress()
        {
            double known = 0;
            var sized = 0;
            foreach (var repo in targets)
                if (ModSources.Find(repo) is { downloadBytes: > 0 } k)
                {
                    known += k.downloadBytes;
                    sized++;
                }
            var fallback = sized > 0 ? known / sized : 1.0;
            double done = 0, all = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var s = ModSources.Find(targets[i]);
                var finished = i < current - 1;
                if (s == null || (finished && !s.installed)) continue;
                var weight = s.downloadBytes > 0 ? s.downloadBytes : fallback;
                all += weight;
                if (finished) done += weight;
                else if (i == current - 1) done += weight * (s.state == ModSources.State.Installing ? 1f : ModSourceTexts.Progress(s));
            }
            return all > 0 ? (float)(done / all) : 0f;
        }
    }
}
