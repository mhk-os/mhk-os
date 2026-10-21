using System;
using System.IO;

namespace MHK.Ghosts
{
    /// <summary>
    /// Channel primitives of the ghost format: values quantised to integers, stored as zig-zag varints of their first or
    /// second differences, so smooth motion becomes runs of tiny numbers that GZip packs well.
    /// </summary>
    static class GhostChannelCodec
    {
        public static long Quantise(float value, float scale) => (long)Math.Round((double)value * scale);

        public static void WriteScalar(BinaryWriter w, GhostFrame[] f, long[] q, Func<GhostFrame, float> get, float scale)
        {
            for (var i = 0; i < f.Length; i++) q[i] = Quantise(get(f[i]), scale);
            WriteChannel(w, q, 1);
        }

        /// <summary>Decodes with double division (positions and velocities use float division: keep both as they are).</summary>
        public static void ReadScalar(BinaryReader r, long[] q, int n, Action<int, float> set, float scale)
        {
            ReadChannel(r, q, 1);
            for (var i = 0; i < n; i++) set(i, (float)(q[i] / (double)scale));
        }

        public static void WriteInts(BinaryWriter w, GhostFrame[] f, long[] q, Func<GhostFrame, long> get)
        {
            for (var i = 0; i < f.Length; i++) q[i] = get(f[i]);
            WriteChannel(w, q, 1);
        }

        public static void ReadInts(BinaryReader r, long[] q, int n, Action<int, long> set)
        {
            ReadChannel(r, q, 1);
            for (var i = 0; i < n; i++) set(i, q[i]);
        }

        public static void WriteChannel(BinaryWriter w, long[] q, int order)
        {
            for (var i = 0; i < q.Length; i++)
            {
                var d = q[i];
                if (i >= 1) d -= q[i - 1];
                if (order == 2 && i >= 2) d -= q[i - 1] - q[i - 2];
                WriteVarint(w, (ulong)((d << 1) ^ (d >> 63)));
            }
        }

        public static void ReadChannel(BinaryReader r, long[] q, int order)
        {
            for (var i = 0; i < q.Length; i++)
            {
                var z = ReadVarint(r);
                var d = (long)(z >> 1) ^ -(long)(z & 1);
                if (i >= 1) d += q[i - 1];
                if (order == 2 && i >= 2) d += q[i - 1] - q[i - 2];
                q[i] = d;
            }
        }

        public static void WriteVarint(BinaryWriter w, ulong v)
        {
            while (v >= 0x80)
            {
                w.Write((byte)(v | 0x80));
                v >>= 7;
            }
            w.Write((byte)v);
        }

        public static ulong ReadVarint(BinaryReader r)
        {
            ulong v = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = r.ReadByte();
                v |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return v;
            }
            throw new InvalidDataException("bad varint");
        }
    }
}
