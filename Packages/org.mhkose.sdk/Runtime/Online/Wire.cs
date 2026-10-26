using System;
using System.Text;

namespace MHK.Online
{
    /// <summary>A malformed body: truncated, over a field limit or holding an invalid value (docs/protocol.md, primitive encoding).</summary>
    public sealed class WireFormatException : Exception
    {
        public WireFormatException(string message) : base(message) { }
    }

    /// <summary>Limits shared by <see cref="WireWriter"/> and <see cref="WireReader"/>.</summary>
    public static class Wire
    {
        /// <summary>Largest varuint (lengths and counts).</summary>
        public const int MaxVarUInt = int.MaxValue;
        /// <summary>Byte limit of a string field that names none.</summary>
        public const int DefaultMaxString = 256;
        /// <summary>Encoded size of a varuint at most.</summary>
        public const int MaxVarUIntBytes = 5;

        // Strict both ways: invalid UTF-8 is a malformed body, and a lone surrogate cannot be sent.
        internal static readonly UTF8Encoding Utf8 = new(false, true);

        // Unix milliseconds DateTimeOffset accepts (years 1 to 9999).
        internal const long MinTimeMs = -62135596800000L;
        internal const long MaxTimeMs = 253402300799999L;
    }

    /// <summary>
    /// Builds a message body (or a whole frame) in the protocol's encoding: little-endian fixed-width numbers, LEB128
    /// lengths, UTF-8 strings. Field limits are checked here as well, a value over one is a bug of the caller
    /// (<see cref="ArgumentException"/>).
    /// </summary>
    public sealed class WireWriter
    {
        byte[] buffer;
        int length;

        public WireWriter(int capacity = 64) => buffer = new byte[Math.Max(16, capacity)];

        /// <summary>Bytes written so far.</summary>
        public int Length => length;

        /// <summary>The written bytes (a copy).</summary>
        public byte[] ToArray()
        {
            var bytes = new byte[length];
            Buffer.BlockCopy(buffer, 0, bytes, 0, length);
            return bytes;
        }

        /// <summary>Starts over, keeping the buffer (datagrams built many times a second).</summary>
        internal void Clear() => length = 0;

        /// <summary>The internal buffer; the first <see cref="Length"/> bytes are the written ones. Valid until the next write.</summary>
        internal byte[] RawBuffer => buffer;

        /// <summary>Overwrites 4 bytes at <paramref name="offset"/> (a frame's length prefix once the body is known).</summary>
        internal void PatchU32(int offset, uint v)
        {
            buffer[offset] = (byte)v;
            buffer[offset + 1] = (byte)(v >> 8);
            buffer[offset + 2] = (byte)(v >> 16);
            buffer[offset + 3] = (byte)(v >> 24);
        }

        void Reserve(int count)
        {
            if (length + count <= buffer.Length) return;
            var size = buffer.Length;
            while (size < length + count) size = size <= int.MaxValue / 2 ? size * 2 : int.MaxValue;
            Array.Resize(ref buffer, size);
        }

        public void U8(byte v)
        {
            Reserve(1);
            buffer[length++] = v;
        }

        public void U16(ushort v)
        {
            Reserve(2);
            buffer[length++] = (byte)v;
            buffer[length++] = (byte)(v >> 8);
        }

        public void U32(uint v)
        {
            Reserve(4);
            buffer[length++] = (byte)v;
            buffer[length++] = (byte)(v >> 8);
            buffer[length++] = (byte)(v >> 16);
            buffer[length++] = (byte)(v >> 24);
        }

        public void U64(ulong v)
        {
            U32((uint)v);
            U32((uint)(v >> 32));
        }

        public void I32(int v) => U32(unchecked((uint)v));

        public void I64(long v) => U64(unchecked((ulong)v));

        /// <summary>IEEE 754 binary32. NaN and infinities only where the field allows them (<paramref name="allowNonFinite"/>).</summary>
        public void F32(float v, bool allowNonFinite = false)
        {
            if (!allowNonFinite && (float.IsNaN(v) || float.IsInfinity(v))) throw new ArgumentException("Non-finite float.", nameof(v));
            I32(BitConverter.SingleToInt32Bits(v));
        }

        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);

        /// <summary>Unsigned LEB128 of a length or count (0 to 2^31-1), the shortest form.</summary>
        public void VarUInt(int v)
        {
            if (v < 0) throw new ArgumentOutOfRangeException(nameof(v), "Negative varuint.");
            var u = (uint)v;
            Reserve(Wire.MaxVarUIntBytes);
            while (u >= 0x80)
            {
                buffer[length++] = (byte)(u | 0x80);
                u >>= 7;
            }
            buffer[length++] = (byte)u;
        }

        /// <summary>UTF-8 string of at most <paramref name="maxBytes"/> bytes (null is written as empty).</summary>
        public void String(string v, int maxBytes = Wire.DefaultMaxString)
        {
            v ??= "";
            int count;
            try
            {
                count = Wire.Utf8.GetByteCount(v);
            }
            catch (EncoderFallbackException e)
            {
                throw new ArgumentException("String is not valid Unicode.", nameof(v), e);
            }
            if (count > maxBytes) throw new ArgumentException($"String of {count} bytes over the field limit of {maxBytes}.", nameof(v));
            VarUInt(count);
            Reserve(count);
            length += Wire.Utf8.GetBytes(v, 0, v.Length, buffer, length);
        }

