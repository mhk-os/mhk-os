using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Input;
using MHK.Karts;
using MHK.Modding;
using UnityEngine;

namespace MHK.Race
{
    public partial class RaceManager
    {
        // One kart per frame: a first race loads their models, so there is no single long frame.
        IEnumerator SpawnKarts()
        {
            var cfg = GameRoot.Instance.Config;
            var order = StartOrder(cfg);
            var count = order.Count;
            var spawned = new List<Kart>(count);
            Kart player = null;
            for (var i = 0; i < count; i++)
            {
                if (i > 0) yield return null;
                var kart = SpawnKart(i, order[i], cfg);
                if (order[i].isHuman && player == null) player = kart;
                spawned.Add(kart);
                LoadProgress = TrackLoadShare + (1f - TrackLoadShare) * (i + 1) / count;
            }
            karts.AddRange(spawned);
            LocalPlayer = player != null ? player : karts.Count > 0 ? karts[0] : null;
            if (Session.IsReplay) AttachReplayViewer();
            FocusCameraOnPlayer();
            UpdateRanking();
        }

        // At most maxRacers karts: every human races, computer drivers fill the remaining slots. Humans start at the
        // back of the grid like in the original games.
        List<RacerSetup> StartOrder(GameConfig cfg)
        {
            var humans = Session.racers.Where(r => r.isHuman).Take(cfg.maxRacers).ToList();
            return Session.racers.Where(r => !r.isHuman).Take(cfg.maxRacers - humans.Count).Concat(humans).ToList();
        }

        Kart SpawnKart(int slot, RacerSetup setup, GameConfig cfg)
        {
            var content = GameRoot.Instance.Content;
            var character = content.Get<CharacterDefinition>(setup.characterId);
            var kartDef = content.ResolveKart(setup.kartId, character);
            var profile = kartDef != null && kartDef.physics != null ? kartDef.physics : cfg.defaultPhysics;
            var pose = TrackRuntime.GetGridPose(slot);
            var kart = Instantiate(cfg.kartRootPrefab, pose.position, pose.rotation, kartsRoot).GetComponent<Kart>();
            kart.Initialize(slot, setup, character, kartDef, profile);
            kart.Physics.ClassFactor = Difficulty.engineClassFactor;
            if (TrackRuntime.FindGridGround(pose.position, out var ground))
                kart.Physics.PlaceOnGround(ground.point, ground.normal, pose.forward);
            else kart.Physics.Teleport(pose.position, pose.rotation);
            kart.Progress.Begin(TrackRuntime, Laps);
            kart.Progress.FinishedRace += OnRacerFinished;
            kart.ControlsEnabled = false;
            kart.Driver = setup.isHuman ? CreatePlayerDriver(Mathf.Max(0, setup.playerIndex)) : CreateAiDriver(slot);
            return kart;
        }

        PlayerKartDriver CreatePlayerDriver(int playerIndex)
        {
            var driver = new PlayerKartDriver(controls, playerIndex);
            humanDrivers.Add(driver);
            return driver;
        }

        // No kart: the viewer still pauses and cycles the camera with the player's keys.
        void AttachReplayViewer()
        {
            var viewer = CreatePlayerDriver(0);
            if (chaseCamera != null) chaseCamera.Viewer = viewer;
        }

        void FocusCameraOnPlayer()
        {
            if (chaseCamera == null) return;
            chaseCamera.Target = LocalPlayer;
            chaseCamera.SnapToTarget();
        }
    }
}
