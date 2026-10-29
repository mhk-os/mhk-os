using System;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>Where the control connection stands.</summary>
    public enum OnlineState
    {
        /// <summary>No connection (never wanted, failed, or waiting to retry: <see cref="OnlineService.RetryIn"/>).</summary>
        Offline,
        Connecting,
        /// <summary>Connected and greeted, no account logged in.</summary>
        Connected,
        LoggedIn,
    }

    /// <summary>Outcome of an account operation: the server's status and message, or no status when it never answered.</summary>
    public readonly struct OnlineResult
    {
        /// <summary>The server's answer, null when there was none (not connected, connection lost, timed out).</summary>
        public readonly Status? status;
        /// <summary>For the player; empty on success.</summary>
        public readonly string message;

        public OnlineResult(Status? status, string message)
        {
            this.status = status;
            this.message = message ?? "";
        }

        public bool Ok => status == Status.Ok;

        public static readonly OnlineResult Success = new(Status.Ok, "");
        public static OnlineResult Failed(string message) => new(null, message);
    }

    /// <summary>Outcome of an online operation with an answer: <see cref="value"/> is set when <see cref="Ok"/>.</summary>
    public readonly struct OnlineResult<T>
    {
        public readonly OnlineResult result;
        public readonly T value;

        public OnlineResult(OnlineResult result, T value = default)
        {
            this.result = result;
            this.value = value;
        }

        public bool Ok => result.Ok;

        public static implicit operator OnlineResult<T>(OnlineResult failure) => new(failure);
    }

    /// <summary>
    /// The game's link to the online server: the one place other code goes through. A component of <see cref="GameRoot"/>
    /// with a static API (like the audio manager). It connects lazily (<see cref="Connect"/>: the Account screen, or a
    /// stored session at start), greets the server, resumes the stored session of the endpoint, reconnects with backoff
    /// (1, 2, 4, 8, 15, 30 s) and resumes again, and forgets a session the server revoked. Everything here runs on the
    /// main thread; <see cref="Changed"/> fires on every visible change.
    /// </summary>
    public sealed partial class OnlineService : MonoBehaviour
    {
        static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 15, 30 };
        static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Default wait for a response.</summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        [AutoStaticsCleanup] static OnlineService instance;

        /// <summary>State, account or connection message changed (main thread).</summary>
        public static event Action Changed;

        /// <summary>
        /// A push other than Disconnect arrived (main thread): its type (<see cref="PushType"/>) and body. Handlers filter
        /// by type and leave the reader of other types untouched.
        /// </summary>
        public static event Action<ushort, WireReader> PushReceived;

        OnlineConnection connection;
        TaskCompletionSource<bool> attempt;
        int generation;
        OnlineState state;
        string lastError;
        Account account;
        string ipCountry;
        string serverVersion;
        ushort udpPort;
        OnlineEndpoint endpoint;
        string endpointError;
        StoredSessions store;
        int buildNumber;
        // The player (Account screen) or a stored session wants a connection: lost ones are retried.
        bool wanted;
        int failures;
        float retryAt = -1f;
        DisconnectPush closing;

        static OnlineService Instance
        {
            get
            {
                if (instance == null)
                {
                    var root = GameRoot.Instance;
                    if (root == null) return null;
                    instance = root.GetComponent<OnlineService>();
                    if (instance == null) instance = root.gameObject.AddComponent<OnlineService>();
                }
                return instance;
            }
        }

        public static OnlineState State => Instance != null ? instance.state : OnlineState.Offline;

        /// <summary>Why the last connection or resume failed (for the player), null when nothing failed.</summary>
        public static string LastError => Instance != null ? instance.lastError : null;

        /// <summary>The logged-in account, null otherwise.</summary>
        public static Account Account => Instance != null && instance.state == OnlineState.LoggedIn ? instance.account : null;

        public static bool IsLoggedIn => Account != null;
        public static bool IsModerator => Account?.IsModerator ?? false;

        /// <summary>
        /// The name the player is known by online: the logged-in account's, else that of the session stored for this
        /// server (while it resumes or waits to reconnect), else null.
        /// </summary>
        public static string Username
        {
            get
            {
                var s = Instance;
                if (s == null) return null;
                if (s.state == OnlineState.LoggedIn) return s.account.username;
                return s.endpoint != null ? s.store.Find(s.endpoint.Text)?.username : null;
            }
        }

        /// <summary>Country the server detected from the registration IP, null while unknown (seen while the account used it).</summary>
        public static string IpCountry => Instance != null ? instance.ipCountry : null;

        /// <summary>The server in effect (command line, setting or built-in), canonical; the raw text when it does not parse.</summary>
        public static string EndpointText => Instance == null ? "" : instance.endpoint != null ? instance.endpoint.Text : instance.ConfiguredText;

        /// <summary>Why the configured server address is unusable, null when it parses.</summary>
        public static string EndpointError => Instance != null ? instance.endpointError : null;

        /// <summary>Whether the open connection is encrypted (TLS).</summary>
        public static bool Encrypted => Instance != null && instance.connection != null && instance.connection.Tls;

        /// <summary>Build label of the connected server, null when not connected.</summary>
        public static string ServerVersion => Instance != null && instance.state >= OnlineState.Connected ? instance.serverVersion : null;

        /// <summary>The server's realtime UDP port (0 = none), from its greeting.</summary>
        public static int UdpPort => Instance != null && instance.state >= OnlineState.Connected ? instance.udpPort : 0;

        /// <summary>Seconds until the next reconnect attempt, 0 when none is scheduled.</summary>
        public static float RetryIn => Instance != null && instance.retryAt >= 0f ? Mathf.Max(0f, instance.retryAt - Time.realtimeSinceStartup) : 0f;

        /// <summary>
        /// Wants a connection from now on and starts one unless it is open or on its way (a scheduled retry starts now).
        /// Called when the Account screen opens.
        /// </summary>
        public static void Connect()
        {
            var s = Instance;
            if (s == null) return;
            s.wanted = true;
            if (s.state == OnlineState.Offline) s.StartAttempt();
        }

        /// <summary>
        /// Sets <see cref="UserSettings.onlineServer"/> (empty = built-in server), saves the settings and, when the server
        /// in effect changes, drops the connection and connects to the new one.
        /// </summary>
        public static void SetServer(string text)
        {
            var s = Instance;
            if (s == null) return;
            var root = GameRoot.Instance;
            root.Settings.onlineServer = (text ?? "").Trim();
            root.SaveSettings();
            s.UseConfiguredEndpoint(false);
        }

        /// <summary>Creates an account and logs it in (the session is stored for this server).</summary>
        public static Awaitable<OnlineResult> Register(string username, string password) =>
            SessionRequest(MessageType.Register, new CredentialsRequest { username = username, password = password }.Write);

        /// <summary>Logs an account in (the session is stored for this server).</summary>
        public static Awaitable<OnlineResult> Login(string username, string password) =>
            SessionRequest(MessageType.Login, new CredentialsRequest { username = username, password = password }.Write);

        /// <summary>Ends the session on the server and forgets it here (also while offline). The connection stays.</summary>
        public static async Awaitable<OnlineResult> Logout()
        {
            var s = Instance;
            if (s == null) return OnlineResult.Failed("The game is shutting down.");
            if (s.endpoint != null) s.store.Remove(s.endpoint.Text);
            var wasLoggedIn = s.state == OnlineState.LoggedIn;
            s.account = null;
            s.ipCountry = null;
            if (wasLoggedIn) s.SetState(OnlineState.Connected);
            if (s.connection != null) s.connection.LoggedIn = false;
            s.lastError = null;
            Changed?.Invoke();
            if (!wasLoggedIn) return OnlineResult.Success;
            try
            {
                var r = await RequestAsync(MessageType.Logout, null);
                return new OnlineResult(r.status, r.message);
            }
            catch (OnlineException e)
            {
                return OnlineResult.Failed(e.Message);
            }
        }

        /// <summary>Chooses the account's country (ISO 3166-1 alpha-2), empty = back to the one detected from the IP.</summary>
        public static async Awaitable<OnlineResult> SetCountry(string country)
        {
            var s = Instance;
            if (s == null || s.state != OnlineState.LoggedIn) return OnlineResult.Failed("Log in first.");
            try
            {
                var r = await RequestAsync(MessageType.SetCountry, new SetCountryRequest { country = (country ?? "").ToUpperInvariant() }.Write);
                if (!r.Ok) return new OnlineResult(r.status, r.message);
                s.UpdateAccount(Account.Read(r.body));
                return OnlineResult.Success;
            }
            catch (OnlineException e)
            {
                return OnlineResult.Failed(e.Message);
            }
            catch (WireFormatException)
            {
                return OnlineResult.Failed(MalformedAnswer);
            }
        }

        /// <summary>
        /// Sends a request on the open connection (any type: later features build on this) and waits for its response,
        /// at most <paramref name="timeout"/> (default <see cref="RequestTimeout"/>). Throws <see cref="OnlineException"/>
        /// when not connected or without an answer. Continues on the main thread.
        /// </summary>
        public static Task<OnlineConnection.Response> RequestAsync(ushort type, Action<WireWriter> body, TimeSpan? timeout = null)
        {
            var s = Instance;
            var c = s != null && s.state >= OnlineState.Connected ? s.connection : null;
            if (c == null || !c.IsOpen) return Task.FromException<OnlineConnection.Response>(new OnlineException("Not connected to the server."));
            return c.RequestAsync(type, body, timeout ?? RequestTimeout);
        }

        const string MalformedAnswer = "The server sent a malformed answer.";

        static async Awaitable<OnlineResult> SessionRequest(ushort type, Action<WireWriter> body)
        {
            var s = Instance;
            if (s == null) return OnlineResult.Failed("The game is shutting down.");
            var error = await s.EnsureConnectedAsync();
            if (error != null) return OnlineResult.Failed(error);
            try
            {
                var r = await RequestAsync(type, body);
                if (!r.Ok) return new OnlineResult(r.status, r.message);
                s.ApplySession(Session.Read(r.body));
                return OnlineResult.Success;
            }
            catch (OnlineException e)
            {
                return OnlineResult.Failed(e.Message);
            }
            catch (WireFormatException)
            {
                return OnlineResult.Failed(MalformedAnswer);
            }
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(this);
                return;
            }
            instance = this;
            buildNumber = BuildInfo.Number;
            store = StoredSessions.Load();
            UseConfiguredEndpoint(true);
        }

        void Update()
        {
            Pump();
            if (wanted && state == OnlineState.Offline && retryAt >= 0f && Time.realtimeSinceStartup >= retryAt) StartAttempt();
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            Shutdown();
        }

        void OnApplicationQuit() => Shutdown();

        void Shutdown()
        {
            generation++;
            wanted = false;
            retryAt = -1f;
            DropConnection();
            attempt?.TrySetResult(false);
            attempt = null;
        }

        string ConfiguredText => OnlineEndpoint.ConfiguredText(GameRoot.Instance != null ? GameRoot.Instance.Settings.onlineServer : "", buildNumber);

        // Reads the server in effect. At start a stored session for it asks for a connection; later a different server
        // replaces the connection.
        void UseConfiguredEndpoint(bool startup)
        {
            OnlineEndpoint.TryParse(ConfiguredText, out var next, out endpointError);
            if (!startup && Equals(next, endpoint))
            {
                Changed?.Invoke();
                return;
            }
            generation++;
            DropConnection();
            attempt?.TrySetResult(false);
            attempt = null;
            endpoint = next;
            account = null;
            ipCountry = null;
            failures = 0;
            retryAt = -1f;
            lastError = endpointError;
            state = OnlineState.Offline;
            if (endpoint != null && store.Find(endpoint.Text) != null) wanted = true;
            if (wanted && endpoint != null) StartAttempt();
            else Changed?.Invoke();
        }

        void SetState(OnlineState next)
        {
            state = next;
            Changed?.Invoke();
        }

        void DropConnection()
        {
            connection?.Dispose();
            connection = null;
            closing = null;
        }

        // Resolves once the current attempt (connect, greet, resume) is over: null when connected, else the reason.
        async Awaitable<string> EnsureConnectedAsync()
        {
            wanted = true;
            if (state >= OnlineState.Connected && connection != null && connection.IsOpen) return null;
            if (endpoint == null) return endpointError;
            if (attempt == null) StartAttempt();
            var current = attempt;
            if (current != null) await current.Task;
            return state >= OnlineState.Connected ? null : lastError ?? "Not connected to the server.";
        }

        void StartAttempt()
        {
            if (attempt != null) return;
            retryAt = -1f;
            if (endpoint == null)
            {
                lastError = endpointError;
                Changed?.Invoke();
                return;
            }
            attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RunAttemptAsync(++generation, attempt).LogExceptionsAndForget();
        }

        async Awaitable RunAttemptAsync(int gen, TaskCompletionSource<bool> done)
        {
            try
            {
                await ConnectAndGreetAsync(gen);
            }
            finally
            {
                if (attempt == done) attempt = null;
                done.TrySetResult(state >= OnlineState.Connected);
            }
        }

        async Awaitable ConnectAndGreetAsync(int gen)
        {
            lastError = null;
            SetState(OnlineState.Connecting);
            OnlineConnection c;
            try
            {
                c = await OnlineConnection.ConnectAsync(endpoint, ConnectTimeout, destroyCancellationToken);
            }
            catch (OnlineException e)
            {
                if (gen == generation) Fail(e.Message, !e.Permanent);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (gen != generation)
            {
                c.Dispose();
                return;
            }
            connection = c;
            closing = null;
            try
            {
                var hello = new HelloRequest
                {
                    gameCommit = BuildInfo.Commit, gameBuild = buildNumber, contentVersion = ContentVersion.Current,
                    modApi = ModSdk.ApiLevel, platform = Application.platform.ToString(),
                };
                var r = await c.RequestAsync(MessageType.Hello, hello.Write, RequestTimeout);
                if (gen != generation) return;
                if (!r.Ok)
                {
                    Fail(Message(r, "The server refused the connection."), r.status != Status.VersionMismatch);
                    return;
                }
                var ok = HelloOk.Read(r.body);
                serverVersion = ok.serverVersion;
                udpPort = ok.udpPort;
                failures = 0;
                Debug.Log($"[Online] Connected to {endpoint.Text} ({(c.Tls ? "TLS" : "plaintext")}), server {serverVersion}.");
                SetState(OnlineState.Connected);
                await ResumeAsync(gen, c);
            }
            catch (OnlineException e)
            {
                if (gen == generation) Fail(e.Message, true);
            }
            catch (WireFormatException)
            {
                if (gen == generation) Fail(MalformedAnswer, true);
            }
        }

        async Awaitable ResumeAsync(int gen, OnlineConnection c)
        {
            var stored = store.Find(endpoint.Text);
            if (stored == null) return;
            var r = await c.RequestAsync(MessageType.Resume, new ResumeRequest { token = stored.token }.Write, RequestTimeout);
            if (gen != generation) return;
            if (r.Ok)
            {
                ApplySession(Session.Read(r.body));
                return;
            }
            if (r.status == Status.SessionInvalid || r.status == Status.AccountDisabled)
            {
                SessionRevoked(Message(r, "Your session ended. Log in again."));
                return;
            }
            lastError = Message(r, "Could not resume the session.");
            Changed?.Invoke();
        }

        // The server no longer accepts the stored session: forget it and stop reconnecting on our own.
        void SessionRevoked(string message)
        {
            if (endpoint != null) store.Remove(endpoint.Text);
            wanted = false;
            retryAt = -1f;
            account = null;
            ipCountry = null;
            if (connection != null) connection.LoggedIn = false;
            lastError = message;
            if (state == OnlineState.LoggedIn) state = OnlineState.Connected;
            Changed?.Invoke();
        }

        void ApplySession(Session session)
        {
            if (endpoint != null) store.Put(endpoint.Text, session);
            if (connection != null) connection.LoggedIn = true;
            lastError = null;
            account = null;
            SetAccount(session.account);
            if (state != OnlineState.LoggedIn) SetState(OnlineState.LoggedIn);
        }

        void SetAccount(Account next)
        {
            account = next;
            if (next.countryFromIp) ipCountry = next.country;
            Changed?.Invoke();
        }

        // A newer view of the logged-in account (renamed here or on another connection); another account's is ignored.
        void UpdateAccount(Account next)
        {
            if (state != OnlineState.LoggedIn || account == null || account.id != next.id) return;
            if (endpoint != null) store.SetUsername(endpoint.Text, next.username);
            SetAccount(next);
        }

        // The attempt or connection failed: offline with the reason, and a retry after the next backoff step when wanted.
        void Fail(string reason, bool retry)
        {
            DropConnection();
            account = null;
            lastError = reason;
            if (retry && wanted)
            {
                retryAt = Time.realtimeSinceStartup + BackoffSeconds[Mathf.Min(failures, BackoffSeconds.Length - 1)];
                failures++;
            }
            else
            {
                retryAt = -1f;
                if (!retry) wanted = false;
            }
            Debug.Log($"[Online] {reason}{(retryAt >= 0f ? $" Retrying in {RetryIn:0} s." : "")}");
            SetState(OnlineState.Offline);
        }

        // Pushes and the end of the connection, from the connection's threads.
        void Pump()
        {
            while (connection != null && connection.TryDequeue(out var item))
            {
                if (item.closed)
                {
                    Closed(item.reason);
                    return;
                }
                if (item.type == PushType.Disconnect)
                {
                    try
                    {
                        closing = DisconnectPush.Read(item.body);
                    }
                    catch (WireFormatException)
                    {
                        Debug.LogWarning("[Online] Malformed Disconnect push.");
                    }
                    continue;
                }
                if (item.type == PushType.AccountUpdated)
                {
                    try
                    {
                        UpdateAccount(Account.Read(item.body));
                    }
                    catch (WireFormatException)
                    {
                        Debug.LogWarning("[Online] Malformed AccountUpdated push.");
                    }
                    continue;
                }
                PushReceived?.Invoke(item.type, item.body);
            }
        }

        void Closed(string reason)
        {
            var notice = closing;
            if (notice != null && (notice.status == Status.SessionInvalid || notice.status == Status.AccountDisabled))
            {
                SessionRevoked(string.IsNullOrEmpty(notice.message) ? "Your session ended. Log in again." : notice.message);
                Fail(lastError, false);
                return;
            }
            Fail(notice != null && !string.IsNullOrEmpty(notice.message) ? notice.message : reason, true);
        }

        static string Message(OnlineConnection.Response r, string fallback) => string.IsNullOrEmpty(r.message) ? fallback : r.message;
    }
}
