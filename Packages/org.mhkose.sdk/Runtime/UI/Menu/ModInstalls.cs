using System;
using MHK.Core;
using MHK.Modding;
using UnityEngine;

namespace MHK.UI
{
    /// <summary>
    /// Mod and asset pack downloads started from the menu. They keep running when their screen closes and never reload
    /// content: the Mods page's Apply (or the first start screen's Continue) loads them (<see cref="ModSources.PendingChanges"/>).
    /// </summary>
    public static class ModInstalls
    {
        /// <summary>Installs the latest compatible release of <paramref name="repo"/>. False when it failed or was paused (the source's state tells which).</summary>
        public static async Awaitable<bool> InstallAsync(string repo)
        {
            try
            {
                return await ModSources.InstallAsync(repo, GameRoot.Instance.destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return false;
        }

        /// <summary>Saves the settings and reloads content behind the loading screen (<see cref="SceneFlow.ReloadContent"/>).</summary>
        public static void ReloadContent()
        {
            GameRoot.Instance.SaveSettings();
            SceneFlow.ReloadContent();
        }
    }
}
