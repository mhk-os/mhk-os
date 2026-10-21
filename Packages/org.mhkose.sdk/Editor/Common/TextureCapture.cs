using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MHK.Editor
{
    /// <summary>Editor texture captures: camera renders into readable textures, GPU downsampling, PNG and EXR files.</summary>
    public static class TextureCapture
    {
        const int DepthBits = 24;

        /// <summary>
        /// Renders <paramref name="camera"/> (sRGB, <paramref name="msaa"/> samples) into a readable
        /// <paramref name="width"/> x <paramref name="height"/> texture. With <paramref name="supersample"/> 2 the
        /// camera renders at twice the size and each 2x2 block is averaged. Destroy the result after use.
        /// </summary>
        public static Texture2D Render(Camera camera, int width, int height, TextureFormat format = TextureFormat.RGBA32,
                                       int supersample = 1, int msaa = 1)
        {
            var desc = new RenderTextureDescriptor(width * supersample, height * supersample, RenderTextureFormat.ARGB32, DepthBits)
            {
                sRGB = true,
                msaaSamples = msaa,
            };
            var target = RenderTexture.GetTemporary(desc);
            try
            {
                RenderCamera(camera, target);
                if (supersample == 1) return ReadBack(target, format, false);
                var small = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                try
                {
                    Graphics.Blit(target, small);
                    return ReadBack(small, format, false);
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(small);
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(target);
            }
        }

        /// <summary>Bilinear GPU copy of <paramref name="source"/> at <paramref name="width"/> x <paramref name="height"/> (linear, half floats when <paramref name="hdr"/>).</summary>
        public static Texture2D Downsample(Texture source, int width, int height, bool hdr)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                Graphics.Blit(source, rt);
                return ReadBack(rt, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, true);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Reads <paramref name="source"/> into a new texture of the same size.</summary>
        public static Texture2D ReadBack(RenderTexture source, TextureFormat format, bool linear)
        {
            var active = RenderTexture.active;
            var tex = new Texture2D(source.width, source.height, format, false, linear);
            try
            {
                RenderTexture.active = source;
                tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = active;
            }
        }

        /// <summary>Writes <paramref name="tex"/> as PNG to <paramref name="path"/> and destroys it.</summary>
        public static void WritePng(Texture2D tex, string path)
        {
            try { File.WriteAllBytes(path, tex.EncodeToPNG()); }
            finally { Object.DestroyImmediate(tex); }
        }

        /// <summary>Writes <paramref name="tex"/> as ZIP-compressed EXR to <paramref name="path"/> and destroys it.</summary>
        public static void WriteExr(Texture2D tex, string path)
        {
            try { File.WriteAllBytes(path, tex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP)); }
            finally { Object.DestroyImmediate(tex); }
        }

        static void RenderCamera(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
                return;
            }
            var previous = camera.targetTexture;
            camera.targetTexture = target;
            try { camera.Render(); }
            finally { camera.targetTexture = previous; }
        }
    }
}
