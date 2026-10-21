using System;
using System.Collections.Generic;
using System.IO;
using MHK.Race;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Core
{
    /// <summary>
    /// Player-build benchmark (<c>-benchmark &lt;seconds&gt;</c>, optional <c>-race &lt;trackId&gt;</c>): measures frame
    /// times after a warm-up, appends one JSON line to benchmark.jsonl and quits. GPU times need Frame Timing Stats.
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        public const string MenuLabel = "menu";
        const string ResultFile = "benchmark.jsonl";
        const float WarmupSeconds = 3f;
        const int AiSeedOffset = 1000;

        /// <summary>One benchmark result (a line of benchmark.jsonl).</summary>
        [Serializable]
        public class Result
        {
            [Tooltip("Version and commit of the measured build (BuildInfo.Id).")]
            public string build;
            public string label;
            public float seconds;
            public int frames;
            public float fps;
            public float frameMsP50, frameMsP95, frameMsP99, frameMsMax;
            public float cpuMainMs, cpuRenderMs, gpuMs;
            public float processCpuPercent;
            public string resolution;
            public int targetFrameRate, vSyncCount;
            [Tooltip("Graphics settings in effect (GraphicsOptions as JSON).")]
            public string graphics;
        }

        float seconds;
        string label;
        readonly List<float> frameMs = new();
        readonly FrameTiming[] timing = new FrameTiming[1];
        double cpuMainSum, cpuRenderSum, gpuSum;
        int timingCount, gpuCount;
        float readyAt = -1f, startedAt = -1f;
        TimeSpan cpuStart;
        bool aiTookOver;

        bool IsRace => label != MenuLabel;

        // Before the first scene: measuring must go on while the window is unfocused (Run In Background is off).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void StartIfRequested()
        {
            if (!CommandLine.TryFloat("-benchmark", out var benchmarkSeconds)) return;
            Application.runInBackground = true;
            var runner = new GameObject("Benchmark").AddComponent<BenchmarkRunner>();
            runner.seconds = benchmarkSeconds;
            runner.label = CommandLine.Value("-race") ?? MenuLabel;
        }

        void Awake() => DontDestroyOnLoad(gameObject);

        void Update()
        {
            var race = RaceManager.Current;
            if (IsRace && race != null && !aiTookOver && race.Karts.Count > 0) GiveHumansToAi(race);
            var now = Time.realtimeSinceStartup;
            if (startedAt < 0f)
            {
                TryStart(race, now);
                return;
            }
            SampleFrame();
            if (now - startedAt >= seconds) Finish(now - startedAt);
        }

        void GiveHumansToAi(RaceManager race)
        {
            foreach (var kart in race.Karts)
                if (kart.IsHuman) race.HandToAi(kart, kart.RacerIndex + AiSeedOffset);
            aiTookOver = true;
        }

        void TryStart(RaceManager race, float now)
        {
            // The menu's background race makes its track the active scene: the menu counts once the background settled.
            var ready = IsRace
                ? race != null && race.Phase == RacePhase.Racing
                : SceneManager.GetSceneByName(SceneFlow.MainMenu).isLoaded && Attract.MenuAttract.Settled;
            if (!ready) return;
            if (readyAt < 0f) readyAt = now;
            if (now - readyAt < WarmupSeconds) return;
            startedAt = now;
            cpuStart = ProcessCpuTime();
        }

        void SampleFrame()
        {
            frameMs.Add(Time.unscaledDeltaTime * 1000f);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timing) == 0) return;
            cpuMainSum += timing[0].cpuMainThreadFrameTime;
            cpuRenderSum += timing[0].cpuRenderThreadFrameTime;
            timingCount++;
            if (timing[0].gpuFrameTime <= 0) return;
            gpuSum += timing[0].gpuFrameTime;
            gpuCount++;
        }

        void Finish(float elapsed)
        {
            var json = JsonUtility.ToJson(BuildResult(elapsed, ProcessCpuTime() - cpuStart));
            Debug.Log("[Benchmark] " + json);
            AppendResult(json);
            enabled = false;
            Application.Quit();
        }

        Result BuildResult(float elapsed, TimeSpan cpu)
        {
            frameMs.Sort();
            return new Result
            {
                build = BuildInfo.Id,
                label = label,
                seconds = elapsed,
                frames = frameMs.Count,
                fps = frameMs.Count / elapsed,
                frameMsP50 = Percentile(0.5f),
                frameMsP95 = Percentile(0.95f),
                frameMsP99 = Percentile(0.99f),
                frameMsMax = frameMs.Count == 0 ? 0f : frameMs[^1],
                cpuMainMs = timingCount > 0 ? (float)(cpuMainSum / timingCount) : -1f,
                cpuRenderMs = timingCount > 0 ? (float)(cpuRenderSum / timingCount) : -1f,
                gpuMs = gpuCount > 0 ? (float)(gpuSum / gpuCount) : -1f,
                processCpuPercent = cpu > TimeSpan.Zero ? (float)(cpu.TotalSeconds / elapsed * 100.0) : -1f,
                resolution = $"{Screen.width}x{Screen.height}",
                targetFrameRate = Application.targetFrameRate,
                vSyncCount = QualitySettings.vSyncCount,
                graphics = JsonUtility.ToJson(GameRoot.Instance.Settings.graphics),
            };
        }

        float Percentile(float p) => frameMs.Count == 0 ? 0f : frameMs[Mathf.Clamp((int)(frameMs.Count * p), 0, frameMs.Count - 1)];

        static void AppendResult(string json)
        {
            try
            {
                File.AppendAllText(DataPaths.File(ResultFile), json + "\n");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Benchmark] Could not write {ResultFile}: {e.Message}");
            }
        }

        static TimeSpan ProcessCpuTime()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }
    }
}
