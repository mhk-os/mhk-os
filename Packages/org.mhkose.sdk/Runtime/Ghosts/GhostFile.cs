using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using MHK.Core;

namespace MHK.Ghosts
{
    /// <summary>
    /// Reads and writes the GZip-compressed <c>.mhkghost</c> format: magic, u16 format version, then a length-prefixed
    /// header block (<see cref="GhostHeaderCodec"/>) and body block (<see cref="GhostBodyCodec"/>). Only
    /// <see cref="FormatVersion"/> is read: files of other versions throw <see cref="GhostFormatException"/>.
    /// </summary>
    public static class GhostFile
    {
        public const string Extension = ".mhkghost";
        public const ushort FormatVersion = 2;
        static readonly byte[] Magic = { (byte)'M', (byte)'H', (byte)'K', (byte)'G' };

        /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> atomically (a complete temporary file replaces it).</summary>
        public static void Save(string path, GhostData data) => SafeFile.Write(path, s => Write(s, data));

        public static GhostData Load(string path)
        {
            using var f = File.OpenRead(path);
            return Read(f, false);
        }

        /// <summary>Reads only the header (inflates the start of the file).</summary>
        public static GhostHeader LoadHeader(string path)
        {
            using var f = File.OpenRead(path);
            return Read(f, true).header;
        }

        public static void Write(Stream stream, GhostData data)
        {
            data.header.frameCount = data.frames.Length;
            using var gz = new GZipStream(stream, CompressionLevel.Optimal, true);
            using var w = new BinaryWriter(gz, Encoding.UTF8, true);
            w.Write(Magic);
            w.Write(FormatVersion);
            WriteBlock(w, b => GhostHeaderCodec.Write(b, data.header));
            WriteBlock(w, b => GhostBodyCodec.Write(b, data));
        }

        /// <summary>Reads a ghost. <paramref name="headerOnly"/> stops after the header (frames and events stay empty).</summary>
        public static GhostData Read(Stream stream, bool headerOnly)
        {
            using var gz = new GZipStream(stream, CompressionMode.Decompress, true);
            using var r = new BinaryReader(gz, Encoding.UTF8, true);
            ReadVersion(r);
            var data = new GhostData();
            using (var h = ReadBlock(r)) data.header = GhostHeaderCodec.Read(h);
            if (headerOnly) return data;
            using (var b = ReadBlock(r)) GhostBodyCodec.Read(b, data);
            return data;
        }

        static void ReadVersion(BinaryReader r)
        {
            var magic = r.ReadBytes(Magic.Length);
            for (var i = 0; i < Magic.Length; i++)
                if (magic.Length != Magic.Length || magic[i] != Magic[i]) throw new InvalidDataException("not a ghost file");
            var version = r.ReadUInt16();
            if (version != FormatVersion) throw new GhostFormatException(version);
        }

        static void WriteBlock(BinaryWriter w, Action<BinaryWriter> write)
        {
            using var m = new MemoryStream();
            using (var b = new BinaryWriter(m, Encoding.UTF8, true)) write(b);
            w.Write((int)m.Length);
            w.Write(m.GetBuffer(), 0, (int)m.Length);
        }

        static BinaryReader ReadBlock(BinaryReader r)
        {
            var length = r.ReadInt32();
            if (length < 0) throw new InvalidDataException("bad block length");
            var bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException("ghost file is truncated");
            return new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        }
    }

    /// <summary>A ghost file of another format version than <see cref="GhostFile.FormatVersion"/>: unreadable by this game.</summary>
    public sealed class GhostFormatException : IOException
    {
        /// <summary>The file's format version.</summary>
        public int Version { get; }

        public GhostFormatException(int version) : base($"ghost format {version}, this game reads {GhostFile.FormatVersion}")
        {
            Version = version;
        }
    }
}
