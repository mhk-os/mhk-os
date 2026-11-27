using System;
using System.IO;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>
    /// JSON files in the persistent data folder (settings, saves, achievements, the stored login), or in
    /// <see cref="Folder"/>. Writes are atomic. An unreadable file is reported and kept as <c>&lt;name&gt;.corrupt</c>
    /// before defaults replace it, so a later save cannot destroy it.
    /// </summary>
    public static partial class JsonStore
    {
        /// <summary>
        /// Folder of the files instead of the persistent data folder, null for that one. Tests point it at a copy, so the
        /// player's own files stay as they were also through the saves when play mode ends; the next play session starts
        /// without it.
        /// </summary>
        [AutoStaticsCleanup] public static string Folder { get; set; }

        static string PathOf(string file) => Folder != null ? Path.Combine(Folder, file) : DataPaths.File(file);

        public static T Load<T>(string file) where T : new()
        {
            var path = PathOf(file);
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
            var path = PathOf(file);
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
