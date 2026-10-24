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
    /// from the title screen. Download All installs the missing ones one after the other, then content reloads.
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
        [SerializeField, Tooltip("Seconds between the rows' entry animations.")]
        float rowStep = 0.06f;
        [SerializeField, Tooltip("Entry animation delay of the first row, seconds.")]
        float rowFirst = 0.12f;

        // The download outlives the screen (Back while it runs), the next open shows its progress.
        [AutoStaticsCleanup] static bool downloading, failed;
        [AutoStaticsCleanup] static int current;
        [AutoStaticsCleanup] static readonly List<string> targets = new();

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
            }
            UiAnim.Stagger(list, rowStep, rowFirst, "from-left");
            download = OnClick("download", DownloadAll);
            OnClick("skip", Skip);
            FocusTarget = download;
            ModSources.Changed += Refresh;
            if (!downloading && NothingChecked()) ModInstalls.Check();
            Refresh();
        }

        protected override void Unbind()
        {
            base.Unbind();
            ModSources.Changed -= Refresh;
            rows.Clear();
            download = null;
        }

        static bool Needed(ModSources.Source s) => s.BaseMod && !s.installed && !ModSourceTexts.Busy(s) && Downloadable(s);

        static bool Downloadable(ModSources.Source s) =>
            s.state is not (ModSources.State.NoRelease or ModSources.State.NeedsNewerGame or ModSources.State.NeedsModUpdate);

        static bool NothingChecked()
        {
            foreach (var s in ModSources.All)
                if (s.BaseMod && s.state != ModSources.State.Unknown) return false;
            return true;
        }

        async void DownloadAll()
        {
            if (downloading) return;
            downloading = true;
            failed = false;
            targets.Clear();
            foreach (var s in ModSources.All)
                if (Needed(s)) targets.Add(s.repo);
            current = 0;
            Refresh();
            foreach (var repo in targets)
            {
                current++;
                if (!await ModInstalls.InstallAsync(repo)) failed = true;
            }
            downloading = false;
            if (!failed) Finish();
            else if (this != null && IsBound) Refresh();
        }

        // Every missing mod installed: forget an earlier skip and load them (back at the title screen).
        static void Finish()
        {
            GameRoot.Instance.Settings.baseModsSkipped = false;
            GameRoot.Instance.SaveSettings();
            ModInstalls.ReloadIfPending();
        }

        void Skip()
        {
            Settings.baseModsSkipped = true;
            GameRoot.Instance.SaveSettings();
            if (!ModInstalls.ReloadIfPending()) Flow.ShowTitle();
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
            download.SetEnabled(!downloading && needed > 0);
            var retry = failed && !downloading;
            download.SetText("download-label", retry ? retryText : downloadAllText);
            download.Q("download-icon")?.SetIcon(retry ? "replay" : "download");
        }

        void ShowRow(VisualElement row, ModSources.Source s)
        {
            row.SetText("base-title", string.IsNullOrEmpty(s.title) ? s.repo : s.title);
            var present = s.installed && !ModSourceTexts.Busy(s);
            var state = row.Q("base-state");
            texts.ShowState(state, s);
            if (present)
            {
                state.SetText(null, installedText);
                state.Q(className: "icon")?.SetIcon("cloud_done");
                state.SetExclusiveClass(Array.IndexOf(Tones, "src-good"), Tones);
                state.tooltip = null;
            }
            ModSourceTexts.ShowProgress(row.Q("base-bar"), s);
            row.SetText("base-size", present ? "" : texts.Size(s.downloadBytes) ?? "");
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
            else if (failed && AnyOffline()) (text, icon, tone) = (offlineLine, "cloud_off", "src-bad");
            else if (failed) (text, icon, tone) = (failedLine, "error", "src-bad");
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

        static bool AnyOffline()
        {
            foreach (var repo in targets)
                if (ModSources.Find(repo) is { state: ModSources.State.Offline }) return true;
            return false;
        }

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
