using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Editor.Content;
using MHK.Rendering;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Editor.Modding.Import
{
    /// <summary>How imported materials are lit (MHK/Lit settings that the glTF material cannot express).</summary>
    public enum GlbLightingStyle
    {
        /// <summary>Static world geometry: fully luminous LightWave surfaces and glowing additive effects stay self-lit.</summary>
        World,
        /// <summary>Dynamic objects (karts, characters, items): always lit.</summary>
        Object,
        /// <summary>Sky domes: self-lit, no culling, no depth write, drawn before the world, no shadows.</summary>
        Sky,
    }

    /// <summary>
    /// Builds MHK/Lit material assets from glTF materials (alpha mode, double-sided, vertex tint, lighting style and the
    /// LightWave extras luminosity, additive, uvScroll) and extracts their textures. A looping extras.animatedTexture
    /// (PNG frames relative to the export root) becomes a Texture2DArray flipbook sheet played by MHK/Lit.
    /// </summary>
    sealed class GlbMaterials
    {
        const int MaxTextureSize = 2048;
        const int AndroidMaxTextureSize = 1024;
        const int FlipbookMaxSize = 2048;
        const byte OpaqueAlpha = 250;
        const float FullLuminosity = 0.99f;
        const float GlowMinLuminosity = 0.5f;
        const float NoDiffuse = 0.01f;

        readonly GlbReader doc;
        readonly GlbImportOptions o;
        readonly string prefix;
        readonly Shader shader;
        readonly Dictionary<string, Material> cache = new();
        readonly Dictionary<int, Texture2D> textures = new();
        readonly Dictionary<string, Texture2DArray> flipbooks = new();
        readonly string fileName;

        /// <summary>First material created per glTF material index (-1: primitives without material).</summary>
        public readonly Dictionary<int, Material> byIndex = new();

        public GlbMaterials(GlbReader doc, GlbImportOptions o, string prefix)
        {
            this.doc = doc;
            this.o = o;
            this.prefix = prefix;
            fileName = Path.GetFileName(doc.path);
            shader = Shader.Find(MHKLitIds.ShaderName);
            if (shader == null) throw new IOException($"GlbImport: shader {MHKLitIds.ShaderName} not found");
            AssetPaths.EnsureFolder(o.materialFolder);
            AssetPaths.EnsureFolder(o.textureFolder);
            if (doc.json["extensionsUsed"] is JArray used && used.Any(e => (string)e == "KHR_texture_transform"))
                Debug.LogWarning($"[GlbImport] {fileName}: KHR_texture_transform is not supported (texture offsets / scales are ignored)");
        }

        /// <summary>
        /// The MHK/Lit material for glTF material <paramref name="index"/> (-1: default material). <paramref name="vertexTint"/>:
        /// the primitive carries COLOR_0 (converters keep vertex colours only where they tint effect geometry).
        /// </summary>
        public Material Get(int index, bool vertexTint)
        {
            var key = $"{index}|{vertexTint}";
            if (cache.TryGetValue(key, out var done)) return done;
            var json = MaterialJson(index);
            var baseName = GlbReader.Str(json?["name"]) ?? (index < 0 ? "default" : $"{prefix}_material{index}");
            var name = AssetPaths.SafeFileName(baseName) + (vertexTint ? "_vc" : "") +
                       (o.lighting == GlbLightingStyle.World ? "" : "_" + o.lighting.ToString().ToLowerInvariant());
            var mat = new Material(shader) { name = name };
            ApplyBaseColour(mat, json, baseName);
            var extras = json?["extras"] as JObject;
            ApplyEmission(mat, extras);
            mat.SetFloat(MHKLitIds.VertexTint, vertexTint ? 1f : 0f);
            SetKeyword(mat, MHKLitIds.VertexTintKeyword, vertexTint);
            ApplyAnimation(mat, extras);
            ApplyAlphaMode(mat, json, GlbReader.B(extras?["additive"]));
            if (!string.IsNullOrEmpty(o.materialFolder)) mat = Store(mat, o.materialFolder.TrimEnd('/'), name);
            cache[key] = mat;
            if (!vertexTint) byIndex[index] = mat;
            else byIndex.TryAdd(index, mat);
            return mat;
        }

        JObject MaterialJson(int index)
        {
            if (index < 0) return null;
            if (doc.json["materials"] is not JArray mats || index >= mats.Count)
                throw new InvalidDataException($"{doc.path}: material {index} does not exist");
            return (JObject)mats[index];
        }

        // Models of one import that give one material name different content get name_2, name_3, … (else the last one
        // would overwrite the others' material).
        static Material Store(Material built, string folder, string name)
        {
            var scope = PackImportScope.Current;
            if (scope == null) return Persist(built, $"{folder}/{name}.mat");
            var key = AssetDeduplicator.MaterialKey(built);
            var path = scope.MaterialPath(folder, name, key, out var claimed);
            if (claimed != null)
            {
                Object.DestroyImmediate(built);
                return claimed;
            }
            built.name = Path.GetFileNameWithoutExtension(path);
            var mat = Persist(built, path);
            scope.Claim(path, key, mat);
            return mat;
        }

        // The asset at <path> is updated in place when it differs (keeping its GUID, dropping properties and keywords of
        // older versions). Without one, an equal material the pack already has is reused before a new asset is made.
        static Material Persist(Material built, string path)
        {
            var stored = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (stored == null)
            {
                stored = PackImportScope.Current?.FindMaterial(built);
                if (stored == null)
                {
                    AssetDatabase.CreateAsset(built, path);
                    return built;
                }
            }
            else if (AssetDeduplicator.MaterialKey(stored) != AssetDeduplicator.MaterialKey(built))
            {
                EditorUtility.CopySerialized(built, stored);
                EditorUtility.SetDirty(stored);
            }
            Object.DestroyImmediate(built);
            return stored;
        }

        void ApplyBaseColour(Material mat, JObject json, string baseName)
        {
            var pbr = json?["pbrMetallicRoughness"];
            var texInfo = pbr?["baseColorTexture"] as JObject;
            var tex = texInfo != null ? GetTexture(GlbReader.I(texInfo["index"])) : null;
            if (texInfo != null && GlbReader.I(texInfo["texCoord"], 0) != 0)
                Debug.LogWarning($"[GlbImport] {fileName}: material '{baseName}' samples TEXCOORD_{texInfo["texCoord"]}; MHK/Lit always uses UV0");
            var color = pbr?["baseColorFactor"] is JArray f && f.Count >= 3
                ? new Color((float)f[0], (float)f[1], (float)f[2], f.Count > 3 ? (float)f[3] : 1f)
                : Color.white;
            mat.SetTexture(MHKLitIds.BaseMap, tex);
            mat.SetColor(MHKLitIds.BaseColor, color);
            mat.SetTexture(MHKLitIds.MainTex, tex);
            mat.SetColor(MHKLitIds.Color, color);
        }

        // Fully luminous surfaces are self-lit (portals, lava, speed pads), additive effects with LightWave diffuse 0 (torch
        // flames) glow too. Plain diffuse-0 world surfaces relied on the original lightmaps and stay lit.
        void ApplyEmission(Material mat, JObject extras)
        {
            var lum = GlbReader.F(extras?["luminosity"], GlbReader.F(extras?["lwLuminosity"], -1f));
            var glowLw = GlbReader.B(extras?["additive"]) && GlbReader.F(extras?["lwDiffuse"], 1f) <= NoDiffuse && lum >= GlowMinLuminosity;
            var selfLit = o.lighting == GlbLightingStyle.Sky || (o.lighting == GlbLightingStyle.World && (lum >= FullLuminosity || glowLw));
            mat.SetFloat(MHKLitIds.Emission, selfLit ? 1f : 0f);
        }

        void ApplyAnimation(Material mat, JObject extras)
        {
            // extras.uvScroll is in glTF texture space (v down), Unity's uv.y = 1 - v runs the other way.
            mat.SetVector(MHKLitIds.UVScroll, extras?["uvScroll"] is JArray scroll && scroll.Count >= 2
                ? new Vector4((float)scroll[0], -(float)scroll[1], 0, 0)
                : Vector4.zero);
            var flipbook = Flipbook(extras?["animatedTexture"] as JObject, out var frameCount, out var fps);
            mat.SetTexture(MHKLitIds.FlipbookArray, flipbook);
            mat.SetVector(MHKLitIds.Flipbook, new Vector4(frameCount, fps, 0, 0));
            mat.SetFloat(MHKLitIds.FlipbookOn, flipbook != null ? 1f : 0f);
            SetKeyword(mat, MHKLitIds.FlipbookKeyword, flipbook != null);
        }

        void ApplyAlphaMode(Material mat, JObject json, bool additive)
        {
            var alphaMode = GlbReader.Str(json?["alphaMode"], "OPAQUE");
            var doubleSided = GlbReader.B(json?["doubleSided"]) || o.lighting == GlbLightingStyle.Sky;
            mat.SetFloat(MHKLitIds.Cull, doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            if (additive) SetBlend(mat, BlendMode.One, BlendMode.One, false, (int)RenderQueue.Transparent + 10);
            else if (alphaMode == "BLEND" && HasTranslucentSource(mat)) SetBlend(mat, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, false, (int)RenderQueue.Transparent);
            else SetBlend(mat, BlendMode.One, BlendMode.Zero, true, alphaMode == "MASK" ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry);
            var clip = alphaMode == "MASK";
            mat.SetFloat(MHKLitIds.AlphaClip, clip ? 1f : 0f);
            mat.SetFloat(MHKLitIds.Cutoff, GlbReader.F(json?["alphaCutoff"], 0.5f));
            SetKeyword(mat, MHKLitIds.AlphaTestKeyword, clip);
            if (o.lighting != GlbLightingStyle.Sky) return;
            // Sky domes draw first without depth so the world (which extends past the dome) always covers them.
            mat.SetFloat(MHKLitIds.ZWrite, 0f);
            mat.renderQueue = (int)RenderQueue.Geometry - 100;
        }

        static bool HasTranslucentSource(Material mat)
        {
            if (mat.GetColor(MHKLitIds.BaseColor).a < 1f || mat.GetFloat(MHKLitIds.VertexTint) > 0f || mat.GetFloat(MHKLitIds.FlipbookOn) > 0f)
                return true;
            if (mat.GetTexture(MHKLitIds.BaseMap) is not Texture2D tex) return false;
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter ti) return ti.alphaSource != TextureImporterAlphaSource.None;
            return tex.isReadable && HasTranslucentPixel(tex.GetPixels32());
        }

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword);
            else m.DisableKeyword(keyword);
        }

        static void SetBlend(Material m, BlendMode src, BlendMode dst, bool zwrite, int queue)
        {
            m.SetFloat(MHKLitIds.SrcBlend, (float)src);
            m.SetFloat(MHKLitIds.DstBlend, (float)dst);
            m.SetFloat(MHKLitIds.ZWrite, zwrite ? 1f : 0f);
            m.renderQueue = queue;
            m.SetOverrideTag("RenderType", zwrite ? "Opaque" : "Transparent");
        }

        Texture2D GetTexture(int index)
        {
            if (index < 0) return null;
            if (textures.TryGetValue(index, out var done)) return done;
            if (doc.json["textures"] is not JArray list || index >= list.Count)
                throw new InvalidDataException($"{doc.path}: texture {index} does not exist");
            var t = (JObject)list[index];
            var source = GlbReader.I(t["source"]);
            if (source < 0)
            {
                Debug.LogWarning($"[GlbImport] {fileName}: texture {index} has no PNG/JPEG source (extension images are not supported)");
                return textures[index] = null;
            }
            var (wrapU, wrapV, filter) = ReadSampler(t);
            var bytes = doc.GetImage(source, out var mime, out var imageName);
            var name = AssetPaths.SafeFileName((imageName ?? $"{prefix}_image{source}").ToLowerInvariant());
            return textures[index] = string.IsNullOrEmpty(o.textureFolder)
                ? InMemoryTexture(bytes, name, wrapU, wrapV, filter, source)
                : TextureAsset(bytes, name, mime == "image/jpeg" ? ".jpg" : ".png", wrapU, wrapV, filter);
        }

        (TextureWrapMode u, TextureWrapMode v, FilterMode filter) ReadSampler(JObject texture)
        {
            var sampler = GlbReader.I(texture["sampler"]) is var s && s >= 0 && doc.json["samplers"] is JArray samplers && s < samplers.Count
                ? (JObject)samplers[s]
                : null;
            return (Wrap(GlbReader.I(sampler?["wrapS"], GltfConstants.Repeat)),
                Wrap(GlbReader.I(sampler?["wrapT"], GltfConstants.Repeat)),
                GlbReader.I(sampler?["magFilter"], GltfConstants.Linear) == GltfConstants.Nearest ? FilterMode.Point : FilterMode.Bilinear);
        }

        Texture2D InMemoryTexture(byte[] bytes, string name, TextureWrapMode wrapU, TextureWrapMode wrapV, FilterMode filter, int source)
        {
            var mem = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name };
            if (!mem.LoadImage(bytes)) throw new InvalidDataException($"{doc.path}: image {source} could not be decoded");
            mem.wrapModeU = wrapU;
            mem.wrapModeV = wrapV;
            mem.filterMode = filter;
            return mem;
        }

        Texture2D TextureAsset(byte[] bytes, string name, string ext, TextureWrapMode wrapU, TextureWrapMode wrapV, FilterMode filter)
        {
            var path = FindOrWriteImage(o.textureFolder.TrimEnd('/'), name, ext, bytes, p =>
                AssetImporter.GetAtPath(p) is TextureImporter { textureType: TextureImporterType.Default, textureShape: TextureImporterShape.Texture2D, mipmapEnabled: true } other &&
                other.wrapModeU == wrapU && other.wrapModeV == wrapV && other.filterMode == filter, out var written);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && (written || ti.wrapModeU != wrapU || ti.wrapModeV != wrapV || ti.filterMode != filter))
            {
                ConfigureColourTexture(ti, written ? HasTranslucentPixel(bytes) : ti.alphaIsTransparency, MaxTextureSize, AndroidMaxTextureSize);
                ti.wrapModeU = wrapU;
                ti.wrapModeV = wrapV;
                ti.filterMode = filter;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // The image in <folder> under its name (or name_hash when another image has it), else an equal one elsewhere in the
        // pack that <reusable> accepts, else written to <folder>.
        static string FindOrWriteImage(string folder, string name, string ext, byte[] bytes, System.Func<string, bool> reusable, out bool written)
        {
            written = false;
            var plain = $"{folder}/{name}{ext}";
            var hashed = $"{folder}/{name}_{Hash(bytes)}{ext}";
            foreach (var candidate in new[] { plain, hashed })
            {
                var full = Path.GetFullPath(candidate);
                if (!File.Exists(full) || new FileInfo(full).Length != bytes.Length || !File.ReadAllBytes(full).SequenceEqual(bytes)) continue;
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(candidate) == null)
                    AssetDatabase.ImportAsset(candidate, ImportAssetOptions.ForceSynchronousImport);
                return candidate;
            }
            var existing = PackImportScope.Current?.FindFile(bytes, name + ext, reusable);
            if (existing != null) return existing;
            var target = File.Exists(Path.GetFullPath(plain)) ? hashed : plain;
            File.WriteAllBytes(Path.GetFullPath(target), bytes);
            AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
            written = true;
            return target;
        }

