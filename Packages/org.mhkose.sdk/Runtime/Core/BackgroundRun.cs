using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// Keeps the game running while its window has no focus (Application.runInBackground) for as long as any feature needs
    /// it: online lobbies and races, and mod downloads on the Mods page (their requests advance only while frames run).
    /// Several features need it at overlapping times, so this is the one owner of the setting: the first holder saves the
    /// player's own setting and turns running in the background on, the last one to let go puts the saved setting back, and
    /// no feature undoes another's. The benchmark and editor captures that set it for a whole session do not go through here.
    /// </summary>
    public static partial class BackgroundRun
    {
        [AutoStaticsCleanup] static readonly HashSet<object> holders = new();
        [AutoStaticsCleanup] static bool before;

        /// <summary>
        /// <paramref name="holder"/> needs the game to keep running without the focus (<paramref name="keep"/> true) or no
        /// longer does (false). Repeated calls with the same value change nothing, so callers may report their need every frame.
        /// </summary>
        public static void Keep(object holder, bool keep)
        {
            if (keep)
            {
                if (!holders.Add(holder) || holders.Count > 1) return;
                before = Application.runInBackground;
                Application.runInBackground = true;
            }
            else if (holders.Remove(holder) && holders.Count == 0)
                Application.runInBackground = before;
        }
    }
}
