using System;
using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Modding;
using MHK.Race;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// One online race (docs/protocol.md, "Multiplayer"): lives from the server's RaceSetup push until the player is
    /// back in the lobby. It owns the realtime UDP seat (clock sync, own kart states out, other racers' states in),
    /// routes the race pushes, sends the race notifies and maps grid slots to karts. A child of <see cref="GameRoot"/>,
    /// so it survives the scene change into the race and back. Everything here runs on the main thread.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public sealed partial class OnlineRace : MonoBehaviour
    {
        /// <summary>Observer and observed RTT at most this (server-measured) for a check or report to count (<see cref="Eligible"/>).</summary>
        public const int EligibleRttMs = 200;

        [AutoStaticsCleanup] public static OnlineRace Current { get; private set; }

        public RaceSetup Setup { get; private set; }
        public long RaceId => Setup.raceId;
        public int LocalSlot => Setup.yourSlot;
        /// <summary>Plausibility checks and reports run: an anticheat lobby with other humans to check.</summary>
        public bool Anticheat => Setup.anticheat && !Solo;
        /// <summary>The local player is the only human of the race (with or without AI): someone may wait to race them.</summary>
        public bool Solo => humans == 1;
        /// <summary>The race counts towards the lobby's set (a solo race in a public lobby does not, and gives no points).</summary>
        public bool InSet => Setup.setRace > 0;
        /// <summary>Grid slot of the human that reports the AI karts' item events (<see cref="AiHost"/>), NoSlot without AI.</summary>
        public int AiHostSlot { get; private set; }
        /// <summary>This client reports for the AI karts (see docs/protocol.md, "AI drivers").</summary>
        public bool IsAiHost => AiHostSlot == LocalSlot && !LocalLeft;
        /// <summary>AI karts in the race.</summary>
        public int AiCount => aiSlots.Count;
        public bool Battle => Setup.Battle;

        /// <summary>What this client sent as RaceLoaded (null before the track loaded).</summary>
        public RaceLoaded Loaded { get; private set; }
        /// <summary>Sorted status effect ids: the index table of <see cref="KartState.effects"/> (empty before loading).</summary>
        public IReadOnlyList<string> EffectIds => effectIds;

        /// <summary>The server gave GO (<see cref="GoServerMs"/>).</summary>
        public bool HasGo => goServerMs != 0;
        /// <summary>Server clock instant of GO (Unix ms), 0 before RaceGo.</summary>
        public long GoServerMs => goServerMs;
        /// <summary>Estimated server clock (Unix ms).</summary>
        public double ServerNowMs => udp != null ? udp.ServerNowMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        /// <summary>Seconds until GO on the synced clock, +infinity before RaceGo.</summary>
        public double SecondsToGo => HasGo ? (goServerMs - ServerNowMs) / 1000.0 : double.PositiveInfinity;

        /// <summary>Race ticks: physics steps since GO, 0 before.</summary>
        public int Tick => RaceManager.Current != null ? RaceManager.Current.RaceTicks : 0;

        /// <summary>The server took the local racer out of the race (kicked, desync); the outcome and message say why.</summary>
        public bool LocalLeft { get; private set; }
        public int LocalLeftOutcome { get; private set; }
        public string LocalLeftMessage { get; private set; } = "";

        /// <summary>The server's result of this race, null until it arrived.</summary>
        public RaceResults Results { get; private set; }

        /// <summary>Solo race: who waits to race the player (<c>SoloRaceJoinWaiting</c>), null when nobody.</summary>
        public string JoinWaitingUser { get; private set; }
        /// <summary>The player asked to end the solo race early (sent once).</summary>
        public bool EndRequested { get; private set; }

        /// <summary>Another racer's state arrived (slot, state; the state object is reused after the handlers ran).</summary>
        public event Action<int, KartState> RemoteState;
        /// <summary>A race push of this race arrived (after the race's own handling).</summary>
        public event Action<IRacePush> Pushed;
        /// <summary>A racer left the race (its kart fades out).</summary>
        public event Action<int> RacerLeft;
        /// <summary>GO: the race ticks count from now.</summary>
        public event Action Going;
        /// <summary>The server's result arrived (<see cref="Results"/>).</summary>
        public event Action ResultsArrived;

        OnlineRaceUdp udp;
        long goServerMs;
        readonly Dictionary<int, Kart> kartsBySlot = new();
        readonly Dictionary<int, int> rttBySlot = new();
        readonly HashSet<int> leftSlots = new();
        readonly List<string> effectIds = new();
        readonly HashSet<int> aiSlots = new();
        readonly HashSet<Kart> aiKarts = new();
        // AI slots whose finish this client sent (as AI host) and whose finish the server announced.
        readonly HashSet<int> aiFinishSent = new(), aiFinished = new();
        int humans;
        bool previousRunInBackground;
        float previousMaxDeltaTime;
        /// <summary>Physics time a frame may catch up online: the race ticks must keep pace with the server clock after hitches.</summary>
        const float OnlineMaxDeltaTime = 0.25f;
        bool startRequested;
        bool ending;

        /// <summary>The kart of grid slot <paramref name="slot"/>, null when none (left, not spawned).</summary>
        public Kart KartOf(int slot) => kartsBySlot.TryGetValue(slot, out var k) && k != null ? k : null;

        /// <summary>Grid slot of <paramref name="kart"/>, NoSlot for unknown karts.</summary>
        public int SlotOf(Kart kart)
        {
            if (kart == null) return RaceProtocol.NoSlot;
            foreach (var pair in kartsBySlot)
                if (pair.Value == kart) return pair.Key;
            return RaceProtocol.NoSlot;
        }

        /// <summary>Server-measured RTT of a racer (ms; own: measured here), 0 while unknown (or below 1 ms on a LAN).</summary>
        public int RttMs(int slot)
        {
            if (slot == LocalSlot) return udp != null ? udp.RttMs : 0;
            return rttBySlot.TryGetValue(slot, out var ms) ? ms : 0;
        }

        /// <summary>Whether <see cref="RttMs"/> of <paramref name="slot"/> is a measurement (a Pong or a World entry arrived).</summary>
        public bool RttKnown(int slot) => slot == LocalSlot ? udp != null && udp.Synced : rttBySlot.ContainsKey(slot);

        /// <summary>
        /// The racer of <paramref name="slot"/> has its race loaded as far as this client can tell: its kart states arrive
        /// (a client sends them from its loaded grid), or for the local racer its RaceLoaded went out.
        /// </summary>
        public bool IsLoaded(int slot) => slot == LocalSlot ? Loaded != null : rttBySlot.ContainsKey(slot) && !leftSlots.Contains(slot);

        /// <summary>
        /// A check of <paramref name="observedSlot"/> by <paramref name="observerSlot"/> for <paramref name="reason"/> counts:
        /// the observer's RTT is known and at most <see cref="EligibleRttMs"/>, and so is the observed racer's unless the
        /// reason is a speed or acceleration magnitude, which latency does not change (<see cref="LatencyFree"/>).
        /// </summary>
        public bool Eligible(int observerSlot, int observedSlot, FlagReason reason) =>
            RttEligible(observerSlot) && (LatencyFree(reason) || RttEligible(observedSlot));

        /// <summary>Speed and acceleration findings: magnitudes of the received velocities, the same at any latency.</summary>
        public static bool LatencyFree(FlagReason reason) => reason is FlagReason.Speed or FlagReason.Acceleration;

        bool RttEligible(int slot) => RttKnown(slot) && RttMs(slot) <= EligibleRttMs;

        /// <summary>Sends a race notify of this race. Dropped while not connected (the server then removes the racer anyway).</summary>
        public void Notify(IRaceNotify m)
        {
            if (m == null || ending) return;
            try
            {
                if (!OnlineService.Notify(m.Type, m.Write)) Debug.LogWarning($"[OnlineRace] Notify {m.Type} not sent: not connected.");
            }
            catch (ArgumentException e)
            {
                Debug.LogException(e);
            }
        }

        // ---- Lifetime -------------------------------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Listen()
        {
            OnlineService.PushReceived -= OnServicePush;
            OnlineService.PushReceived += OnServicePush;
        }

        // RaceSetup anywhere (lobby screen, results of the previous race) starts the race; the other race pushes go to
        // the race they name.
        static void OnServicePush(ushort type, WireReader body)
        {
            if (type < RaceProtocol.RaceSetup || type > RaceProtocol.AiHost || type == RaceProtocol.RaceResults) return;
            try
            {
                if (type == RaceProtocol.RaceSetup)
                {
                    Begin(RaceSetup.Read(body));
                    return;
                }
                var push = ReadPush(type, body);
                if (push != null && Current != null && push.RaceId == Current.RaceId) Current.Handle(push);
            }
            catch (WireFormatException e)
            {
                Debug.LogWarning($"[OnlineRace] Malformed race push {type}: {e.Message}");
            }
        }

        static IRacePush ReadPush(ushort type, WireReader r) => type switch
        {
            RaceProtocol.RaceGo => RaceGo.Read(r),
            RaceProtocol.ItemGranted => ItemGranted.Read(r),
            RaceProtocol.ItemBoxTaken => ItemBoxTaken.Read(r),
            RaceProtocol.ItemDenied => ItemDenied.Read(r),
            RaceProtocol.ItemUsed => ItemUsed.Read(r),
            RaceProtocol.ItemUseRejected => ItemUseRejected.Read(r),
            RaceProtocol.ProjectileCorrect => ProjectileCorrect.Read(r),
            RaceProtocol.HitApplied => HitApplied.Read(r),
            RaceProtocol.HitForce => HitForce.Read(r),
            RaceProtocol.PlayerFinished => PlayerFinished.Read(r),
            RaceProtocol.PlayerLeftRace => PlayerLeftRace.Read(r),
            RaceProtocol.Eliminated => Eliminated.Read(r),
            RaceProtocol.BridgeTriggered => BridgeTriggered.Read(r),
            RaceProtocol.AiHost => AiHost.Read(r),
            _ => null,
        };

        static void Begin(RaceSetup setup)
        {
            var root = GameRoot.Instance;
            if (root == null) return;
            // Only the race of the lobby the player is in: a setup arriving after leaving it (or while racing offline)
            // must not pull the player into a race.
            var lobby = OnlineLobby.Current;
            if (lobby == null || lobby.summary.lobbyId != setup.lobbyId)
            {
                Debug.Log($"[OnlineRace] Ignored the setup of race {setup.raceId}: not in lobby {setup.lobbyId}.");
                return;
            }
            // Neither an offline race the player runs (only the menu's background race may give way).
            var running = RaceManager.Current;
            if (running != null && !running.IsOnline && !running.IsAttract)
            {
                Debug.Log($"[OnlineRace] Ignored the setup of race {setup.raceId}: an offline race is running.");
                return;
            }
            if (Current != null) Current.End();
            var go = new GameObject($"Online race {setup.raceId}");
            go.transform.SetParent(root.transform, false);
            var race = go.AddComponent<OnlineRace>();
            race.Setup = setup;
            race.AiHostSlot = setup.aiHost;
            foreach (var s in setup.grid)
                if (s.ai) race.aiSlots.Add(s.slot);
                else race.humans++;
            Current = race;
            race.previousRunInBackground = Application.runInBackground;
            // Every client simulates its own kart: the race must go on while the window has no focus.
            Application.runInBackground = true;
            race.previousMaxDeltaTime = Time.maximumDeltaTime;
            Time.maximumDeltaTime = Mathf.Max(Time.maximumDeltaTime, OnlineMaxDeltaTime);
            race.udp = new OnlineRaceUdp(OnlineService.EndpointHost, setup.udpPort, setup.udpToken, setup.udpKey);
            Debug.Log($"[OnlineRace] Race {setup.raceId} on {setup.trackId}: slot {setup.yourSlot} of {setup.grid.Count}, " +
                      $"{race.aiSlots.Count} AI (host slot {setup.aiHost}), UDP {OnlineService.EndpointHost}:{setup.udpPort}.");
            OnlineLobby.ResultsReceived += race.TakeResults;
            OnlineLobby.SoloRaceJoinWaiting += race.OnJoinWaiting;
            OnlineLobby.Changed += race.OnLobbyChanged;
            race.startRequested = true;
        }

        void Update()
        {
            if (startRequested && !SceneFlow.Busy)
            {
                startRequested = false;
                SceneFlow.StartSession(BuildSession(Setup));
            }
            SyncGameClock();
            PumpUdp();
            if (OnlineService.State != OnlineState.LoggedIn && !ending) Abandon("Connection to the server lost");
        }

        // States reach the stand-ins every physics step, not only once per frame: at low frame rates several steps run
        // per frame and would otherwise all see the states of the previous frame.
        void FixedUpdate()
        {
            PumpUdp();
            SendLocalState();
            SendAiProgress();
        }

        // Out of the lobby during the race (moderator kick, lobby closed): the race cannot go on here.
        void OnLobbyChanged()
        {
            if (!OnlineLobby.InLobby && !ending) Abandon("No longer in the lobby");
        }

        // The control connection dropped or the lobby is gone: the server already removed this racer.
        void Abandon(string why)
        {
            Debug.LogWarning($"[OnlineRace] {why}; leaving the race.");
            End();
            if (!SceneFlow.Busy) OnlineLobby.ReturnToLobby();
        }

        void OnJoinWaiting(string username)
        {
            if (Solo && Results == null) JoinWaitingUser = string.IsNullOrEmpty(username) ? "Someone" : username;
        }

        /// <summary>Solo race: ends it early so the waiting player can race (the server sends the result, a DNF).</summary>
        public async void EndSoloRaceEarly()
        {
            if (!Solo || EndRequested || Results != null) return;
            EndRequested = true;
            var r = await OnlineLobby.EndRaceEarly();
            if (!r.Ok && this != null)
            {
                EndRequested = false;
                Debug.LogWarning($"[OnlineRace] End race early failed: {r.message}");
            }
        }

        /// <summary>Ends this online race here (UDP closed, focus handling restored). Scenes are the caller's business.</summary>
        public void End()
        {
            if (ending) return;
            LogStats();
            Teardown();
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (!ending) Teardown();
        }

        void Teardown()
        {
            ending = true;
            OnlineLobby.ResultsReceived -= TakeResults;
            OnlineLobby.SoloRaceJoinWaiting -= OnJoinWaiting;
            OnlineLobby.Changed -= OnLobbyChanged;
            if (localKart != null) localKart.Respawned -= OnLocalRespawned;
            udp?.Dispose();
            udp = null;
            Application.runInBackground = previousRunInBackground;
            Time.maximumDeltaTime = previousMaxDeltaTime;
            if (Current == this) Current = null;
        }

        // ---- Session --------------------------------------------------------------------------------------------

        /// <summary>The race as a <see cref="GameSession"/>: the grid in order (AI karts first, then the humans).</summary>
        static GameSession BuildSession(RaceSetup s)
        {
            var session = new GameSession
            {
                mode = s.Battle ? GameMode.Battle : GameMode.SingleRace,
                difficulty = (Difficulty)Mathf.Clamp(s.engineClass, 0, 2),
                lapsFromTrack = s.laps == 0,
                laps = s.laps == 0 ? 3 : s.laps,
                powerupsEnabled = s.items,
                online = true,
            };
            session.trackIds.Add(s.trackId);
            foreach (var o in s.itemOdds) session.powerupOdds.Add(new PowerupOdds { id = o.powerupId, percent = o.percent });
            foreach (var slot in s.grid)
            {
                var local = slot.slot == s.yourSlot;
                session.racers.Add(new RacerSetup
                {
                    characterId = slot.characterId,
                    kartId = slot.kartId,
                    isHuman = local,
                    playerIndex = local ? 0 : -1,
                    displayName = slot.ai ? CharacterName(slot.characterId) : slot.username,
                    onlineSlot = slot.slot,
                    country = slot.country,
                    platform = slot.platform,
                });
            }
            session.ResetStandings();
            return session;
        }

        /// <summary>The name an AI driver shows: its character's, the id when the character is not installed.</summary>
        public static string CharacterName(string characterId)
        {
            var root = GameRoot.Instance;
            var c = root != null && !string.IsNullOrEmpty(characterId) ? root.Content.Get<CharacterDefinition>(characterId) : null;
            return c != null ? c.DisplayName : characterId ?? "";
        }

        // ---- Race hooks (RaceManager) ---------------------------------------------------------------------------

        /// <summary>Records the kart of grid slot <paramref name="slot"/> (called by the race as it spawns them).</summary>
        public void Register(int slot, Kart kart)
        {
            if (slot < 0 || kart == null) return;
            kartsBySlot[slot] = kart;
            if (slot == LocalSlot) WatchLocalKart(kart);
            else if (aiSlots.Contains(slot)) aiKarts.Add(kart);
            else if (kart.TryGetComponent<RemoteKart>(out var remote)) RegisterRemote(slot, remote);
        }

        /// <summary>Grid slot <paramref name="slot"/> is an AI kart (every client simulates it; the AI host reports for it).</summary>
        public bool IsAi(int slot) => aiSlots.Contains(slot);

        /// <summary><paramref name="kart"/> is one of the race's AI karts.</summary>
        public bool IsAiKart(Kart kart) => kart != null && aiKarts.Contains(kart);

        /// <summary>The track and karts are ready: sends RaceLoaded with this client's view of the race content.</summary>
        public void SendLoaded(RaceLoaded loaded)
        {
            loaded.raceId = RaceId;
            effectIds.Clear();
            effectIds.AddRange(loaded.effects);
            IndexEffects();
            Loaded = loaded;
            Notify(loaded);
            Debug.Log($"[OnlineRace] Loaded: track hash {loaded.trackHash}, {loaded.laps} laps, {loaded.boxes.Count} boxes, " +
                      $"{loaded.pool.Count} pool entries, {loaded.effects.Count} effects.");
        }

        /// <summary>
        /// Sorted ids of the status effects that are content of a lobby with <paramref name="packs"/> (null: all loaded):
        /// the effect index table every racer must agree on.
        /// </summary>
        public static List<string> EffectTable(IReadOnlyList<string> packs)
        {
            var registry = GameRoot.Instance.Content;
            var ids = new List<string>();
            foreach (var e in registry.All<StatusEffectDefinition>())
                if (packs == null || ContentManifests.IsLobbyContent(registry, e, packs)) ids.Add(e.Id);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        /// <summary>GO was given at this physics step (the race calls it from FixedUpdate).</summary>
        public void OnGo() => Going?.Invoke();

        /// <summary>
        /// A kart crossed the line here: the local racer's finish goes to the server, and so does an AI kart's while this
        /// client is the AI host (once; the server's PlayerFinished finishes it everywhere else).
        /// </summary>
        public void OnKartFinished(Kart kart, RacerProgress p)
        {
            if (kart == null || Battle) return;
            int slot;
            if (kart == localKart)
            {
                if (LocalLeft) return;
                slot = RaceProtocol.NoSlot;
            }
            else if (IsAiHost && IsAiKart(kart))
            {
                slot = SlotOf(kart);
                if (!aiFinishSent.Add(slot) || aiFinished.Contains(slot)) return;
            }
            else return;
            var m = new Finished { raceId = RaceId, finishMs = Mathf.RoundToInt(p.FinishTime * 1000f), tick = Tick, slot = slot };
            foreach (var lap in p.LapTimes) m.lapMs.Add(Mathf.RoundToInt(lap * 1000f));
            Notify(m);
        }

        /// <summary>The server's result (lobby push 523) for this race.</summary>
        public void TakeResults(RaceResults results)
        {
            if (results == null || results.raceId != RaceId || Results != null) return;
            Results = results;
            ResultsArrived?.Invoke();
        }

        // ---- Pushes ---------------------------------------------------------------------------------------------

        void Handle(IRacePush push)
        {
            switch (push)
            {
                case RaceGo g:
                    goServerMs = g.goMs;
                    Debug.Log($"[OnlineRace] GO in {SecondsToGo:0.000} s (clock {(udp != null && udp.Synced ? "synced" : "not synced")}, RTT {RttMs(LocalSlot)} ms).");
                    break;
                case PlayerFinished f:
                    if (IsAi(f.slot)) FinishAi(f);
                    else FinishRemote(f);
                    break;
                case AiHost h:
                    ChangeAiHost(h.slot);
                    break;
                case PlayerLeftRace left:
                    RemoveRacer(left);
                    break;
            }
            Pushed?.Invoke(push);
        }

        void FinishRemote(PlayerFinished f)
        {
            var kart = KartOf(f.slot);
            if (kart == null || !kart.IsRemote) return;
            var laps = new List<float>(f.lapMs.Count);
            foreach (var ms in f.lapMs) laps.Add(ms / 1000f);
            kart.Progress.SetRemoteLapTimes(laps);
            kart.Progress.ForceFinish(f.finishMs / 1000f);
        }

        // The server took an AI kart's finish (from the AI host): it finishes here too unless it already has.
        void FinishAi(PlayerFinished f)
        {
            aiFinished.Add(f.slot);
            var kart = KartOf(f.slot);
            if (kart != null && !kart.Progress.Finished) kart.Progress.ForceFinish(f.finishMs / 1000f);
        }

        // The AI host left: a new host reports from its own AI karts on, first the finishes the old one never sent.
        void ChangeAiHost(int slot)
        {
            AiHostSlot = slot;
            Debug.Log($"[OnlineRace] AI host is slot {slot}{(IsAiHost ? " (this client)" : "")}.");
            if (!IsAiHost) return;
            foreach (var slotAndKart in kartsBySlot)
                if (aiSlots.Contains(slotAndKart.Key) && slotAndKart.Value != null && slotAndKart.Value.Progress.Finished)
                    OnKartFinished(slotAndKart.Value, slotAndKart.Value.Progress);
        }

        void RemoveRacer(PlayerLeftRace left)
        {
            if (left.slot == LocalSlot)
            {
                LocalLeft = true;
                LocalLeftOutcome = left.outcome;
                LocalLeftMessage = left.message ?? "";
                var own = KartOf(left.slot);
                if (own != null) own.ControlsEnabled = false;
                Debug.Log($"[OnlineRace] Removed from the race (outcome {left.outcome}): {left.message}");
                return;
            }
            leftSlots.Add(left.slot);
            var kart = KartOf(left.slot);
            kartsBySlot.Remove(left.slot);
            rttBySlot.Remove(left.slot);
            if (kart != null && kart.TryGetComponent<RemoteKart>(out var remote))
            {
                leftStats.Add(remote.StatsReport() + " (left)");
                remote.Leave();
            }
            RacerLeft?.Invoke(left.slot);
        }

        /// <summary>The racer of <paramref name="slot"/> left the race (its kart is not spawned when it left before).</summary>
        public bool HasLeft(int slot) => leftSlots.Contains(slot);
    }
}
