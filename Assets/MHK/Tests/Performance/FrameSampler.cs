using System;
using System.Collections.Generic;
using MHK.Core;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace MHK.Tests.Performance
{
    /// <summary>Frame and render statistics of a measured stretch of frames (one JSON line in the test log).</summary>
    [Serializable]
    public class FrameStats
    {
        public string scenario;
        public string device;
        public int frames;
        public float seconds;
        public float fps;
        public float frameMsP50, frameMsP95, frameMsMax;
        /// <summary>Busy time of the main thread, render thread and GPU per frame (ms), -1 when the platform does not report it.</summary>
        public float cpuMainMs = -1f, cpuRenderMs = -1f, gpuMs = -1f;
        /// <summary>Highest per-frame render counts (shadow passes included), -1 when not reported (release players).</summary>
        public int setPassCalls = -1, triangles = -1;
        /// <summary>Most particles alive at once.</summary>
        public int particles;
        public int renderHeight;
        /// <summary>Quality level rendered with (a desktop run stands in the PC level for a phone's Mobile one).</summary>
        public string qualityLevel;
        public string platform;
    }

    /// <summary>
    /// Records frame times, the profiler's render counters and the particles alive over the frames between
    /// <see cref="Begin"/> and <see cref="End"/> (call <see cref="Sample"/> once per frame). Render counters come from
    /// the editor and development players only.
    /// </summary>
    public sealed class FrameSampler : IDisposable
    {
        const int ParticleSampleInterval = 10;

        readonly List<float> frameMs = new();
        readonly List<ParticleSystem> systems = new();
        ProfilerRecorder setPass, triangles, cpuMain, cpuRender, gpu;
        double cpuMainSum, cpuRenderSum, gpuSum;
        int cpuMainCount, cpuRenderCount, gpuCount, maxSetPass = -1, maxTriangles = -1, maxParticles;
        float started;

        public void Begin()
        {
            frameMs.Clear();
            cpuMainSum = cpuRenderSum = gpuSum = 0;
            cpuMainCount = cpuRenderCount = gpuCount = maxParticles = 0;
            maxSetPass = maxTriangles = -1;
            setPass = Start("SetPass Calls Count");
            triangles = Start("Triangles Count");
            cpuMain = Start("CPU Main Thread Frame Time");
            cpuRender = Start("CPU Render Thread Frame Time");
            gpu = Start("GPU Frame Time");
            started = Time.realtimeSinceStartup;
        }

        public void Sample()
        {
            frameMs.Add(Time.unscaledDeltaTime * 1000f);
            Max(setPass, ref maxSetPass);
            Max(triangles, ref maxTriangles);
            AddMs(cpuMain, ref cpuMainSum, ref cpuMainCount);
            AddMs(cpuRender, ref cpuRenderSum, ref cpuRenderCount);
            AddMs(gpu, ref gpuSum, ref gpuCount);
            if (frameMs.Count % ParticleSampleInterval != 0) return;
            systems.Clear();
            systems.AddRange(UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsInactive.Exclude));
            var alive = 0;
            foreach (var s in systems) alive += s.particleCount;
            maxParticles = Math.Max(maxParticles, alive);
        }

        /// <summary>The statistics since <see cref="Begin"/> of <paramref name="device"/> playing with <paramref name="graphics"/>.</summary>
        public FrameStats End(string scenario, DeviceProfile device, GraphicsOptions graphics)
        {
            var seconds = Time.realtimeSinceStartup - started;
            var sorted = new List<float>(frameMs);
            sorted.Sort();
            var stats = new FrameStats
            {
                scenario = scenario,
                device = device.name,
                frames = sorted.Count,
                seconds = seconds,
                fps = seconds > 0f ? sorted.Count / seconds : 0f,
                frameMsP50 = Percentile(sorted, 0.5f),
                frameMsP95 = Percentile(sorted, 0.95f),
                frameMsMax = sorted.Count > 0 ? sorted[^1] : 0f,
                cpuMainMs = Average(cpuMainSum, cpuMainCount),
                cpuRenderMs = Average(cpuRenderSum, cpuRenderCount),
                gpuMs = Average(gpuSum, gpuCount),
                setPassCalls = maxSetPass,
                triangles = maxTriangles,
                particles = maxParticles,
                renderHeight = GraphicsQuality.RenderHeight(graphics),
                qualityLevel = QualitySettings.names[QualitySettings.GetQualityLevel()],
                platform = $"{Application.platform} {SystemInfo.deviceModel} {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})",
            };
            Dispose();
            return stats;
        }

        public void Dispose()
        {
            setPass.Dispose();
            triangles.Dispose();
            cpuMain.Dispose();
            cpuRender.Dispose();
            gpu.Dispose();
        }

        // Finds the counter by name in whatever category the platform files it under; invalid when it has none.
        static ProfilerRecorder Start(string name)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var h in handles)
            {
                var d = ProfilerRecorderHandle.GetDescription(h);
                if (d.Name == name) return ProfilerRecorder.StartNew(d.Category, name);
            }
            return default;
        }

        static void Max(ProfilerRecorder r, ref int max)
        {
            if (r.Valid && r.Count > 0) max = Math.Max(max, (int)r.LastValue);
        }

        // Frame timing counters report nanoseconds, 0 until the platform has a measurement.
        static void AddMs(ProfilerRecorder r, ref double sum, ref int count)
        {
            if (!r.Valid || r.Count == 0 || r.LastValue <= 0) return;
            sum += r.LastValue * 1e-6;
            count++;
        }

        static float Average(double sum, int count) => count > 0 ? (float)(sum / count) : -1f;

        static float Percentile(List<float> sorted, float p) =>
            sorted.Count == 0 ? 0f : sorted[Mathf.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];
    }
}
