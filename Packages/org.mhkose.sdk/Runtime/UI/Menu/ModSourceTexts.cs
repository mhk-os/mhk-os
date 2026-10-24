using System;
using System.Globalization;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>Texts and icons that describe a mod source (<see cref="ModSources.Source"/>): its state, commits and download size.</summary>
    [Serializable]
    public sealed class ModSourceTexts
    {
        [Tooltip("State text before the first check.")]
        public string unknown = "Not checked";
        [Tooltip("State text while the releases are checked.")]
        public string checking = "Checking";
        [Tooltip("State text of a source whose mod is not downloaded.")]
        public string notInstalled = "Not installed";
        [Tooltip("State text of an installed mod without a newer release.")]
        public string upToDate = "Up to date";
        [Tooltip("State text of an installed mod with a newer compatible release.")]
        public string updateAvailable = "Update available";
        [Tooltip("State text while downloading. {0} = percent.")]
        public string downloading = "Downloading {0}";
        [Tooltip("State text while the download is unpacked.")]
        public string installing = "Installing";
        [Tooltip("State text when the newest release needs a newer game.")]
        public string needsNewerGame = "Needs a newer game";
        [Tooltip("Tooltip of the needs a newer game state.")]
        public string needsNewerGameTip = "The newest release was made for a newer version of the game. Update the game to get it.";
        [Tooltip("State text of a repository without a release this game can use.")]
        public string noRelease = "No release";
        [Tooltip("Tooltip of the no release state.")]
        public string noReleaseTip = "The repository has no mod release yet.";
        [Tooltip("State text without an internet connection.")]
        public string offline = "Offline";
        [Tooltip("Tooltip of the offline state.")]
        public string offlineTip = "GitHub could not be reached. Check the internet connection.";
        [Tooltip("State text after a failed check or download (the tooltip shows the error).")]
        public string error = "Error";
        [Tooltip("Installed commit. {0} = commit.")]
        public string installedFormat = "Installed {0}";
        [Tooltip("Commit of the newest compatible release. {0} = commit.")]
        public string latestFormat = "Latest {0}";
        [Tooltip("Separator between the parts of a line.")]
        public string separator = " | ";
        [Tooltip("Download size in megabytes. {0} = size.")]
        public string megabytesFormat = "{0} MB";
        [Tooltip("Download size below one megabyte, in kilobytes. {0} = size.")]
        public string kilobytesFormat = "{0} KB";

        static readonly string[] Icons =
        {
            "sync", "sync", "cloud_download", "cloud_done", "update", "download", "download", "warning", "warning", "cloud_off", "error"
        };

        static readonly string[] StateClasses =
        {
            "src-muted", "src-muted", "src-muted", "src-good", "src-accent", "src-accent", "src-accent", "src-bad", "src-muted", "src-bad", "src-bad"
        };

        /// <summary>Material Symbols icon name of <paramref name="state"/>.</summary>
        public static string Icon(ModSources.State state) => Icons[(int)state];

        /// <summary>One-line state text of <paramref name="source"/> (with the download percent while downloading).</summary>
        public string State(ModSources.Source source) => source.state switch
        {
            ModSources.State.Checking => checking,
            ModSources.State.NotInstalled => notInstalled,
            ModSources.State.UpToDate => upToDate,
            ModSources.State.UpdateAvailable => updateAvailable,
            ModSources.State.Downloading => string.Format(downloading, UiText.Percent(Mathf.Round(Progress(source) * 100f))),
            ModSources.State.Installing => installing,
            ModSources.State.NeedsNewerGame => needsNewerGame,
            ModSources.State.NoRelease => noRelease,
            ModSources.State.Offline => offline,
            ModSources.State.Error => error,
            _ => unknown
        };

        /// <summary>Tooltip of the state of <paramref name="source"/>, null when the text says it all.</summary>
        public string StateTip(ModSources.Source source) => source.state switch
        {
            ModSources.State.NeedsNewerGame => needsNewerGameTip,
            ModSources.State.NoRelease => noReleaseTip,
            ModSources.State.Offline => offlineTip,
            ModSources.State.Error => source.error,
            _ => null
        };

        /// <summary>The repository, installed and latest commit of <paramref name="source"/> as one line.</summary>
        public string Details(ModSources.Source source)
        {
            var line = source.repo;
            if (!string.IsNullOrEmpty(source.installedCommit)) line += separator + string.Format(installedFormat, source.installedCommit);
            if (!string.IsNullOrEmpty(source.latestCommit) && source.latestCommit != source.installedCommit)
                line += separator + string.Format(latestFormat, source.latestCommit);
            return line;
        }

        /// <summary>Download size (KB below 1 MB, 1 decimal below 10 MB), null when unknown.</summary>
        public string Size(long bytes)
        {
            if (bytes <= 0) return null;
            if (bytes < 1024 * 1024) return string.Format(kilobytesFormat, Math.Max(1L, bytes / 1024).ToString(CultureInfo.InvariantCulture));
            var mb = bytes / (1024.0 * 1024.0);
            return string.Format(megabytesFormat, mb.ToString(mb < 10.0 ? "0.0" : "0", CultureInfo.InvariantCulture));
        }

        /// <summary>Download progress 0..1 of <paramref name="source"/> (0 while the size is unknown).</summary>
        public static float Progress(ModSources.Source source) =>
            source.downloadBytes > 0 ? Mathf.Clamp01((float)((double)source.downloadedBytes / source.downloadBytes)) : 0f;

        /// <summary>True while <paramref name="source"/> downloads or installs.</summary>
        public static bool Busy(ModSources.Source source) =>
            source.state is ModSources.State.Downloading or ModSources.State.Installing;

        /// <summary>Sets the state icon, text, colour class and tooltip of <paramref name="source"/> on a state element (icon + label).</summary>
        public void ShowState(VisualElement stateElement, ModSources.Source source)
        {
            if (stateElement == null) return;
            stateElement.Q(className: "icon")?.SetIcon(Icon(source.state));
            var label = stateElement.Q<Label>();
            if (label != null) label.text = State(source);
            stateElement.tooltip = StateTip(source);
            foreach (var c in StateClasses) stateElement.RemoveFromClassList(c);
            stateElement.AddToClassList(StateClasses[(int)source.state]);
        }

        /// <summary>Sets the width of a progress bar fill (child with class <c>src-bar-fill</c>) and shows the bar while <paramref name="source"/> is busy.</summary>
        public static void ShowProgress(VisualElement bar, ModSources.Source source)
        {
            if (bar == null) return;
            var busy = Busy(source);
            bar.SetVisible(busy);
            if (!busy) return;
            var fill = bar.Q(className: "src-bar-fill");
            if (fill != null) fill.style.width = Length.Percent(source.state == ModSources.State.Installing ? 100f : Progress(source) * 100f);
        }
    }
}
