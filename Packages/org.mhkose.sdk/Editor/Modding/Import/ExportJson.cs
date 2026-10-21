using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Lenient value readers for the export contract's JSON files (wrong token types give the fallback). Coordinates
    /// convert from glTF space like the models: x is negated, rotations become (x, -y, -z, w).
    /// </summary>
    public static class ExportJson
    {
        public static Vector3 Pos(JToken a, float scale = 1f) =>
            a is JArray { Count: >= 3 } ? new Vector3(-(float)a[0] * scale, (float)a[1] * scale, (float)a[2] * scale) : Vector3.zero;

        /// <summary>A direction (no scale), forward when missing.</summary>
        public static Vector3 Dir(JToken a) => a is JArray { Count: >= 3 } ? new Vector3(-(float)a[0], (float)a[1], (float)a[2]) : Vector3.forward;

        public static Quaternion Rot(JToken q) =>
            q is JArray { Count: >= 4 } ? new Quaternion((float)q[0], -(float)q[1], -(float)q[2], (float)q[3]).normalized : Quaternion.identity;

        /// <summary>A 3-vector, a uniform number, else one.</summary>
        public static Vector3 Scale(JToken a) =>
            a is JArray { Count: >= 3 } ? new Vector3((float)a[0], (float)a[1], (float)a[2]) : a != null && a.Type is JTokenType.Float or JTokenType.Integer ? Vector3.one * (float)a : Vector3.one;

        public static Color Col(JToken a, float fallback = 1f)
        {
            if (a is not JArray { Count: >= 3 } arr) return new Color(fallback, fallback, fallback);
            return new Color((float)arr[0], (float)arr[1], (float)arr[2], arr.Count > 3 ? (float)arr[3] : 1f);
        }

        public static string Str(JToken t, string fallback = null) =>
            t == null || t.Type == JTokenType.Null ? fallback : t.Type == JTokenType.String ? (string)t : t.ToString();

        public static float F(JToken t, float fallback = 0f) =>
            t != null && t.Type is JTokenType.Float or JTokenType.Integer ? (float)t : fallback;

        public static int I(JToken t, int fallback = 0) =>
            t != null && t.Type is JTokenType.Float or JTokenType.Integer ? (int)(float)t : fallback;

        public static bool B(JToken t, bool fallback = false) =>
            t == null || t.Type == JTokenType.Null ? fallback : t.Type == JTokenType.Boolean ? (bool)t : F(t) != 0f;

        /// <summary>The array, or a new empty one when <paramref name="t"/> is missing or not an array.</summary>
        public static JArray Items(JToken t) => t as JArray ?? new JArray();

        /// <summary>Id without its pack namespace ("tincan.desert" gives "desert").</summary>
        public static string ShortName(string id) => id.Contains('.') ? id[(id.IndexOf('.') + 1)..] : id;

        /// <summary>File-name-safe short name of <paramref name="id"/>.</summary>
        public static string SafeShortName(string id) => AssetPaths.SafeFileName(ShortName(id));

        /// <summary>Display name: "displayName", else "name" up to " (" (original names, no translations in parentheses).</summary>
        public static string DisplayName(JObject json, string fallback)
        {
            var name = Str(json["displayName"]) ?? Str(json["name"]) ?? fallback;
            var cut = name.IndexOf(" (", System.StringComparison.Ordinal);
            return cut > 0 ? name[..cut] : name;
        }
    }
}
