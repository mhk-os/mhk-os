using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Lighting
{
    /// <summary>After a bake: tiny unused colour lightmaps, single-channel shadowmasks and the memory report.</summary>
    internal static class LightmapPostProcess
    {
        const int Rgba16PerEacR = 4;
        const int BlockSize = 4;
        const int BytesPerBlock4bpp = 8;

        /// <summary>
        /// Rewrites the unused colour and directional lightmaps tiny, imports the shadowmasks single-channel when every
        /// mixed light uses channel 0 and measures memory. Baked-indirect scenes keep their colour lightmaps at the
        /// component's size cap. The probes' baked light stays (writing bakedProbes would clear their occlusion).
        /// </summary>
        public static void Run(TrackBakeReport report, Scene scene)
        {
            var settings = TrackBakeSettings.Load();
            var colorMaxSize = ColorMaxSize(scene, settings);
            var singleChannel = TrackBakePreparer.SceneLights(scene)
                .All(l => l.bakingOutput.lightmapBakeType != LightmapBakeType.Mixed || l.bakingOutput.occlusionMaskChannel <= 0);
            if (!singleChannel) report.warnings.Add("several mixed lights: shadowmasks kept 4-channel");
            if (!HasGraphicsDevice) report.warnings.Add("no graphics device (-nographics): colour lightmaps kept at full size");
            foreach (var data in LightmapSettings.lightmaps)
            {
                ShrinkUnusedLightmap(AssetDatabase.GetAssetPath(data.lightmapColor), colorMaxSize);
                ShrinkUnusedLightmap(AssetDatabase.GetAssetPath(data.lightmapDir), colorMaxSize);
                if (singleChannel) SingleChannel(data.shadowMask);
            }
            Measure(report, singleChannel);
        }

        /// <summary>
        /// MHK/Lit samples the colour and directional lightmaps only in baked-indirect scenes, but the lightmapper writes
        /// them at full atlas size. Rewrites the file at <paramref name="path"/> downscaled to <paramref name="maxSize"/>
        /// in place (same name and GUID, so the LightingData references stay valid) and imports it capped to that size.
        /// Without a graphics device (batch mode with -nographics) the GPU downsample would write black pixels, so the
        /// file is left at full size and an error is logged.
        /// </summary>
        public static void ShrinkUnusedLightmap(string path, int maxSize)
        {
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;
            if (importer.maxTextureSize != maxSize)
            {
                importer.maxTextureSize = maxSize;
                importer.SaveAndReimport();
            }
            importer.GetSourceTextureWidthAndHeight(out var width, out var height);
            if (Mathf.Max(width, height) <= maxSize) return;
            if (!HasGraphicsDevice)
            {
                Debug.LogError($"[Lighting] {path}: not shrunk, no graphics device (run without -nographics); the file stays at {width}x{height}.");
                return;
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path); // imported at maxSize already
            var factor = (float)maxSize / Mathf.Max(width, height);
            var w = Mathf.Max(1, Mathf.RoundToInt(width * factor));
            var h = Mathf.Max(1, Mathf.RoundToInt(height * factor));
            var exr = path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase);
            var small = TextureCapture.Downsample(texture, w, h, exr);
            if (exr) TextureCapture.WriteExr(small, path);
            else TextureCapture.WritePng(small, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        static bool HasGraphicsDevice => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        static int ColorMaxSize(Scene scene, TrackBakeSettings settings)
        {
            var indirect = TrackBakePreparer.FindBakedIndirect(scene);
            if (indirect == null) return settings.ColorLightmapMaxSize;
            return indirect.lightmapMaxSize > 0 ? Mathf.Min(indirect.lightmapMaxSize, settings.LightmapSize) : settings.LightmapSize;
        }

        static void Measure(TrackBakeReport report, bool singleChannel)
        {
            var sizes = new List<string>();
            foreach (var data in LightmapSettings.lightmaps)
            {
                report.lightmapCount++;
                if (data.shadowMask != null)
                {
                    report.shadowmaskBytes += StorageBytes(data.shadowMask);
                    report.shadowmaskAndroidBytes += singleChannel ? Bytes4bpp(data.shadowMask) : Rgba16PerEacR * Bytes4bpp(data.shadowMask);
                    sizes.Add($"{data.shadowMask.width}x{data.shadowMask.height}");
                }
                report.colorBytes += StorageBytes(data.lightmapColor) + StorageBytes(data.lightmapDir);
            }
            report.lightmapSizes = string.Join(" ", sizes);
        }

        static void SingleChannel(Texture2D shadowMask)
        {
            if (shadowMask == null) return;
            if (!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(shadowMask)) is TextureImporter importer)) return;
            var changed = false;
            foreach (var (platform, format) in new[] { ("Standalone", TextureImporterFormat.BC4), ("Android", TextureImporterFormat.EAC_R), ("iPhone", TextureImporterFormat.EAC_R) })
            {
                var ps = importer.GetPlatformTextureSettings(platform);
                if (ps.overridden && ps.format == format) continue;
                ps.overridden = true;
                ps.format = format;
                ps.maxTextureSize = importer.maxTextureSize;
                importer.SetPlatformTextureSettings(ps);
                changed = true;
            }
            if (changed) importer.SaveAndReimport();
        }

        static long Bytes4bpp(Texture texture)
        {
            long bytes = 0;
            for (int w = texture.width, h = texture.height; ; w = Mathf.Max(1, w / 2), h = Mathf.Max(1, h / 2))
            {
                bytes += (long)((w + BlockSize - 1) / BlockSize) * ((h + BlockSize - 1) / BlockSize) * BytesPerBlock4bpp;
                if (w == 1 && h == 1) break;
            }
            return bytes;
        }

        static long StorageBytes(Texture texture)
        {
            if (texture == null) return 0;
            long bytes = 0;
            for (var m = 0; m < texture.mipmapCount; m++)
                bytes += GraphicsFormatUtility.ComputeMipmapSize(Mathf.Max(1, texture.width >> m), Mathf.Max(1, texture.height >> m), texture.graphicsFormat);
            return bytes;
        }
    }
}
