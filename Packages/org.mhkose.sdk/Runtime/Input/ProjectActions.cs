using UnityEngine;
using UnityEngine.InputSystem;

namespace MHK.Input
{
    /// <summary>
    /// The project-wide actions asset (MHKControls): its UI map drives UI Toolkit navigation everywhere, the gameplay
    /// maps only run on per-player copies (<see cref="PlayerKartDriver"/>), so they stay off in the shared asset.
    /// </summary>
    public static class ProjectActions
    {
        public const string UiMap = "UI";
        public const string KartMap = "Kart";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void DisableGameplayMaps()
        {
            var actions = InputSystem.actions;
            if (actions == null) return;
            foreach (var map in actions.actionMaps)
                if (map.name != UiMap) map.Disable();
        }
    }
}