#pragma warning disable CA5351
        static string Hash(byte[] bytes)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            return System.BitConverter.ToString(md5.ComputeHash(bytes), 0, 4).Replace("-", "").ToLowerInvariant();
        }
#pragma warning restore CA5351

        static bool HasTranslucentPixel(byte[] bytes)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                return t.LoadImage(bytes) && HasTranslucentPixel(t.GetPixels32());
            }
            finally
            {
                Object.DestroyImmediate(t);
            }
        }

        static bool HasTranslucentPixel(Color32[] pixels)
        {
            foreach (var p in pixels)
                if (p.a < OpaqueAlpha) return true;
            return false;
        }

        static void ConfigureColourTexture(TextureImporter ti, bool hasAlpha, int maxSize, int androidMaxSize)
        {
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = hasAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = hasAlpha;
            ti.mipmapEnabled = true;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.maxTextureSize = maxSize;
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = androidMaxSize;
            android.format = TextureImporterFormat.ASTC_6x6;
            ti.SetPlatformTextureSettings(android);
        }

        static TextureWrapMode Wrap(int gl) => gl switch
        {
            GltfConstants.ClampToEdge => TextureWrapMode.Clamp,
            GltfConstants.MirroredRepeat => TextureWrapMode.Mirror,
            _ => TextureWrapMode.Repeat,
        };

        Texture2DArray Flipbook(JObject anim, out int frameCount, out float fps)
        {
            frameCount = 0;
            fps = 0f;
            if (anim == null || anim["exportFrames"] is not JArray list || list.Count < 2) return null;
            if (string.IsNullOrEmpty(o.exportRoot) || string.IsNullOrEmpty(o.textureFolder))
            {
                Debug.LogWarning($"[GlbImport] {fileName}: animated texture '{anim["sequence"]}' imports as its first frame (exportRoot / textureFolder not set)");
                return null;
            }
            var rate = GlbReader.F(anim["fps"]);
            if (rate <= 0f || !GlbReader.B(anim["loop"], true)) return null;
            var files = list.Select(t => Path.Combine(o.exportRoot, (string)t)).ToArray();
            var missing = files.FirstOrDefault(f => !File.Exists(f));
            if (missing != null)
            {
                Debug.LogWarning($"[GlbImport] {fileName}: animated texture frame missing: {missing}");
                return null;
            }
            var array = FlipbookArray(anim, files);
            if (array == null) return null;
            frameCount = files.Length;
            fps = rate;
            return array;
        }

        Texture2DArray FlipbookArray(JObject anim, string[] files)
        {
            var signature = string.Join(";", files.Select(f =>
            {
                var fi = new FileInfo(f);
                return $"{fi.Name}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
            }));
            if (flipbooks.TryGetValue(signature, out var array)) return array;
            var seq = AssetPaths.SafeFileName(GlbReader.Str(anim["sequence"], Path.GetFileNameWithoutExtension(files[0])).ToLowerInvariant());
            var dir = o.textureFolder.TrimEnd('/') + "/Flipbooks";
            var path = $"{dir}/{seq}_{files.Length}.png";
            array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
            if (array != null && AssetImporter.GetAtPath(path) is TextureImporter ti && ti.userData == signature) return flipbooks[signature] = array;
            // Without a sheet here, one the pack keeps elsewhere from the same frames will do.
            var existing = array == null
                ? PackImportScope.Current?.FindFile(".png", p => AssetImporter.GetAtPath(p) is TextureImporter other && other.userData == signature)
                : null;
            if (existing == null)
            {
                AssetPaths.EnsureFolder(dir);
                WriteFlipbook(files, path, signature);
                existing = path;
            }
            return flipbooks[signature] = AssetDatabase.LoadAssetAtPath<Texture2DArray>(existing);
        }

        static void WriteFlipbook(string[] files, string path, string signature)
        {
            var hasAlpha = PackSheet(files, path, out var cols, out var rows);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is not TextureImporter ti) return;
            // The sheet already fits FlipbookMaxSize: a smaller Android limit would rescale it to non-integer slices.
            ConfigureColourTexture(ti, hasAlpha, FlipbookMaxSize, FlipbookMaxSize);
            ti.wrapMode = TextureWrapMode.Repeat;
            var settings = new TextureImporterSettings();
            ti.ReadTextureSettings(settings);
            settings.textureShape = TextureImporterShape.Texture2DArray;
            settings.flipbookColumns = cols;
            settings.flipbookRows = rows;
            ti.SetTextureSettings(settings);
            ti.userData = signature;
            ti.SaveAndReimport();
        }

        static bool PackSheet(string[] files, string path, out int cols, out int rows)
        {
            var frames = new Texture2D[files.Length];
            var hasAlpha = false;
            try
            {
                for (var i = 0; i < files.Length; i++)
                {
                    frames[i] = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    frames[i].LoadImage(File.ReadAllBytes(files[i]));
                }
                int fw = frames[0].width, fh = frames[0].height;
                while (!FlipbookLayout(files.Length, fw, fh, out cols, out rows))
                {
                    fw = Mathf.Max(1, fw / 2);
                    fh = Mathf.Max(1, fh / 2);
                }
                var sheet = new Texture2D(cols * fw, rows * fh, TextureFormat.RGBA32, false);
                sheet.SetPixels32(new Color32[sheet.width * sheet.height]);
                for (var k = 0; k < frames.Length; k++)
                {
                    var px = FramePixels(frames[k], fw, fh);
                    hasAlpha = hasAlpha || HasTranslucentPixel(px);
                    sheet.SetPixels32(k % cols * fw, (rows - 1 - k / cols) * fh, fw, fh, px);
                }
                File.WriteAllBytes(Path.GetFullPath(path), sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);
            }
            finally
            {
                foreach (var f in frames)
                    if (f != null) Object.DestroyImmediate(f);
            }
            return hasAlpha;
        }

        static bool FlipbookLayout(int n, int fw, int fh, out int cols, out int rows)
        {
            cols = rows = 0;
            var best = long.MaxValue;
            for (var c = 1; c <= n; c++)
            {
                var r = (n + c - 1) / c;
                if (c * fw > FlipbookMaxSize || r * fh > FlipbookMaxSize) continue;
                var score = (long)(c * r - n) * 1_000_000 + Mathf.Abs(c * fw - r * fh);
                if (score >= best) continue;
                best = score;
                cols = c;
                rows = r;
            }
            return cols > 0;
        }

        static Color32[] FramePixels(Texture2D t, int w, int h)
        {
            if (t.width == w && t.height == h) return t.GetPixels32();
            var px = new Color32[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    px[y * w + x] = t.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
            return px;
        }
    }
}
