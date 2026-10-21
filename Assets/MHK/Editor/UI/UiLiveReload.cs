using System.Linq;
using MHK.UI;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.UI
{
    /// <summary>
    /// Play mode live reload: a (re)imported UXML makes every bound screen re-clone its layout and bind again
    /// (all screens, so templates are covered). USS changes apply on their own.
    /// </summary>
    public class UiLiveReload : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!EditorApplication.isPlaying || !imported.Any(p => p.EndsWith(".uxml"))) return;
            foreach (var screen in Object.FindObjectsByType<UiScreen>(FindObjectsInactive.Exclude))
                if (screen.IsBound) screen.Rebuild();
        }
    }
}