        /// <summary>Raw bytes with their length, at most <paramref name="maxLength"/> (null is written as empty).</summary>
        public void Bytes(byte[] v, int maxLength) => Bytes(v ?? Array.Empty<byte>(), 0, v?.Length ?? 0, maxLength);

        public void Bytes(byte[] v, int offset, int count, int maxLength)
        {
            if (count > maxLength) throw new ArgumentException($"{count} bytes over the field limit of {maxLength}.", nameof(count));
            VarUInt(count);
            Raw(v, offset, count);
        }

        /// <summary>Item count of a list of at most <paramref name="maxCount"/> items; the items follow.</summary>
        public void ListCount(int count, int maxCount)
        {
            if (count > maxCount) throw new ArgumentException($"{count} items over the field limit of {maxCount}.", nameof(count));
            VarUInt(count);
        }

        /// <summary>Unix milliseconds UTC.</summary>
        public void Time(DateTimeOffset v) => I64(v.ToUnixTimeMilliseconds());

        /// <summary>Appends <paramref name="count"/> bytes without a length (a body built by another writer).</summary>
        public void Raw(byte[] v, int offset, int count)
        {
            if (count <= 0) return;
            Reserve(count);
            Buffer.BlockCopy(v, offset, buffer, length, count);
            length += count;
        }
    }

    /// <summary>
    /// Reads a body written in the protocol's encoding. Every problem (truncated, over a field limit, invalid value)
    /// throws <see cref="WireFormatException"/>; lengths are checked against the bytes left before anything is
    /// allocated. Bytes after the fields a reader knows are ignored (bodies are append-only).
    /// </summary>
    public sealed class WireReader
    {
        readonly byte[] data;
        readonly int end;
        int position;

        public WireReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }

        public WireReader(byte[] data, int offset, int count)
        {
            this.data = data ?? Array.Empty<byte>();
            if (offset < 0 || count < 0 || offset + count > this.data.Length) throw new ArgumentOutOfRangeException(nameof(count));
            position = offset;
            end = offset + count;
        }

        /// <summary>Bytes not read yet.</summary>
        public int Remaining => end - position;

        int Take(int count)
        {
            if (count > end - position) throw new WireFormatException("Truncated body.");
            var at = position;
            position += count;
            return at;
        }

        public byte U8() => data[Take(1)];

        public ushort U16()
        {
            var i = Take(2);
            return (ushort)(data[i] | data[i + 1] << 8);
        }

        public uint U32()
        {
            var i = Take(4);
            return (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
        }

        public ulong U64()
        {
            ulong lo = U32();
            ulong hi = U32();
            return lo | hi << 32;
        }

        public int I32() => unchecked((int)U32());

        public long I64() => unchecked((long)U64());

        public float F32(bool allowNonFinite = false)
        {
            var v = BitConverter.Int32BitsToSingle(I32());
            if (!allowNonFinite && (float.IsNaN(v) || float.IsInfinity(v))) throw new WireFormatException("Non-finite float.");
            return v;
        }

        public bool Bool()
        {
            var v = U8();
            if (v > 1) throw new WireFormatException("Invalid bool.");
            return v == 1;
        }

        public int VarUInt()
        {
            uint v = 0;
            for (var shift = 0; shift < 7 * Wire.MaxVarUIntBytes; shift += 7)
            {
                var b = U8();
                if (shift == 28 && b > 0x07) throw new WireFormatException("Varuint over 2^31-1.");
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return (int)v;
            }
            throw new WireFormatException("Varuint longer than 5 bytes.");
        }

        public string String(int maxBytes = Wire.DefaultMaxString)
        {
            var count = VarUInt();
            if (count > maxBytes) throw new WireFormatException($"String of {count} bytes over the field limit of {maxBytes}.");
            var at = Take(count);
            try
            {
                return Wire.Utf8.GetString(data, at, count);
            }
            catch (DecoderFallbackException)
            {
                throw new WireFormatException("String is not valid UTF-8.");
            }
        }

        public byte[] Bytes(int maxLength)
        {
            var count = VarUInt();
            if (count > maxLength) throw new WireFormatException($"{count} bytes over the field limit of {maxLength}.");
            var at = Take(count);
            var bytes = new byte[count];
            Buffer.BlockCopy(data, at, bytes, 0, count);
            return bytes;
        }

        /// <summary>
        /// Item count of a list of at most <paramref name="maxCount"/> items, each taking at least
        /// <paramref name="minItemBytes"/> bytes, so a count the body cannot hold fails before a list is sized by it.
        /// </summary>
        public int ListCount(int maxCount, int minItemBytes = 1)
        {
            var count = VarUInt();
            if (count > maxCount) throw new WireFormatException($"{count} items over the field limit of {maxCount}.");
            if (minItemBytes > 0 && count > Remaining / minItemBytes) throw new WireFormatException("Truncated list.");
            return count;
        }

        /// <summary>Unix milliseconds UTC.</summary>
        public DateTimeOffset Time()
        {
            var ms = I64();
            if (ms < Wire.MinTimeMs || ms > Wire.MaxTimeMs) throw new WireFormatException("Time out of range.");
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
    }
}
