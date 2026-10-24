using System.IO;
using UnityEngine;

namespace MHK.Core
{
    /// <summary>Files and folders of the player's data under <see cref="Application.persistentDataPath"/>.</summary>
    public static class DataPaths
    {
        public const string GhostsFolder = "Ghosts";
        public const string ModsFolder = "Mods";

        public static string Root => Application.persistentDataPath;

        /// <summary><paramref name="name"/> inside the persistent data folder.</summary>
        public static string File(string name) => Path.Combine(Root, name);

        public static string Ghosts => File(GhostsFolder);
        public static string Mods => File(ModsFolder);
    }
}
