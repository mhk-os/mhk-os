using System;
using System.Collections;
using MHK.Cameras;
using MHK.Core;
using MHK.Ghosts;
using MHK.Karts;
using MHK.Modding;
using MHK.Online;
using UnityEngine;

namespace MHK.Race
{
    public partial class RaceManager
    {
        /// <summary>The online race this race belongs to (<see cref="GameSession.online"/>), null offline.</summary>
        public OnlineRace Online { get; private set; }
        /// <summary>An online race (also once its <see cref="Online"/> ended, e.g. on the results screen).</summary>
        public bool IsOnline => Session != null && Session.online;
        /// <summary>
        /// Testing aid (<c>-autopilot</c> on the command line, or set from the editor): in online races the computer
        /// drives the local kart instead of the player's input.
        /// </summary>
        public static bool OnlineAutopilot { get; set; } = CommandLine.Has("-autopilot");

        /// <summary>Online: the track and karts are loaded, the server's GO has not come yet ('Waiting for players').</summary>
        public bool WaitingForPlayers => Online != null && Phase == RacePhase.Intro && !Online.HasGo;

        // The solo battle's Finished went out (once).
        bool soloBattleReported;
        // Left for the lobby because the online race ended under this scene (once).
        bool abandoned;
        // The local player's driver while the online pause menu covers the race (the kart coasts without input).
        IKartDriver pausedDriver;

        /// <summary>No controls: the kart rolls on without input (online pause, the race goes on).</summary>
        sealed class IdleDriver : IKartDriver
        {
            public static readonly IdleDriver Instance = new();

            public void UpdateInput(Kart kart, ref KartInput input) => input = default;
        }

        // Online the pause menu does not stop the race: the menu's keys must not drive the local kart meanwhile.
        void HoldLocalInput(bool paused)
        {
            var kart = LocalPlayer;
            if (kart == null) return;
            if (paused && kart.Driver is Input.PlayerKartDriver)
            {
                pausedDriver = kart.Driver;
                kart.Driver = IdleDriver.Instance;
            }
            else if (!paused && pausedDriver != null)
            {
                if (kart.Driver == IdleDriver.Instance) kart.Driver = pausedDriver;
                pausedDriver = null;
            }
        }

        // An online session without its race (left meanwhile) cannot run.
        bool BindOnline()
        {
            if (Session == null || !Session.online) return true;
            Online = OnlineRace.Current;
            return Online != null;
        }

        // The remote racers' stand-ins: the character and kart they chose (a placeholder when not installed).
        Kart SpawnRemote(int gridIndex, RacerSetup setup, GameConfig cfg)
        {
            Validation.UnknownContentGuard.Resolve(setup.onlineSlot, setup.characterId, setup.kartId, out var character, out var kartDef);
            var kart = Spawn(gridIndex, setup, character, kartDef, cfg);
            kart.Driver = null;
            RemoteKart.Attach(kart, setup.onlineSlot, Online);
            Online.Register(setup.onlineSlot, kart);
            return kart;
        }

        // Online rules and HUD pieces, then the content report the server compares across racers.
        void AttachOnline()
        {
            if (Online == null) return;
            gameObject.AddComponent<UI.OnlineNotices>();
            if (!Online.Solo) gameObject.AddComponent<Validation.LiveValidation>();
            var loaded = new RaceLoaded
            {
                trackHash = OnlineTrackHash(),
                laps = IsBattle ? 0 : Mathf.Clamp(Laps, 1, RaceProtocol.MaxLaps),
                effects = OnlineRace.EffectTable(OnlineLobby.Current != null ? OnlineLobby.Current.settings.packs : null),
            };
            Powerups.NetItemContent.Fill(loaded, this);
            Powerups.OnlineItems.Begin(Online);
            Online.SendLoaded(loaded);
        }

        string OnlineTrackHash()
        {
            try
            {
                return GhostFingerprint.TrackHash(Track, TrackRuntime);
            }
            catch (Exception e)
            {
                // The server sees a hash nobody else has and takes this racer out (desync): nothing to race on.
                Debug.LogException(e);
                return "";
            }
        }

        // Instead of the intro and the local countdown: the camera orbits until the server's GO is due, the countdown
        // follows the synced server clock and GO is the first physics step at or after the GO instant (FixedUpdate).
        IEnumerator RunOnlineStart()
        {
            Phase = RacePhase.Intro;
            var cfg = GameRoot.Instance.Config;
            var countdown = cfg.countdownSeconds;
            var orbit = chaseCamera != null;
            if (orbit) chaseCamera.CurrentMode = ChaseCamera.Mode.Orbit;
            SecondsToGo = IntroSeconds + countdown;
            while (!Online.HasGo || Online.SecondsToGo > countdown)
            {
                if (Phase != RacePhase.Intro) yield break; // the result came first
                if (Online.HasGo) SecondsToGo = (float)Online.SecondsToGo;
                yield return null;
            }
            if (orbit)
            {
                chaseCamera.CurrentMode = ChaseCamera.Mode.Chase;
                chaseCamera.SnapToTarget();
            }
            Phase = RacePhase.Countdown;
            powerStart.Clear();
            var difficulty = Difficulty;
            var lastTick = int.MaxValue;
            while (Phase == RacePhase.Countdown)
            {
                var left = (float)Math.Max(0.0, Online.SecondsToGo);
                SecondsToGo = left;
                var tick = Mathf.FloorToInt(left + 1f);
                if (tick != lastTick && tick > 0 && tick <= MaxCountdownTick)
                {
                    lastTick = tick;
                    CountdownTick?.Invoke(tick);
                }
                powerStart.Sample(karts, left, cfg, difficulty);
                yield return null;
            }
        }

        // FixedUpdate: GO on the server clock.
        void StepOnlineStart()
        {
            if (Online == null || Phase != RacePhase.Countdown || !Online.HasGo || Online.SecondsToGo > 0.0) return;
            CountdownTick?.Invoke(0);
            StartRacing();
            Online.OnGo();
        }

        /// <summary>
        /// Online: leaves the race and the lobby (the pause menu's Leave race, confirmed). The server counts it as
        /// left; the player lands in the main menu.
        /// </summary>
        public void LeaveOnline()
        {
            if (!IsOnline) return;
            SetPaused(false);
            if (Online != null) Online.End();
            // Failures are values (never thrown); the server takes a racer out when its connection drops anyway.
            _ = OnlineLobby.Leave();
            SceneFlow.GoToMenu();
        }

        // Online results: on to the lobby (its next race starts from there), unless the next race is already set up.
        void ReturnToLobby()
        {
            if (SceneFlow.Busy) return;
            if (Online != null) Online.End();
            if (OnlineRace.Current != null) return;
            OnlineLobby.ReturnToLobby();
        }
    }
}
