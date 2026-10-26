using MHK.Core;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Drives <see cref="OnlineLobby"/> and <see cref="JoinLinks"/> from the game root: lobby pushes, the content
    /// announcement after logins and content reloads, and join links that arrived on other threads. Added to
    /// <see cref="GameRoot"/> at start.
    /// </summary>
    sealed class OnlineLobbyHost : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Attach()
        {
            var root = GameRoot.Instance;
            if (root != null && !root.TryGetComponent<OnlineLobbyHost>(out _)) root.gameObject.AddComponent<OnlineLobbyHost>();
        }

        void OnEnable() => OnlineService.PushReceived += OnPush;

        void OnDisable() => OnlineService.PushReceived -= OnPush;

        static void OnPush(ushort type, WireReader body)
        {
            if (type is >= LobbyPushType.LobbyState and <= LobbyPushType.SoloRaceJoinWaiting or LobbyPushType.RaceResults) OnlineLobby.OnPush(type, body);
        }

        void Update()
        {
            OnlineLobby.Tick();
            JoinLinks.Pump();
        }

        void OnDestroy()
        {
            JoinLinks.StopListening();
            OnlineLobby.RestoreRunInBackground();
        }

        void OnApplicationQuit() => OnlineLobby.RestoreRunInBackground();
    }
}
