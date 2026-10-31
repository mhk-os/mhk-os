using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MHK.Online;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Tooltips of hidden runs that say why they are hidden (<see cref="OnlineTimeTrials.HiddenReason"/>): asked for when
    /// the tooltip's element is hovered or its row focused, once per run (answers are kept until a moderation action).
    /// Until the answer, and when it cannot be had, the element keeps its fallback text.
    /// </summary>
    public static partial class HiddenReasons
    {
        public static string ModeratorFormat { get; set; } = "Hidden by {0}: {1}";
        public static string ModeratorNoReasonFormat { get; set; } = "Hidden by {0}";
        /// <summary>Hidden from the server's command line (no moderator name).</summary>
        public static string ReasonFormat { get; set; } = "Hidden: {0}";
        public static string DeletedText { get; set; } = "Deleted by the player";
        public static string DisabledFormat { get; set; } = "Account disabled: {0}";
        public static string DisabledText { get; set; } = "Account disabled";

        // The run an element's tooltip explains (0 = none since Detach); callbacks are registered once per element.
        sealed class Binding
        {
            public long runId;
        }

        [AutoStaticsCleanup] static readonly ConditionalWeakTable<VisualElement, Binding> bindings = new();
        [AutoStaticsCleanup] static Dictionary<long, string> texts;
        [AutoStaticsCleanup] static Dictionary<long, Task<OnlineResult<RunHiddenInfo>>> pending;
        // Bumped by Clear: answers asked for before it are dropped.
        [AutoStaticsCleanup] static int generation;

        /// <summary>
        /// Sets <paramref name="target"/>'s tooltip to the known reason of <paramref name="runId"/>, else
        /// <paramref name="fallback"/>, and asks for it when <paramref name="target"/> is hovered or something in
        /// <paramref name="focusArea"/> (default: the target) takes the focus. Attaching the same element again switches it
        /// to the new run.
        /// </summary>
        public static void Attach(VisualElement target, long runId, string fallback, VisualElement focusArea = null)
        {
            target.tooltip = texts != null && texts.TryGetValue(runId, out var known) ? known : fallback;
            if (bindings.TryGetValue(target, out var binding))
            {
                binding.runId = runId;
                return;
            }
            binding = new Binding { runId = runId };
            bindings.Add(target, binding);
            target.RegisterCallback<PointerEnterEvent>(_ => Load(target, binding.runId));
            (focusArea ?? target).RegisterCallback<FocusInEvent>(_ => Load(target, binding.runId));
        }

        /// <summary>Stops explaining <paramref name="target"/> (its run is no longer hidden); its tooltip is the caller's again.</summary>
        public static void Detach(VisualElement target)
        {
            if (bindings.TryGetValue(target, out var binding)) binding.runId = 0;
        }

        /// <summary>Forgets every known reason (after a moderator hid, showed, disabled or enabled something).</summary>
        public static void Clear()
        {
            texts?.Clear();
            pending?.Clear();
            generation++;
        }

        /// <summary>The tooltip text of <paramref name="info"/>, null when the run is not hidden.</summary>
        public static string Text(RunHiddenInfo info)
        {
            if (info == null || !info.Hidden) return null;
            if (info.deletedByOwner) return DeletedText;
            if (info.accountDisabled) return string.IsNullOrEmpty(info.reason) ? DisabledText : string.Format(DisabledFormat, info.reason);
            if (!string.IsNullOrEmpty(info.moderator))
                return string.IsNullOrEmpty(info.reason) ? string.Format(ModeratorNoReasonFormat, info.moderator) : string.Format(ModeratorFormat, info.moderator, info.reason);
            return string.IsNullOrEmpty(info.reason) ? null : string.Format(ReasonFormat, info.reason);
        }

        static void Load(VisualElement target, long runId)
        {
            if (runId == 0) return;
            if (texts != null && texts.TryGetValue(runId, out var known))
            {
                target.tooltip = known;
                return;
            }
            LoadAsync(target, runId).LogExceptionsAndForget();
        }

        static async Awaitable LoadAsync(VisualElement target, long runId)
        {
            var asked = generation;
            pending ??= new Dictionary<long, Task<OnlineResult<RunHiddenInfo>>>();
            if (!pending.TryGetValue(runId, out var task)) pending[runId] = task = OnlineTimeTrials.HiddenReason(runId);
            var r = await task;
            // Failures are asked again on the next hover.
            if (pending.TryGetValue(runId, out var current) && current == task) pending.Remove(runId);
            if (!r.Ok || asked != generation) return;
            var text = Text(r.value);
            if (text == null) return;
            (texts ??= new Dictionary<long, string>())[runId] = text;
            if (target.panel != null && bindings.TryGetValue(target, out var binding) && binding.runId == runId) target.tooltip = text;
        }
    }
}
