using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>glTF 2.0 enum codes used by the reader and the material builder.</summary>
    static class GltfConstants
    {
        public const int Byte = 5120;
        public const int UnsignedByte = 5121;
        public const int Short = 5122;
        public const int UnsignedShort = 5123;
        public const int UnsignedInt = 5125;
        public const int Float = 5126;

        public const int Nearest = 9728;
        public const int Linear = 9729;
        public const int Repeat = 10497;
        public const int ClampToEdge = 33071;
        public const int MirroredRepeat = 33648;

        public const int Triangles = 4;
        public const int Mat4Components = 16;
    }

    /// <summary>
    /// Reads a glTF 2.0 asset: a binary .glb (JSON chunk + BIN chunk) or a text .gltf whose buffers and images are
    /// embedded data URIs or files next to it. Decodes accessors (byteStride, normalized integers, sparse
    /// substitution) into plain arrays. No coordinate conversion happens here (see <see cref="GlbSpace"/>).
    /// </summary>
    public sealed class GlbReader
    {
        const uint GlbMagic = 0x46546C67;   // "glTF"
        const uint ChunkJson = 0x4E4F534A;  // "JSON"
        const uint ChunkBin = 0x004E4942;   // "BIN\0"

        /// <summary>Source file path (for messages and relative URIs).</summary>
        public readonly string path;
        /// <summary>The glTF JSON.</summary>
        public readonly JObject json;

        readonly byte[] binChunk;
        readonly byte[][] buffers;

        GlbReader(string path, JObject json, byte[] binChunk)
        {
            this.path = path;
            this.json = json;
            this.binChunk = binChunk;
            buffers = new byte[(json["buffers"] as JArray)?.Count ?? 0][];
        }

        /// <summary>Opens <paramref name="filePath"/>. Throws <see cref="InvalidDataException"/> with the reason when the
        /// file is not a readable glTF 2.0 asset.</summary>
        public static GlbReader Load(string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException($"glTF file not found: {filePath}", filePath);
            var bytes = File.ReadAllBytes(filePath);
            byte[] bin = null;
            var json = bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == GlbMagic ? ParseGlb(filePath, bytes, out bin) : ParseText(filePath, bytes);
            ValidateAsset(filePath, json);
            return new GlbReader(filePath, json, bin);
        }

        static JObject ParseGlb(string filePath, byte[] bytes, out byte[] bin)
        {
            var version = BitConverter.ToUInt32(bytes, 4);
            if (version != 2) throw new InvalidDataException($"{filePath}: glb version {version} is not supported (only glTF 2.0)");
            var length = (int)Math.Min(BitConverter.ToUInt32(bytes, 8), (uint)bytes.Length);
            string text = null;
            bin = null;
            var pos = 12;
            while (pos + 8 <= length)
            {
                var chunkLength = (int)BitConverter.ToUInt32(bytes, pos);
                var chunkType = BitConverter.ToUInt32(bytes, pos + 4);
                pos += 8;
                if (chunkLength < 0 || pos + chunkLength > length)
                    throw new InvalidDataException($"{filePath}: truncated glb chunk (type 0x{chunkType:X8})");
                if (chunkType == ChunkJson && text == null) text = Encoding.UTF8.GetString(bytes, pos, chunkLength);
                else if (chunkType == ChunkBin && bin == null)
                {
                    bin = new byte[chunkLength];
                    Buffer.BlockCopy(bytes, pos, bin, 0, chunkLength);
                }
                pos += (chunkLength + 3) & ~3;
            }
            if (text == null) throw new InvalidDataException($"{filePath}: glb has no JSON chunk");
            return ParseJson(filePath, text);
        }

        static JObject ParseText(string filePath, byte[] bytes)
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (!text.StartsWith('{')) throw new InvalidDataException($"{filePath}: neither a .glb nor a .gltf JSON file");
            return ParseJson(filePath, text);
        }

        static void ValidateAsset(string filePath, JObject json)
        {
            var assetVersion = Str(json["asset"]?["version"]);
            if (assetVersion == null || !assetVersion.StartsWith('2'))
                throw new InvalidDataException($"{filePath}: glTF asset version '{assetVersion ?? "missing"}' is not supported (only 2.x)");
            if (json["extensionsRequired"] is JArray required && required.Count > 0)
                Debug.LogWarning($"[GlbImport] {Path.GetFileName(filePath)}: required extensions are not supported and are ignored: {string.Join(", ", required)}");
        }

        static JObject ParseJson(string filePath, string text)
        {
            try { return JObject.Parse(text); }
            catch (Exception e) { throw new InvalidDataException($"{filePath}: invalid glTF JSON ({e.Message})", e); }
        }

        /// <summary>Number of components of an accessor type (SCALAR = 1 … MAT4 = 16).</summary>
        public static int ComponentCount(string type) => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT2" => 4,
            "MAT3" => 9,
            "MAT4" => GltfConstants.Mat4Components,
            _ => throw new InvalidDataException($"unknown accessor type '{type}'"),
        };

        static int ComponentSize(int componentType) => componentType switch
        {
            GltfConstants.Byte or GltfConstants.UnsignedByte => 1,
            GltfConstants.Short or GltfConstants.UnsignedShort => 2,
            GltfConstants.UnsignedInt or GltfConstants.Float => 4,
            _ => throw new InvalidDataException($"unknown accessor componentType {componentType}"),
        };

        /// <summary>The accessor JSON. Throws when the index is out of range.</summary>
        public JObject Accessor(int index)
        {
            if (json["accessors"] is not JArray list || index < 0 || index >= list.Count)
                throw new InvalidDataException($"{path}: accessor {index} does not exist");
            return (JObject)list[index];
        }

        /// <summary>Element count of an accessor.</summary>
        public int Count(int accessor) => (int)Accessor(accessor)["count"];

        /// <summary>
        /// Reads an accessor as floats (count * components values, tightly packed). Normalized integer components are
        /// mapped to 0..1 or -1..1 as the glTF specification defines, other integers keep their value.
        /// </summary>
        public float[] ReadFloats(int accessor, out int components)
        {
            var a = Accessor(accessor);
            var n = components = ComponentCount(Str(a["type"], "SCALAR"));
            var componentType = (int)a["componentType"];
            var normalized = B(a["normalized"]);
            var result = new float[(int)a["count"] * n];
            var size = ComponentSize(componentType);
            Read(a, n, componentType, (data, offset, element) =>
            {
                for (var c = 0; c < n; c++) result[element * n + c] = Decode(data, offset + c * size, componentType, normalized);
            });
            return result;
        }

        /// <summary>Reads an integer accessor (indices, joints) as ints (count * components values).</summary>
        public int[] ReadInts(int accessor, out int components)
        {
            var a = Accessor(accessor);
            var n = components = ComponentCount(Str(a["type"], "SCALAR"));
            var componentType = (int)a["componentType"];
            var result = new int[(int)a["count"] * n];
            var size = ComponentSize(componentType);
            Read(a, n, componentType, (data, offset, element) =>
            {
                for (var c = 0; c < n; c++) result[element * n + c] = DecodeInt(data, offset + c * size, componentType);
            });
            return result;
        }

        void Read(JObject a, int components, int componentType, Action<byte[], int, int> write)
        {
            var count = (int)a["count"];
            var reader = new ElementReader(this, a, count, components, componentType);
            if (reader.data != null)
                for (var i = 0; i < count; i++) write(reader.data, reader.Offset(i), i);
            ApplySparse(a, components, componentType, write);
        }

        readonly struct ElementReader
        {
            public readonly byte[] data;
            readonly int start, stride;

            public ElementReader(GlbReader r, JObject a, int count, int components, int componentType)
            {
                data = null;
                start = stride = 0;
                if (a["bufferView"] == null || a["bufferView"].Type == JTokenType.Null) return; // all zeros (+ sparse)
                var componentSize = ComponentSize(componentType);
                var view = r.BufferView((int)a["bufferView"], out data, out var viewOffset, out var viewLength, out var byteStride);
                var elementSize = componentSize * components;
                var type = Str(a["type"]);
                // Matrices of 1- and 2-byte components align every column to 4 bytes.
                if (componentSize < 4 && type is "MAT2" or "MAT3")
                    throw new InvalidDataException($"{r.path}: padded {type} accessors of {componentSize}-byte components are not supported");
                stride = byteStride > 0 ? byteStride : elementSize;
                start = viewOffset + (int)(a["byteOffset"] ?? 0);
                if (count > 0 && start + (count - 1) * stride + elementSize > viewOffset + viewLength)
                    throw new InvalidDataException($"{r.path}: accessor reads past the end of buffer view {view}");
            }

            public int Offset(int element) => start + element * stride;
        }

        void ApplySparse(JObject a, int components, int componentType, Action<byte[], int, int> write)
        {
            if (a["sparse"] is not JObject sparse) return;
            var count = (int)sparse["count"];
            var idx = (JObject)sparse["indices"];
            var val = (JObject)sparse["values"];
            BufferView((int)idx["bufferView"], out var idxData, out var idxStart, out _, out _);
            BufferView((int)val["bufferView"], out var valData, out var valStart, out _, out _);
            idxStart += (int)(idx["byteOffset"] ?? 0);
            valStart += (int)(val["byteOffset"] ?? 0);
            var idxType = (int)idx["componentType"];
            var idxSize = ComponentSize(idxType);
            var elementSize = ComponentSize(componentType) * components;
            var total = (int)a["count"];
            for (var i = 0; i < count; i++)
            {
                var element = DecodeInt(idxData, idxStart + i * idxSize, idxType);
                if (element < 0 || element >= total) throw new InvalidDataException($"{path}: sparse index {element} out of range");
                write(valData, valStart + i * elementSize, element);
            }
        }

        static float Decode(byte[] d, int o, int componentType, bool normalized) => componentType switch
        {
            GltfConstants.Float => BitConverter.ToSingle(d, o),
            GltfConstants.UnsignedByte => normalized ? d[o] / 255f : d[o],
            GltfConstants.Byte => normalized ? Mathf.Max((sbyte)d[o] / 127f, -1f) : (sbyte)d[o],
            GltfConstants.UnsignedShort => normalized ? BitConverter.ToUInt16(d, o) / 65535f : BitConverter.ToUInt16(d, o),
            GltfConstants.Short => normalized ? Mathf.Max(BitConverter.ToInt16(d, o) / 32767f, -1f) : BitConverter.ToInt16(d, o),
            GltfConstants.UnsignedInt => BitConverter.ToUInt32(d, o),
            _ => throw new InvalidDataException($"unknown accessor componentType {componentType}"),
        };

        static int DecodeInt(byte[] d, int o, int componentType) => componentType switch
        {
            GltfConstants.UnsignedByte => d[o],
            GltfConstants.Byte => (sbyte)d[o],
            GltfConstants.UnsignedShort => BitConverter.ToUInt16(d, o),
            GltfConstants.Short => BitConverter.ToInt16(d, o),
            GltfConstants.UnsignedInt => (int)BitConverter.ToUInt32(d, o),
            GltfConstants.Float => (int)BitConverter.ToSingle(d, o),
            _ => throw new InvalidDataException($"unknown accessor componentType {componentType}"),
        };

        /// <summary>Resolves a buffer view to its buffer bytes, absolute offset, length and byteStride (0 = packed).</summary>
        public int BufferView(int index, out byte[] data, out int offset, out int length, out int byteStride)
        {
            if (json["bufferViews"] is not JArray views || index < 0 || index >= views.Count)
                throw new InvalidDataException($"{path}: bufferView {index} does not exist");
            var v = (JObject)views[index];
            data = GetBuffer((int)v["buffer"]);
            offset = (int)(v["byteOffset"] ?? 0);
            length = (int)v["byteLength"];
            byteStride = (int)(v["byteStride"] ?? 0);
            if (offset + length > data.Length) throw new InvalidDataException($"{path}: bufferView {index} exceeds its buffer");
            return index;
        }

        /// <summary>The bytes of buffer <paramref name="index"/> (BIN chunk, data URI or external file).</summary>
        public byte[] GetBuffer(int index)
        {
            if (index < 0 || index >= buffers.Length) throw new InvalidDataException($"{path}: buffer {index} does not exist");
            if (buffers[index] != null) return buffers[index];
            var uri = Str(json["buffers"][index]["uri"]);
            if (uri != null) return buffers[index] = ReadUri(uri, out _);
            if (binChunk == null) throw new InvalidDataException($"{path}: buffer {index} has no uri and the file has no BIN chunk");
            return buffers[index] = binChunk;
        }

        /// <summary>
        /// Encoded bytes of image <paramref name="index"/> (PNG or JPEG), its MIME type and a name: the image name, else
        /// the file name of its URI, else null.
        /// </summary>
        public byte[] GetImage(int index, out string mimeType, out string name)
        {
            if (json["images"] is not JArray images || index < 0 || index >= images.Count)
                throw new InvalidDataException($"{path}: image {index} does not exist");
            var img = (JObject)images[index];
            mimeType = Str(img["mimeType"]);
            name = Str(img["name"]);
            byte[] bytes;
            if (img["bufferView"] != null && img["bufferView"].Type != JTokenType.Null)
            {
                BufferView((int)img["bufferView"], out var data, out var offset, out var length, out _);
                bytes = new byte[length];
                Buffer.BlockCopy(data, offset, bytes, 0, length);
            }
            else
            {
                var uri = Str(img["uri"]) ?? throw new InvalidDataException($"{path}: image {index} has neither bufferView nor uri");
                bytes = ReadUri(uri, out var uriMime);
                mimeType ??= uriMime;
                if (name == null && !uri.StartsWith("data:", StringComparison.Ordinal))
                    name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri));
            }
            mimeType ??= bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : "image/png";
            return bytes;
        }

        byte[] ReadUri(string uri, out string mimeType)
        {
            mimeType = null;
            if (!uri.StartsWith("data:", StringComparison.Ordinal))
            {
                var file = Path.Combine(Path.GetDirectoryName(path) ?? "", Uri.UnescapeDataString(uri));
                if (!File.Exists(file)) throw new InvalidDataException($"{path}: referenced file not found: {file}");
                return File.ReadAllBytes(file);
            }
            var comma = uri.IndexOf(',');
            if (comma < 0) throw new InvalidDataException($"{path}: malformed data URI");
            var header = uri.Substring(5, comma - 5);
            var semi = header.IndexOf(';');
            mimeType = semi >= 0 ? header.Substring(0, semi) : header;
            if (!header.EndsWith(";base64", StringComparison.Ordinal))
                throw new InvalidDataException($"{path}: only base64 data URIs are supported");
            return Convert.FromBase64String(uri.Substring(comma + 1));
        }

        /// <summary>String value or <paramref name="fallback"/> when missing or null. Wrong types throw (strict glTF schema).</summary>
        public static string Str(JToken t, string fallback = null) => t == null || t.Type == JTokenType.Null ? fallback : (string)t;
        public static float F(JToken t, float fallback = 0f) => t == null || t.Type == JTokenType.Null ? fallback : (float)t;
        public static int I(JToken t, int fallback = -1) => t == null || t.Type == JTokenType.Null ? fallback : (int)t;
        /// <summary>Bool value (numbers: non-zero) or <paramref name="fallback"/> when missing or null.</summary>
        public static bool B(JToken t, bool fallback = false) =>
            t == null || t.Type == JTokenType.Null ? fallback : t.Type == JTokenType.Boolean ? (bool)t : (float)t != 0f;
    }

    /// <summary>
    /// glTF (right-handed) to Unity (left-handed) conversion, identical to glTFast: x is negated for positions, normals
    /// and translations, rotations become (x, -y, -z, w), matrices are mirrored (S·M·S with S = diag(-1,1,1,1)),
    /// triangle winding is reversed and texture v is flipped (uv.y = 1 - v).
    /// </summary>
    public static class GlbSpace
    {
        public static Vector3 Vec(float x, float y, float z) => new(-x, y, z);
        public static Quaternion Rot(float x, float y, float z, float w) => new(x, -y, -z, w);
        /// <summary>Position from a JSON array (null: zero).</summary>
        public static Vector3 Vec(JToken a) => a == null ? Vector3.zero : Vec((float)a[0], (float)a[1], (float)a[2]);
        /// <summary>Rotation from a JSON array (null: identity).</summary>
        public static Quaternion Rot(JToken q) => q == null ? Quaternion.identity : Rot((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        /// <summary>Scale from a JSON array (null: one).</summary>
        public static Vector3 Scale(JToken a) => a == null ? Vector3.one : new Vector3((float)a[0], (float)a[1], (float)a[2]);

        /// <summary>Column-major glTF matrix (16 floats from <paramref name="offset"/>) converted to Unity space.</summary>
        public static Matrix4x4 Matrix(IList<float> m, int offset = 0)
        {
            var r = new Matrix4x4();
            for (var col = 0; col < 4; col++)
                for (var row = 0; row < 4; row++)
                {
                    var v = m[offset + col * 4 + row];
                    r[row, col] = (row == 0) != (col == 0) ? -v : v;
                }
            return r;
        }

        /// <summary>Splits a converted TRS matrix into translation, rotation and scale (negative determinant: negative x scale).</summary>
        public static void Decompose(Matrix4x4 m, out Vector3 t, out Quaternion r, out Vector3 s)
        {
            t = m.GetColumn(3);
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            s = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
            if (Vector3.Dot(Vector3.Cross(c0, c1), c2) < 0f) s.x = -s.x;
            if (Mathf.Approximately(s.y, 0f) || Mathf.Approximately(s.z, 0f))
            {
                r = Quaternion.identity;
                return;
            }
            r = Quaternion.LookRotation(c2 / s.z, c1 / s.y);
        }
    }
}
