using System;
using System.Globalization;

namespace MHK.Core
{
    /// <summary>Command-line options of the player (<c>-name value</c>). Numbers parse with the invariant culture.</summary>
    public static class CommandLine
    {
        public static bool Has(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

        /// <summary>The argument following <paramref name="name"/>, or null.</summary>
        public static string Value(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        public static bool TryInt(string name, out int value) =>
            int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryFloat(string name, out float value) =>
            float.TryParse(Value(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
