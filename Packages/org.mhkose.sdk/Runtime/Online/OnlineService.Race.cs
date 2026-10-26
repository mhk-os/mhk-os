using System;

namespace MHK.Online
{
    public sealed partial class OnlineService
    {
        /// <summary>
        /// Sends a notify (frame kind 3, never answered) on the open connection. False when not connected; the race
        /// treats a lost connection on its own (<see cref="OnlineRace"/>).
        /// </summary>
        internal static bool Notify(ushort type, Action<WireWriter> body)
        {
            var s = Instance;
            var c = s != null && s.state >= OnlineState.Connected ? s.connection : null;
            return c != null && c.IsOpen && c.Notify(type, body);
        }

        /// <summary>Host name or address of the server in effect (the realtime UDP goes there), null when none parses.</summary>
        internal static string EndpointHost => Instance != null && instance.endpoint != null ? instance.endpoint.Host : null;
    }
}
