using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MHK.Attract;
using MHK.Core;
using MHK.Modding;
using MHK.Race;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace MHK.Tests.Performance
{
    /// <summary>
    /// Plays the game as each <see cref="DeviceProfile"/> does, with its recommended graphics, and checks that it keeps
    /// its frame rate: races on demanding tracks in their heaviest weather and time of day, and the main menu while its
    /// background race loads and shows. Every scenario logs its <see cref="FrameStats"/> as a "[Perf] {json}" line.
    /// <para>
    /// The render budgets (SetPass calls, triangles, particles) count work, not time, so they hold on every machine: a
    /// desktop run catches what would cost a low-end device its frame rate. The frame-rate and hitch checks measure the
    /// machine the tests run on, so they are the device's verdict only on the device itself (elsewhere they catch gross
    /// regressions). A desktop run has no Mobile Quality levels and stands in the PC level of the same tier.
    /// </para>
    /// <para>
    /// The tests need a window (batch mode renders nothing and skips them). Editor: Test Runner, PlayMode, category
    /// Performance, or <c>Unity -projectPath . -runTests -testPlatform PlayMode -testCategory Performance -testResults perf.xml</c>
    /// (without <c>-batchmode</c>; the editor quits when done). On a phone connected over USB (developer mode):
    /// <c>Unity -projectPath . -runTests -testPlatform Android -buildTarget Android -testCategory Performance
    /// -testResults perf.xml -mhkExtraMods &lt;built mods&gt;</c> builds a development player with the mods, runs the tests
    /// there and reports back. Tracks that are not installed are skipped.
    /// </para>
    /// </summary>
    [Category("Performance")]
    public class PerformanceTests
    {
        const float WarmupSeconds = 3f, SampleSeconds = 10f, LoadTimeoutSeconds = 180f, MenuTailSeconds = 3f;
        // The frame rate held over the sample (a capped run measures slightly below its cap; a stray first-use stall of a
        // few hundred ms must not fail it alone) and the slow frames allowed.
        const float MinFpsShare = 0.9f, MaxP95FrameShare = 1.5f;
        const int RaceLaps = 99;
        const int TimeoutMs = 300_000;
        static readonly string[] HeavyOptions = { "core.rain", "core.night" };

        /// <summary>Rain on water (Island), night on water (Pirate Island), a dense city (Barcelona), a detailed indoor level (Production).</summary>
        static readonly string[] Tracks = { "mhk2.island", "mhk3.pirate_island", "mhkt.barcelona", "tincan.production" };

        public static IEnumerable<DeviceProfile> Profiles => DeviceProfile.All;
        public static IEnumerable<string> RaceTracks => Tracks;

        int qualityLevel;
        AttractMode? forcedAttract;

        // The races use items and count playtime: settings, saves and achievements go to a copy of the player's files.
        [OneTimeSetUp]
        public void UseProfileCopy()
        {
            var copy = Path.Combine(Application.temporaryCachePath, "PerformanceTestProfile");
            if (Directory.Exists(copy)) Directory.Delete(copy, true);
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(DataPaths.Root, "*.json"))
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            JsonStore.Folder = copy;
        }

        [SetUp]
        public void SetUp()
        {
            if (Application.isBatchMode) Assert.Ignore("Batch mode renders nothing: run the performance tests with a window.");
            qualityLevel = QualitySettings.GetQualityLevel();
            forcedAttract = MenuAttract.Forced;
        }

        // Back to a quiet menu (no background race) with the player's own graphics.
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isBatchMode) yield break;
            MenuAttract.Forced = AttractMode.Off;
            yield return WaitIdle();
            SceneFlow.GoToMenu();
            yield return WaitIdle();
            MenuAttract.Forced = forcedAttract;
            QualitySettings.SetQualityLevel(qualityLevel, true);
            GraphicsQuality.Apply(GameRoot.Instance.Settings.graphics);
        }

        [UnityTest, Timeout(TimeoutMs)]
        public IEnumerator RaceHoldsFrameRate([ValueSource(nameof(Profiles))] DeviceProfile device, [ValueSource(nameof(RaceTracks))] string trackId)
        {
            var root = GameRoot.Instance;
            yield return WaitIdle();
            var track = root.Content.Get<TrackDefinition>(trackId);
            if (track == null) Assert.Ignore($"{trackId} is not installed.");
            var graphics = Use(device);
            var session = GameSession.CreateQuickRace(root.Content, trackId, device.karts - 1);
            session.laps = RaceLaps;
            session.lapsFromTrack = false;
            var variant = HeaviestVariant(track);
            session.variants.Add(variant);
            SceneFlow.StartSession(session);
            RaceManager race = null;
            yield return Until(() => (race = RaceManager.Current) != null && (race.Phase == RacePhase.Racing || race.LoadFailed), "the race to start");
            Assert.IsFalse(race.LoadFailed, $"{trackId} [{variant}] did not load.");
            foreach (var kart in race.Karts)
                if (kart.IsHuman) race.HandToAi(kart, kart.RacerIndex + 1000);
            yield return new WaitForSecondsRealtime(WarmupSeconds);

            var sampler = new FrameSampler();
            sampler.Begin();
            for (var end = Time.realtimeSinceStartup + SampleSeconds; Time.realtimeSinceStartup < end;)
            {
                yield return null;
                sampler.Sample();
            }
            var stats = sampler.End($"race {trackId} [{variant}]", device, graphics);
            Report(stats);
            AssertBudget(stats, device);
            AssertFrameRate(stats, device);
        }

        /// <summary>
        /// The title stays smooth while the background race loads (the load window, from the title showing until the race
        /// is built) and while it warms up and fades in (the show window, until it ran a little). The editor rebuilds its
        /// Hierarchy for every object a loading scene adds, so there the load window is reported, not checked.
        /// </summary>
        [UnityTest, Timeout(TimeoutMs)]
        public IEnumerator MenuLoadsBackgroundWithoutHitches([ValueSource(nameof(Profiles))] DeviceProfile device)
        {
            yield return WaitIdle();
            var graphics = Use(device);
            MenuAttract.Forced = device.attract;
            SceneFlow.GoToMenu();
            yield return WaitIdle();

            var sampler = new FrameSampler();
            sampler.Begin();
            var giveUp = Time.realtimeSinceStartup + LoadTimeoutSeconds;
            while (RaceManager.Current == null || RaceManager.Current.Phase == RacePhase.Loading)
            {
                Assert.Less(Time.realtimeSinceStartup, giveUp, "the menu background race did not load.");
                yield return null;
                sampler.Sample();
            }
            var load = sampler.End($"menu {device.attract} load", device, graphics);
            Report(load);

            sampler.Begin();
            while (!MenuAttract.Settled)
            {
                Assert.Less(Time.realtimeSinceStartup, giveUp, "the menu background did not show.");
                yield return null;
                sampler.Sample();
            }
            for (var end = Time.realtimeSinceStartup + MenuTailSeconds; Time.realtimeSinceStartup < end;)
            {
                yield return null;
                sampler.Sample();
            }
            var show = sampler.End($"menu {device.attract} show", device, graphics);
            Report(show);
            var budget = device.budget.menuHitchMs;
            Assert.LessOrEqual(show.frameMsMax, budget, $"{device}: a {show.frameMsMax:0} ms frame while the menu background showed (budget {budget:0} ms).");
            if (!Application.isEditor)
                Assert.LessOrEqual(load.frameMsMax, budget, $"{device}: a {load.frameMsMax:0} ms frame while the menu background loaded (budget {budget:0} ms).");
        }

        // The device's Quality level (Mobile or PC family) and graphics options, for this run only. A platform without the
        // device's family (the desktop editor has no Mobile levels) renders with its own level of the same tier.
        static GraphicsOptions Use(DeviceProfile device)
        {
            var graphics = device.Graphics();
            var names = QualitySettings.names;
            var level = Array.IndexOf(names, (device.mobile ? "Mobile " : "PC ") + device.preset);
            if (level < 0) level = Array.IndexOf(names, (device.mobile ? "PC " : "Mobile ") + device.preset);
            Assert.GreaterOrEqual(level, 0, $"No Quality level of the {device.preset} tier.");
            QualitySettings.SetQualityLevel(level, true);
            GraphicsQuality.Apply(graphics);
            return graphics;
        }

        // Rain and night where the track offers them: the most lights, particles and shader work it can show.
        static string HeaviestVariant(TrackDefinition track)
        {
            var set = TrackVariants.Of(track);
            if (set == null) return "";
            var variant = set.Default;
            for (var m = 0; m < set.Modifiers.Count; m++)
                foreach (var option in HeavyOptions)
                {
                    var index = set.Modifiers[m].IndexOf(option);
                    if (index >= 0) variant = set.With(variant, m, index);
                }
            return variant.Key;
        }

        static void Report(FrameStats stats) => Debug.Log("[Perf] " + JsonUtility.ToJson(stats));

        static void AssertBudget(FrameStats stats, DeviceProfile device)
        {
            var b = device.budget;
            var over = new List<string>();
            if (stats.setPassCalls > b.setPassCalls) over.Add($"{stats.setPassCalls} SetPass calls (budget {b.setPassCalls})");
            if (stats.triangles > b.triangles) over.Add($"{stats.triangles} triangles (budget {b.triangles})");
            if (stats.particles > b.particles) over.Add($"{stats.particles} particles (budget {b.particles})");
            Assert.IsEmpty(over, $"{device} {stats.scenario}: " + string.Join(", ", over));
        }

        static void AssertFrameRate(FrameStats stats, DeviceProfile device)
        {
            Assert.GreaterOrEqual(stats.fps, device.fps * MinFpsShare, $"{device} {stats.scenario}: {stats.fps:0.0} fps, target {device.fps}.");
            Assert.LessOrEqual(stats.frameMsP95, device.FrameMs * MaxP95FrameShare,
                $"{device} {stats.scenario}: 95th percentile frame {stats.frameMsP95:0.0} ms, target {device.FrameMs:0.0} ms.");
        }

        static IEnumerator WaitIdle()
        {
            yield return Until(() => GameRoot.Instance != null && !SceneFlow.Busy, "the scene change to finish");
        }

        static IEnumerator Until(Func<bool> done, string what)
        {
            var giveUp = Time.realtimeSinceStartup + LoadTimeoutSeconds;
            while (!done())
            {
                if (Time.realtimeSinceStartup > giveUp) Assert.Fail($"Timed out waiting for {what}.");
                yield return null;
            }
        }
    }
}
