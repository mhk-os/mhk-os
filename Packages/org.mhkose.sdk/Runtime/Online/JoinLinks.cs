using System;
using System.Collections.Concurrent;
using MHK.Core;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using Application = UnityEngine.Application;

namespace MHK.Online
{
    /// <summary>
    /// Room links <c>mhkos://join/CODE</c>. They arrive as an OS deep link (<see cref="Application.deepLinkActivated"/>,
    /// <see cref="Application.absoluteURL"/>: Android, macOS), as a command-line argument (Windows and Linux register
    /// the scheme to start the game with the link) or from a second game instance (<see cref="JoinLinkForwarder"/>: a
    /// desktop game started with a link while another one runs hands it over and quits). A link becomes
    /// <see cref="OnlineLobby.PendingJoinCode"/> and opens the Online screen, which logs in and joins the room.
    /// </summary>
    public static partial class JoinLinks
    {
        public const string Scheme = "mhkos";
        const string Prefix = Scheme + "://join/";
        /// <summary>Characters of room codes: no 0/O, 1/I.</summary>
        public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        public const int CodeLength = 6;
        /// <summary>Loopback port a running desktop game takes links from other instances on.</summary>
        public const int ForwardPort = 47350;

        [AutoStaticsCleanup] static readonly ConcurrentQueue<string> arrived = new();
        // Disposed (the port closed) and set back to this when a play session ends.
        [AutoStaticsCleanup] static JoinLinkForwarder.Listener listener = null;

        /// <summary>A link was taken (its room code), after <see cref="OnlineLobby.PendingJoinCode"/> was set.</summary>
        public static event Action<string> Opened;

        /// <summary>The link that joins room <paramref name="code"/>.</summary>
        public static string Link(string code) => Prefix + code;

        /// <summary>The room code in canonical form (upper case, spaces and dashes removed), null when it is not one.</summary>
        public static string NormalizeCode(string code)
        {
            if (code == null) return null;
            var chars = new System.Text.StringBuilder(CodeLength);
            foreach (var c in code)
            {
                if (c == ' ' || c == '-') continue;
                var upper = char.ToUpperInvariant(c);
                if (CodeAlphabet.IndexOf(upper) < 0 || chars.Length == CodeLength) return null;
                chars.Append(upper);
            }
            return chars.Length == CodeLength ? chars.ToString() : null;
        }

        /// <summary>The room code of a join link (scheme and host in any case, a trailing slash allowed), false when it is none.</summary>
        public static bool TryParse(string url, out string code)
        {
            code = null;
            if (string.IsNullOrEmpty(url)) return false;
            url = url.Trim();
            if (!url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var rest = url.Substring(Prefix.Length).TrimEnd('/');
            var query = rest.IndexOfAny(new[] { '?', '#' });
            if (query >= 0) rest = rest.Substring(0, query);
            code = NormalizeCode(rest);
            return code != null;
        }

        /// <summary>
        /// Takes a link (main thread): remembers its room for the Online screen and opens it, now in the main menu or
        /// when the menu starts next. Anything that is not a join link is ignored. Returns whether it was one.
        /// </summary>
        public static bool Handle(string url)
        {
            if (!TryParse(url, out var code))
            {
                if (!string.IsNullOrEmpty(url)) Debug.LogWarning($"[Online] Ignored link '{url}': not a room link.");
                return false;
            }
            Debug.Log($"[Online] Room link {code}.");
            OnlineLobby.PendingJoinCode = code;
            OnlineService.Connect();
            UI.OnlineScreen.OpenForLink();
            Opened?.Invoke(code);
            return true;
        }

        /// <summary>The first join link among the command-line arguments, null when there is none.</summary>
        public static string CommandLineLink()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return arg;
            return null;
        }

        // Before the first scene: a desktop game started with a link while another one runs hands it over and quits;
        // otherwise this game takes links from later instances and handles its own once the game root runs.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Startup()
        {
            Application.deepLinkActivated -= OnDeepLink;
            Application.deepLinkActivated += OnDeepLink;
            var link = CommandLineLink();
            if (string.IsNullOrEmpty(link)) link = Application.absoluteURL;
            if (Desktop)
            {
                if (!string.IsNullOrEmpty(link) && TryParse(link, out _) && JoinLinkForwarder.TryForward(link, ForwardPort))
                {
                    Debug.Log("[Online] The running game took the room link: quitting.");
                    Application.Quit();
                    return;
                }
                listener = JoinLinkForwarder.Listen(ForwardPort, arrived.Enqueue);
            }
            if (!string.IsNullOrEmpty(link)) arrived.Enqueue(link);
        }

        // Standalone desktop players start once per launch; the editor and mobile platforms route links themselves.
        static bool Desktop => Application.platform is RuntimePlatform.WindowsPlayer or RuntimePlatform.LinuxPlayer or RuntimePlatform.OSXPlayer;

        static void OnDeepLink(string url) => arrived.Enqueue(url);

        /// <summary>Handles links that arrived meanwhile (the host calls it every frame).</summary>
        internal static void Pump()
        {
            if (arrived.IsEmpty || GameRoot.Instance == null || SceneFlow.Busy) return;
            while (arrived.TryDequeue(out var url)) Handle(url);
        }

        internal static void StopListening()
        {
            listener?.Dispose();
            listener = null;
        }
    }
}
