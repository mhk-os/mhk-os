using System;
using UnityEditor;

namespace MHK.Editor
{
    /// <summary>Editor progress bar for a batch job, cleared on dispose. Cancelable by default.</summary>
    public sealed class EditorProgressScope : IDisposable
    {
        readonly string title;
        readonly bool cancelable;

        public EditorProgressScope(string title, bool cancelable = true)
        {
            this.title = title;
            this.cancelable = cancelable;
        }

        /// <summary>The user pressed Cancel during an earlier <see cref="Report"/>.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>Shows <paramref name="info"/> at <paramref name="progress"/> (0..1). Returns true once cancelled.</summary>
        public bool Report(string info, float progress)
        {
            if (cancelable) Cancelled |= EditorUtility.DisplayCancelableProgressBar(title, info, progress);
            else EditorUtility.DisplayProgressBar(title, info, progress);
            return Cancelled;
        }

        /// <summary>Item <paramref name="index"/> of <paramref name="count"/>. Returns true once cancelled.</summary>
        public bool Step(int index, int count, string info) => Report(info, count > 0 ? (float)index / count : 0f);

        public void Dispose() => EditorUtility.ClearProgressBar();
    }
}
