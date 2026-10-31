using System;
using System.Threading.Tasks;
using UnityEngine;

namespace MHK.Online
{
    public sealed partial class OnlineService
    {
        const string NotLoggedInMessage = "Log in on the Account screen first.";

        /// <summary>
        /// One request of the phase-2 APIs: connects (and resumes the stored session) when needed, sends, reads the answer
        /// with <paramref name="read"/> and turns every failure into a result, so callers never catch. A request that
        /// <paramref name="needsLogin"/> fails locally with <see cref="Status.NotLoggedIn"/> when no account is logged in.
        /// Continues on the main thread.
        /// </summary>
        internal static async Task<OnlineResult<T>> Call<T>(ushort type, Action<WireWriter> body, Func<WireReader, T> read, bool needsLogin,
            TimeSpan? timeout = null)
        {
            var s = Instance;
            if (s == null) return OnlineResult.Failed("The game is shutting down.");
            if (needsLogin && s.state != OnlineState.LoggedIn && Username == null) return new OnlineResult(Status.NotLoggedIn, NotLoggedInMessage);
            var error = await s.EnsureConnectedAsync();
            if (error != null) return OnlineResult.Failed(error);
            if (needsLogin && s.state != OnlineState.LoggedIn) return new OnlineResult(Status.NotLoggedIn, NotLoggedInMessage);
            try
            {
                var r = await RequestAsync(type, body, timeout);
                if (!r.Ok) return new OnlineResult(r.status, Message(r, $"The server refused the request ({r.status})."));
                return new OnlineResult<T>(OnlineResult.Success, read(r.body));
            }
            catch (OnlineException e)
            {
                return OnlineResult.Failed(e.Message);
            }
            catch (WireFormatException)
            {
                return OnlineResult.Failed(MalformedAnswer);
            }
            catch (ArgumentException e)
            {
                // A field over its protocol limit: a bug of the caller, reported but not thrown at the UI.
                Debug.LogException(e);
                return OnlineResult.Failed(e.Message);
            }
        }

        /// <summary><see cref="Call{T}"/> of a request whose answer is empty.</summary>
        internal static async Task<OnlineResult> Call(ushort type, Action<WireWriter> body, bool needsLogin)
        {
            var r = await Call(type, body, _ => true, needsLogin);
            return r.result;
        }

        /// <summary>
        /// Connects and resumes the stored session when an account is being logged in (as <see cref="Call{T}"/> does with
        /// needsLogin), so callers that need <see cref="Account"/> before their first request can wait for it.
        /// </summary>
        internal static async Task<OnlineResult> EnsureLoggedIn()
        {
            var s = Instance;
            if (s == null) return OnlineResult.Failed("The game is shutting down.");
            if (s.state != OnlineState.LoggedIn && Username == null) return new OnlineResult(Status.NotLoggedIn, NotLoggedInMessage);
            var error = await s.EnsureConnectedAsync();
            if (error != null) return OnlineResult.Failed(error);
            return s.state == OnlineState.LoggedIn ? OnlineResult.Success : new OnlineResult(Status.NotLoggedIn, NotLoggedInMessage);
        }

        /// <summary>The server ended the session itself (account deleted): forgets it here; the connection stays, logged out.</summary>
        internal static void ForgetSession()
        {
            var s = Instance;
            if (s != null) s.SessionRevoked(null);
        }

        /// <summary>A request answered with the logged-in account changed (renamed): the account, its stored session and the UI follow.</summary>
        internal static void ApplyAccount(Account account)
        {
            var s = Instance;
            if (s != null) s.UpdateAccount(account);
        }
    }
}
