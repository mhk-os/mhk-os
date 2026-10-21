using System;
using System.IO;
using System.Text;

namespace MHK.Core
{
    /// <summary>Crash-safe file writes: data goes to a temporary file that then replaces the target in one step.</summary>
    public static class SafeFile
    {
        public const string TempSuffix = ".tmp";
        public const string CorruptSuffix = ".corrupt";

        static readonly UTF8Encoding Utf8NoBom = new(false);

        /// <summary>Writes <paramref name="path"/> through <paramref name="write"/>. The old file stays intact until the new one is complete.</summary>
        public static void Write(string path, Action<Stream> write)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + TempSuffix;
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(f);
                f.Flush(true);
            }
            Replace(tmp, path);
        }

        /// <summary>Writes <paramref name="text"/> as UTF-8 without BOM (like File.WriteAllText).</summary>
        public static void WriteAllText(string path, string text) =>
            Write(path, s =>
            {
                var bytes = Utf8NoBom.GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            });

        /// <summary>Moves <paramref name="from"/> onto <paramref name="to"/>, replacing an existing file without a moment where neither exists.</summary>
        public static void Replace(string from, string to)
        {
            if (File.Exists(to)) File.Replace(from, to, null);
            else File.Move(from, to);
        }

        /// <summary>Keeps an unreadable file as <c>&lt;path&gt;.corrupt</c> so the next write cannot destroy it. Returns the new path, or null.</summary>
        public static string Quarantine(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var target = path + CorruptSuffix;
                if (File.Exists(target)) File.Delete(target);
                File.Move(path, target);
                return target;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
