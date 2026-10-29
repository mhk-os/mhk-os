using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MHK.Core;
using MHK.Modding;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>Where the announcement of the loaded content stands (ContentHello, needed before any lobby request).</summary>
    public enum ContentAnnouncement
    {
        /// <summary>Not logged in, or the content or connection changed since the last announcement.</summary>
        None,
        Announcing,
        Announced,
        /// <summary>The server refused it or did not answer: <see cref="OnlineLobby.ContentError"/>.</summary>
        Failed,
    }

    /// <summary>
    /// Lobbies on the server (docs/protocol.md, multiplayer): announces the loaded content after every login, reconnect
    /// and content reload (answering the manifests the server asks for), lists the public lobbies, joins, creates and
    /// leaves lobbies, and keeps the lobby the player is in up to date from the lobby pushes. Main thread only; failures
    /// are results, never exceptions. Races start from RaceSetup (OnlineRace) and come back with <see cref="ReturnToLobby"/>.
    /// </summary>
    public static partial class OnlineLobby
    {
        // Manifests of every pack in one frame: a few hundred KiB at most.
        static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(60);
        // Lobby countdowns compare with the server clock: refreshed by a ping now and then while in a lobby.
        const float ClockRefreshSeconds = 60f;

        [AutoStaticsCleanup] static LobbyState current;
        [AutoStaticsCleanup] static RaceResults lastResults;
        [AutoStaticsCleanup] static LobbyLeftPush lastLeft;
        [AutoStaticsCleanup] static ContentAnnouncement content;
        [AutoStaticsCleanup] static string contentError;
        [AutoStaticsCleanup] static List<ContentStrike> strikes = new();
        [AutoStaticsCleanup] static Task<OnlineResult> announcing;
        // What the last announcement (or failure) was for: account, content version and connection.
        [AutoStaticsCleanup] static ulong announcedAccount;
        [AutoStaticsCleanup] static int announcedVersion = -1;
        [AutoStaticsCleanup] static int announcedConnection = -1;
        [AutoStaticsCleanup] static int connection;
        [AutoStaticsCleanup] static OnlineState lastState;
        [AutoStaticsCleanup] static long clockOffsetMs;
        [AutoStaticsCleanup] static float clockAt = -1f;
        [AutoStaticsCleanup] static bool clockPending;
        [AutoStaticsCleanup] static bool? runInBackgroundBefore;

        /// <summary>The lobby, the content announcement or the results changed (main thread).</summary>
        public static event Action Changed;

        /// <summary>Someone joined while this player is the only human racing (their name): the race may end early (<see cref="EndRaceEarly"/>).</summary>
        public static event Action<string> SoloRaceJoinWaiting;

        /// <summary>The result of a race arrived (also kept in <see cref="LastResults"/>).</summary>
        public static event Action<RaceResults> ResultsReceived;

        /// <summary>The player is no longer in the lobby: left, kicked, the lobby closed or the connection was lost.</summary>
        public static event Action<LobbyLeftPush> Left;

        /// <summary>The lobby the player is in, null when none.</summary>
        public static LobbyState Current => current;

        public static bool InLobby => current != null;

        /// <summary>The newest race result (of the current lobby), null before the first one.</summary>
        public static RaceResults LastResults => lastResults;

        /// <summary>Why the player last left a lobby without asking, null when they left themselves or never were in one.</summary>
        public static LobbyLeftPush LastLeft => lastLeft;

        /// <summary>The player saw why they left (<see cref="LastLeft"/>): it is not shown again.</summary>
        public static void ForgetLastLeft() => lastLeft = null;

        public static ContentAnnouncement Content => content;

        /// <summary>Why the announcement failed, null otherwise.</summary>
        public static string ContentError => contentError;

        /// <summary>Packs whose copy here differs from what most players have: they cannot be used in anticheat lobbies.</summary>
        public static IReadOnlyList<ContentStrike> Strikes => strikes;

        /// <summary>A join link (<see cref="JoinLinks"/>) waits for the Online screen to log in and join, null when none.</summary>
        public static string PendingJoinCode
        {
            get => pendingJoinCode;
            internal set
            {
                pendingJoinCode = value;
                KeepRunningInBackground();
            }
        }

        [AutoStaticsCleanup] static string pendingJoinCode;

        /// <summary>The logged-in account's id as lobbies list it, 0 when logged out.</summary>
        public static long MyAccountId => OnlineService.Account != null ? unchecked((long)OnlineService.Account.id) : 0;

        /// <summary>The player's own member entry, null outside a lobby.</summary>
        public static LobbyMember Me => current?.Member(MyAccountId);

        /// <summary>The player owns the private room they are in.</summary>
        public static bool IsOwner => current != null && current.IsPrivate && (Me?.owner ?? false);

        /// <summary>The server clock now (Unix milliseconds), estimated from the last ping.</summary>
        public static long ServerNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + clockOffsetMs;

        /// <summary>Seconds until the lobby's phase ends, 0 without a timer.</summary>
        public static float PhaseSecondsLeft => current == null || current.phaseEnds == 0 ? 0f : Mathf.Max(0f, (current.phaseEnds - ServerNowMs) / 1000f);

        static ContentRegistry Registry => GameRoot.Instance != null ? GameRoot.Instance.Content : null;

        /// <summary>
        /// Announces the loaded content unless that already happened for this login, connection and content (then it
        /// succeeds at once); concurrent callers share one announcement.
        /// </summary>
        public static Task<OnlineResult> EnsureContent()
        {
            if (!OnlineService.IsLoggedIn) return Task.FromResult(new OnlineResult(Status.NotLoggedIn, "Log in on the Account screen first."));
            if (announcing != null) return announcing;
            if (content == ContentAnnouncement.Announced && UpToDate) return Task.FromResult(OnlineResult.Success);
            announcing = AnnounceAsync();
            return announcing;
        }

        static bool UpToDate =>
            OnlineService.Account != null && announcedAccount == OnlineService.Account.id && announcedConnection == connection
            && Registry != null && announcedVersion == Registry.Version;

        static async Task<OnlineResult> AnnounceAsync()
        {
            var registry = Registry;
            var account = OnlineService.Account;
            var forConnection = connection;
            SetContent(ContentAnnouncement.Announcing, null);
            try
            {
                var manifests = ContentManifests.Build(registry);
                var version = registry.Version;
                var hello = new ContentHelloRequest { contentVersion = ContentVersion.Current };
                foreach (var m in manifests) hello.packs.Add(new PackRef { packId = m.packId, commit = m.commit, manifestHash = m.manifestHash });
                var r = await OnlineService.Call(LobbyMessageType.ContentHello, hello.Write, ContentHelloAnswer.Read, true);
                if (!r.Ok) return Fail(r.result, account, version, forConnection);
                var owed = new List<PackManifest>();
                foreach (var m in manifests)
                    if (r.value.missing.Exists(h => string.Equals(h, m.manifestHash, StringComparison.OrdinalIgnoreCase))) owed.Add(m);
                if (owed.Count > 0)
                {
                    var sent = await OnlineService.Call(LobbyMessageType.ContentManifests, w => PackManifest.WriteList(w, owed), _ => true, true, ManifestTimeout);
                    if (!sent.Ok) return Fail(sent.result, account, version, forConnection);
                }
                strikes = r.value.strikes;
                foreach (var s in strikes) Debug.LogWarning($"[Online] {s.message}");
                Debug.Log($"[Online] Content announced: {manifests.Count} packs, {owed.Count} manifests sent, {strikes.Count} differ.");
                Remember(account, version, forConnection);
                SetContent(ContentAnnouncement.Announced, null);
                return OnlineResult.Success;
            }
            finally
            {
                announcing = null;
            }
        }

        static OnlineResult Fail(OnlineResult result, Account account, int version, int forConnection)
        {
            Remember(account, version, forConnection);
            SetContent(ContentAnnouncement.Failed, result.message);
            Debug.LogWarning($"[Online] Content announcement failed: {result.message}");
            return result;
        }

        static void Remember(Account account, int version, int forConnection)
        {
            announcedAccount = account != null ? account.id : 0;
            announcedVersion = version;
            announcedConnection = forConnection;
        }

        static void SetContent(ContentAnnouncement next, string error)
        {
            content = next;
            contentError = error;
            Changed?.Invoke();
        }

        /// <summary>Public lobbies (every category's instances) of this game version.</summary>
        public static async Task<OnlineResult<List<LobbySummary>>> ListLobbies()
        {
            var ready = await EnsureContent();
            if (!ready.Ok) return ready;
            return await OnlineService.Call(LobbyMessageType.ListLobbies, null, LobbySummary.ReadList, true);
        }

        /// <summary>
        /// Joins a public instance or a known room by id. Packs it lacks here or that a mod overrides are explained
        /// without asking the server.
        /// </summary>
        public static Task<OnlineResult> Join(LobbySummary lobby)
        {
            var problem = ContentProblem(lobby.packs);
            if (problem != null) return Task.FromResult(new OnlineResult(Status.BadRequest, problem));
            return JoinWith(LobbyMessageType.JoinLobby, w =>
            {
                w.I64(lobby.lobbyId);
                w.String("", LobbyLimits.Code);
            });
        }

        /// <summary>Joins a private room by its code (case and spaces ignored).</summary>
        public static Task<OnlineResult> JoinByCode(string code)
        {
            code = JoinLinks.NormalizeCode(code);
            if (code == null) return Task.FromResult(new OnlineResult(Status.BadRequest, "A room code has 6 letters and digits."));
            return JoinWith(LobbyMessageType.JoinLobby, w =>
            {
                w.I64(0);
                w.String(code, LobbyLimits.Code);
            });
        }

        /// <summary>Joins the fullest instance of public category <paramref name="category"/> that has room.</summary>
        public static Task<OnlineResult> QuickJoin(int category)
        {
            var problem = LobbyCategories.TryGet(category, out var c) ? ContentProblem(c.packs) : null;
            if (problem != null) return Task.FromResult(new OnlineResult(Status.BadRequest, problem));
            return JoinWith(LobbyMessageType.QuickJoin, w => w.U8((byte)category));
        }

        /// <summary>Opens a private room owned by the player.</summary>
        public static Task<OnlineResult> CreateRoom(RoomSettings settings)
        {
            var problem = ContentProblem(settings.packs);
            if (problem != null) return Task.FromResult(new OnlineResult(Status.BadRequest, problem));
            return JoinWith(LobbyMessageType.CreateRoom, settings.Write);
        }

        static async Task<OnlineResult> JoinWith(ushort type, Action<WireWriter> body)
        {
            var ready = await EnsureContent();
            if (!ready.Ok) return ready;
            var r = await OnlineService.Call(type, body, LobbyState.Read, true);
            if (!r.Ok) return r.result;
            // A room joined by code shows its packs only now.
            var problem = ContentProblem(r.value.settings.packs);
            if (problem != null)
            {
                await OnlineService.Call(LobbyMessageType.LeaveLobby, null, true);
                current = null;
                Changed?.Invoke();
                return new OnlineResult(Status.BadRequest, problem);
            }
            lastLeft = null;
            if (current == null || current.summary.lobbyId != r.value.summary.lobbyId) lastResults = null;
            Apply(r.value);
            RefreshClock();
            ChooseDefaultLoadout();
            return OnlineResult.Success;
        }

        /// <summary>
        /// Why this game cannot play with <paramref name="packs"/>: packs that are not loaded, or definitions of them that
        /// a mod replaces (that mod must be disabled for the lobby). Null when it can.
        /// </summary>
        public static string ContentProblem(IReadOnlyList<string> packs)
        {
            var registry = Registry;
            if (registry == null) return "The game is shutting down.";
            var missing = ContentManifests.MissingPacks(registry, packs);
            if (missing.Count > 0) return $"This lobby needs the mods {string.Join(", ", missing)}. Enable or download them on the Mods screen.";
            var overrides = ContentManifests.Overrides(registry, packs);
            if (overrides.Count == 0) return null;
            var mods = new List<string>();
            var ids = new List<string>();
            foreach (var (id, by) in overrides)
            {
                if (!mods.Contains(by)) mods.Add(by);
                if (ids.Count < 3) ids.Add(id);
            }
            var more = overrides.Count > ids.Count ? $" and {overrides.Count - ids.Count} more" : "";
            return $"{string.Join(", ", mods)} replaces content of this lobby ({string.Join(", ", ids)}{more}). " +
                   "Everybody must race with the same content: disable it on the Mods screen to join.";
        }

        /// <summary>Changes the settings of the player's room (owner, while it waits).</summary>
        public static Task<OnlineResult> UpdateRoom(RoomSettings settings) => Request(LobbyMessageType.UpdateRoom, settings.Write);

        /// <summary>Leaves the lobby (also when the server is gone: the lobby is forgotten here either way).</summary>
        public static async Task<OnlineResult> Leave()
        {
            if (current == null) return OnlineResult.Success;
            current = null;
            lastResults = null;
            lastLeft = null;
            Changed?.Invoke();
            return await OnlineService.Call(LobbyMessageType.LeaveLobby, null, true);
        }

        /// <summary>Chooses the player's driver and kart (lobby content); remembered as the next default.</summary>
        public static Task<OnlineResult> SetLoadout(string characterId, string kartId)
        {
            var settings = GameRoot.Instance.Settings;
            settings.lastOnlineCharacterId = characterId ?? "";
            settings.lastOnlineKartId = kartId ?? "";
            GameRoot.Instance.SaveSettings();
            return Request(LobbyMessageType.SetLoadout, w =>
            {
                w.String(characterId ?? "", LobbyLimits.ContentId);
                w.String(kartId ?? "", LobbyLimits.ContentId);
            });
        }

        public static Task<OnlineResult> SetReady(bool ready) => Request(LobbyMessageType.SetReady, w => w.Bool(ready));

        /// <summary>Votes for the next track while voting is open, "" = Random.</summary>
        public static Task<OnlineResult> Vote(string trackId) => Request(LobbyMessageType.Vote, w => w.String(trackId ?? "", LobbyLimits.ContentId));

        /// <summary>Owner of a private room: starts the next race on <paramref name="trackId"/> (owner pick), or the voting ("").</summary>
        public static Task<OnlineResult> StartRace(string trackId) => Request(LobbyMessageType.StartRace, w => w.String(trackId ?? "", LobbyLimits.ContentId));

        /// <summary>Ends the player's race against AI early (someone waits to race them).</summary>
        public static Task<OnlineResult> EndRaceEarly() => Request(LobbyMessageType.EndRaceEarly, null);

        /// <summary>Removes a member: the room owner within the room, moderators anywhere (audit-logged).</summary>
        public static Task<OnlineResult> KickMember(long accountId, string reason) => Request(LobbyMessageType.KickMember, w =>
        {
            w.I64(accountId);
            w.String(LobbyWire.Clip(reason, LobbyLimits.KickReason), LobbyLimits.KickReason);
        });

        static async Task<OnlineResult> Request(ushort type, Action<WireWriter> body)
        {
            var ready = await EnsureContent();
            if (!ready.Ok) return ready;
            return await OnlineService.Call(type, body, true);
        }

        /// <summary>After an online race: the main menu with the lobby screen (the online screen when the lobby is gone).</summary>
        public static void ReturnToLobby()
        {
            UI.MenuFlow.OpenOnStart = InLobby ? typeof(UI.LobbyScreen) : typeof(UI.OnlineScreen);
            SceneFlow.GoToMenu();
        }

        // The last online loadout when it is content here, else the last menu driver, else the first ones, each with
        // the driver's default kart when that is content too.
        static void ChooseDefaultLoadout()
        {
            var me = Me;
            var registry = Registry;
            if (me == null || me.HasLoadout || registry == null) return;
            var packs = current.settings.packs;
            var characters = ContentManifests.LobbyContent<CharacterDefinition>(registry, packs);
            var karts = ContentManifests.LobbyContent<KartDefinition>(registry, packs);
            if (characters.Count == 0 || karts.Count == 0) return;
            var settings = GameRoot.Instance.Settings;
            var character = Pick(characters, settings.lastOnlineCharacterId) ?? Pick(characters, settings.lastCharacterId) ?? characters[0];
            var kart = Pick(karts, settings.lastOnlineKartId);
            if (kart == null && character.defaultKart != null) kart = Pick(karts, character.defaultKart.Id);
            kart ??= karts[0];
            Request(LobbyMessageType.SetLoadout, w =>
            {
                w.String(character.Id, LobbyLimits.ContentId);
                w.String(kart.Id, LobbyLimits.ContentId);
            }).LogFailure("default loadout");
        }

        static T Pick<T>(List<T> list, string id) where T : ContentDefinition =>
            string.IsNullOrEmpty(id) ? null : list.Find(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        static void Apply(LobbyState state)
        {
            current = state;
            Changed?.Invoke();
        }

        // Clock offset from a ping: the server time at the middle of the round trip.
        static async void RefreshClock()
        {
            if (clockPending) return;
            clockPending = true;
            try
            {
                var sent = DateTimeOffset.UtcNow;
                var r = await OnlineService.Call(MessageType.Ping, new PingRequest { clientTime = sent }.Write, PingResponse.Read, false);
                if (!r.Ok) return;
                var now = DateTimeOffset.UtcNow;
                var middle = sent.ToUnixTimeMilliseconds() + (now - sent).Ticks / TimeSpan.TicksPerMillisecond / 2;
                clockOffsetMs = r.value.serverTime.ToUnixTimeMilliseconds() - middle;
                clockAt = Time.realtimeSinceStartup;
                Changed?.Invoke();
            }
            finally
            {
                clockPending = false;
            }
        }

        // From the host: pushes of the lobby range and the race results.
        internal static void OnPush(ushort type, WireReader body)
        {
            try
            {
                switch (type)
                {
                    case LobbyPushType.LobbyState:
                        var state = LobbyState.Read(body);
                        if (current == null) return; // a push that crossed our LeaveLobby
                        Apply(state);
                        break;
                    case LobbyPushType.LobbyLeft:
                        var left = LobbyLeftPush.Read(body);
                        if (current == null) return;
                        LeftLobby(left);
                        break;
                    case LobbyPushType.SoloRaceJoinWaiting:
                        SoloRaceJoinWaiting?.Invoke(body.String(LobbyLimits.Username));
                        break;
                    case LobbyPushType.RaceResults:
                        lastResults = RaceResults.Read(body);
                        ResultsReceived?.Invoke(lastResults);
                        Changed?.Invoke();
                        break;
                }
            }
            catch (WireFormatException e)
            {
                Debug.LogWarning($"[Online] Malformed lobby push {type}: {e.Message}");
            }
        }

        static void LeftLobby(LobbyLeftPush left)
        {
            current = null;
            lastResults = null;
            lastLeft = left;
            Debug.Log($"[Online] Left the lobby ({left.reason}): {left.message}");
            Left?.Invoke(left);
            Changed?.Invoke();
        }

        // From the host every frame: a new connection or login means a new announcement (and no lobby: the server takes
        // a player out of it when the connection drops); content reloads announce again.
        internal static void Tick()
        {
            var state = OnlineService.State;
            if (state != lastState)
            {
                if (lastState == OnlineState.LoggedIn || state < OnlineState.Connected) ConnectionEnded(state);
                if (state == OnlineState.LoggedIn) connection++;
                lastState = state;
            }
            KeepRunningInBackground();
            if (state != OnlineState.LoggedIn || announcing != null || Registry == null) return;
            var stale = announcedConnection != connection || announcedAccount != OnlineService.Account.id || announcedVersion != Registry.Version;
            if (stale) EnsureContent().LogFailure("content announcement");
            if (current != null && (clockAt < 0f || Time.realtimeSinceStartup - clockAt > ClockRefreshSeconds)) RefreshClock();
        }

        static void ConnectionEnded(OnlineState state)
        {
            if (content != ContentAnnouncement.None) SetContent(ContentAnnouncement.None, null);
            if (current != null && state < OnlineState.LoggedIn)
                LeftLobby(new LobbyLeftPush { reason = LobbyLeftReason.Closed, message = "The connection to the server was lost." });
        }

        // In a lobby the game keeps running without the focus: an unfocused client would miss RaceSetup and the race
        // start. The same while a room link waits to log in and join (a game started by a link may open behind other
        // windows). The player's own setting comes back once out of the lobby and its race (OnlineRace keeps it during races).
        static void KeepRunningInBackground()
        {
            var wanted = current != null || pendingJoinCode != null;
            if (wanted && runInBackgroundBefore == null)
            {
                runInBackgroundBefore = Application.runInBackground;
                Application.runInBackground = true;
            }
            else if (!wanted && runInBackgroundBefore != null && OnlineRace.Current == null)
            {
                Application.runInBackground = runInBackgroundBefore.Value;
                runInBackgroundBefore = null;
            }
        }

        /// <summary>Gives the player's own run-in-background setting back (the game root shuts down; in the editor it is a project setting).</summary>
        internal static void RestoreRunInBackground()
        {
            if (runInBackgroundBefore == null) return;
            Application.runInBackground = runInBackgroundBefore.Value;
            runInBackgroundBefore = null;
        }

        static void LogFailure(this Task<OnlineResult> task, string what) => LogFailureAsync(task, what);

        static async void LogFailureAsync(Task<OnlineResult> task, string what)
        {
            var r = await task;
            if (!r.Ok && r.status != Status.NotLoggedIn) Debug.LogWarning($"[Online] {what}: {r.message}");
        }
    }
}
