using System;
using System.IO;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// JSON files in the persistent data folder (settings, saves). Writes are atomic. An unreadable file is reported and
    /// kept as <c>&lt;name&gt;.corrupt</c> before defaults replace it, so a later save cannot destroy it.
    /// </summary>
    public static class JsonStore
    {
        public static T Load<T>(string file) where T : new()
        {
            var path = DataPaths.File(file);
            if (!File.Exists(path)) return new T();
            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(path)) ?? new T();
            }
            catch (Exception e)
            {
                var kept = SafeFile.Quarantine(path);
                Debug.LogError($"[Save] Could not read {path} ({e.Message}); using defaults" +
                               (kept != null ? $", the file is kept as {kept}." : "."));
                return new T();
            }
        }

        public static void Save<T>(string file, T data)
        {
            var path = DataPaths.File(file);
            try
            {
                SafeFile.WriteAllText(path, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Save] Could not write {path}: {e.Message}");
            }
        }
    }
}
