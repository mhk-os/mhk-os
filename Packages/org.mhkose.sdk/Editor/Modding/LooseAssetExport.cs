using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding
{
    /// <summary>
    /// Writes the loose files of a desktop build: each replaceable asset's source file to &lt;folder&gt;/&lt;virtual path&gt;.
    /// A texture file whose pixels differ from the imported texture is written re-encoded as the build imports it.
    /// </summary>
    public static class LooseAssetExport
    {
        const int JpgQuality = 95;
        const byte Opaque = 255;

        /// <summary>
        /// Writes <paramref name="items"/> into <paramref name="assetsFolder"/> (cleared first with <paramref name="clear"/>)
        /// as imported for <paramref name="target"/>. Returns the number of bytes written.
        /// </summary>
        public static long Export(IReadOnlyList<PackAssetPaths.Item> items, BuildTarget target, string assetsFolder, bool clear = true)
        {
            if (clear && Directory.Exists(assetsFolder)) Directory.Delete(assetsFolder, true);
            var platform = BuildPipeline.GetBuildTargetGroup(target).ToString();
            long bytes = 0;
            var reencoded = 0;
            using var progress = new EditorProgressScope("Loose assets", false);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (i % 50 == 0) progress.Step(i, items.Count, item.path);
                var file = Path.Combine(assetsFolder, item.path);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var data = AssetImporter.GetAtPath(item.assetPath) is TextureImporter t ? Reencoded(t, item.assetPath, platform) : null;
                if (data != null)
                {
                    File.WriteAllBytes(file, data);
                    reencoded++;
                }
                else File.Copy(item.assetPath, file, true);
                bytes += new FileInfo(file).Length;
            }
            Debug.Log($"[Assets] {items.Count} loose files ({reencoded} re-encoded), {bytes / ModBundleBuilder.BytesPerMB:0.0} MB, into {Path.GetFullPath(assetsFolder)}");
            return bytes;
        }

        // The runtime loads the file as it is into the texture, so the file must hold the imported pixels. Sizes clamped by
        // maxTextureSize are resampled (area average, keeping the aspect). Alpha the importer ignores is dropped, alpha
        // from gray is baked. Linear vs sRGB, mipmaps, filtering and wrapping stay on the bundled placeholder texture,
        // NPOT scaling is left out (sampling by UV looks the same). Null: the source file is right as it is.
        static byte[] Reencoded(TextureImporter t, string assetPath, string platform)
        {
            t.GetSourceTextureWidthAndHeight(out var width, out var height);
            var settings = t.GetPlatformTextureSettings(platform);
            var max = settings.overridden ? settings.maxTextureSize : t.maxTextureSize;
            var clamp = Math.Max(width, height) > max;
            var gray = t.alphaSource == TextureImporterAlphaSource.FromGrayScale;
            var dropAlpha = t.alphaSource == TextureImporterAlphaSource.None && t.DoesSourceTextureHaveAlpha();
            if (!clamp && !gray && !dropAlpha) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(assetPath))) throw new IOException($"Cannot decode {assetPath}.");
                var pixels = tex.GetPixels32();
                if (dropAlpha && !gray && !clamp && IsOpaque(pixels)) return null;
                if (gray) AlphaFromGray(pixels);
                if (clamp)
                {
                    var scale = (double)max / Math.Max(width, height);
                    var w = Math.Max(1, (int)Math.Round(width * scale));
                    var h = Math.Max(1, (int)Math.Round(height * scale));
                    pixels = Downscale(pixels, width, height, w, h);
                    width = w;
                    height = h;
                }
                return Encode(pixels, width, height, !dropAlpha, assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        static bool IsOpaque(Color32[] pixels)
        {
            foreach (var p in pixels)
                if (p.a != Opaque) return false;
            return true;
        }

        static void AlphaFromGray(Color32[] pixels)
        {
            for (var i = 0; i < pixels.Length; i++) pixels[i].a = (byte)((pixels[i].r + pixels[i].g + pixels[i].b) / 3);
        }

        static Color32[] Downscale(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (var y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (var x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    long r = 0, g = 0, b = 0, a = 0;
                    for (var sy = y0; sy < y1; sy++)
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = src[sy * sw + sx];
                        r += p.r;
                        g += p.g;
                        b += p.b;
                        a += p.a;
                    }
                    var n = (x1 - x0) * (y1 - y0);
                    dst[y * dw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }
            return dst;
        }

        static byte[] Encode(Color32[] pixels, int width, int height, bool alpha, string assetPath)
        {
            var tex = new Texture2D(width, height, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                var jpg = assetPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
                return jpg ? tex.EncodeToJPG(JpgQuality) : tex.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
