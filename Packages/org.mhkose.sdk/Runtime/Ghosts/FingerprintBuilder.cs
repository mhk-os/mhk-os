using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>Incremental SHA-256 over newline-terminated invariant text tokens (floats rounded to 4 decimals).</summary>
    sealed class FingerprintBuilder : IDisposable
    {
        const int HashBytes = 16;
        const int FloatDecimals = 4;

        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Add(string s) => hash.AppendData(Encoding.UTF8.GetBytes((s ?? "") + "\n"));
        public void Add(int v) => Add(v.ToString(CultureInfo.InvariantCulture));
        public void Add(bool v) => Add(v ? 1 : 0);
        public void Add(float v) => Add(Math.Round(v, FloatDecimals).ToString("R", CultureInfo.InvariantCulture));

        public void Add(Vector2 v)
        {
            Add(v.x);
            Add(v.y);
        }

        public void Add(Vector3 v)
        {
            Add(v.x);
            Add(v.y);
            Add(v.z);
        }

        public void Add(float[] values)
        {
            Add(values != null ? values.Length : 0);
            if (values == null) return;
            foreach (var v in values) Add(v);
        }

        public void Add(Vector3[] values)
        {
            Add(values != null ? values.Length : 0);
            if (values == null) return;
            foreach (var v in values) Add(v);
        }

        /// <summary>Adds "null" and returns true when <paramref name="o"/> is missing (Unity objects compare with their own null).</summary>
        public bool AddNull(object o)
        {
            var missing = o is UnityEngine.Object u ? u == null : o == null;
            if (missing) Add("null");
            return missing;
        }

        public string Finish()
        {
            var bytes = hash.GetHashAndReset();
            var sb = new StringBuilder(HashBytes * 2);
            for (var i = 0; i < HashBytes; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        public void Dispose() => hash.Dispose();
    }
}
