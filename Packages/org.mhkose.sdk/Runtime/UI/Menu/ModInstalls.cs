using System;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.UI
{
    /// <summary>
    /// Mod source checks and downloads started from the menu. They keep running when their screen closes, the content
    /// reload that loads the new mods waits until the last download finished (<see cref="ReloadIfPending"/>).
    /// </summary>
    public static partial class ModInstalls
    {
        [AutoStaticsCleanup] static int running;
        [AutoStaticsCleanup] static bool reloadPending;

        /// <summary>True while a download started here runs.</summary>
        public static bool Running => running > 0;

        /// <summary>Installed or deleted mods wait for a content reload and no download runs.</summary>
        public static bool ReloadPending => reloadPending && running == 0;

        /// <summary>Installs the latest compatible release of <paramref name="repo"/>. False when it failed (the source's state tells why).</summary>
        public static async Awaitable<bool> InstallAsync(string repo)
        {
            running++;
            var installed = false;
            try
            {
                installed = await ModSources.InstallAsync(repo, GameRoot.Instance.destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                running--;
            }
            reloadPending |= installed;
            return installed;
        }

        /// <summary>Content changed on disk outside a download (a deleted mod): the next <see cref="ReloadIfPending"/> reloads.</summary>
        public static void MarkChanged() => reloadPending = true;

        /// <summary>Reloads content (<see cref="SceneFlow.ReloadContent"/>) when <see cref="ReloadPending"/>. True when it started.</summary>
        public static bool ReloadIfPending()
        {
            if (!ReloadPending || SceneFlow.Busy) return false;
            reloadPending = false;
            SceneFlow.ReloadContent();
            return true;
        }

        /// <summary>Checks every source for updates in the background.</summary>
        public static async void Check()
        {
            try
            {
                await ModSources.CheckAsync(GameRoot.Instance.destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
