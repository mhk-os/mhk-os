using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>One track import: its track.json, target folder, model library and the models loaded so far.</summary>
    public sealed class TrackImportContext
    {
        readonly Dictionary<(string file, GlbLightingStyle style), GlbModel> models = new();
        readonly List<GameObject> scratch = new();

        public ExportImportSettings Settings { get; }
        public JObject Json { get; }
        public string Dir { get; }
        public string Id { get; }
        public string ShortName { get; }
        /// <summary>Pack id + short name: the scene, definition and library file name.</summary>
        public string BaseName { get; }
        public string Folder { get; }
        public float Scale { get; }
        public ModelLibraryWriter Library { get; }

        public string ScenePath => $"{Folder}/{BaseName}.unity";

        public TrackImportContext(string dir, ExportImportSettings s)
        {
            Settings = s;
            Dir = dir;
            Json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "track.json")));
            var packId = s.pack.packId;
            Id = ExportJson.Str(Json["id"], packId + "." + Path.GetFileName(dir));
            ShortName = ExportJson.SafeShortName(Id);
            BaseName = $"{AssetPaths.SafeFileName(packId)}_{ShortName}";
            Folder = $"{ImportAssets.PackFolder(s.pack)}/Tracks/{BaseName}";
            AssetPaths.EnsureFolder(Folder);
            Scale = s.TrackScale(Id);
            Library = ModelLibraryWriter.Open($"{Folder}/{BaseName}_models.asset");
        }

        /// <summary>The model <paramref name="file"/> (relative to the track folder), imported once per style. Null when missing.</summary>
        public GlbModel Model(string file, GlbLightingStyle style)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (models.TryGetValue((file, style), out var m)) return m;
            var src = Path.GetFullPath(Path.Combine(Dir, file));
            if (!File.Exists(src))
            {
                Debug.LogWarning($"[Import] {ShortName}: missing model {src}");
                return null;
            }
            m = GlbModelImporter.Import(src, new GlbImportOptions
            {
                scale = Scale,
                textureFolder = Folder + "/Textures",
                materialFolder = Folder + "/Materials",
                library = Library,
                namePrefix = AssetPaths.SafeFileName(Path.GetFileNameWithoutExtension(file)),
                exportRoot = Settings.exportFolder,
                lighting = style,
            });
            m.root.SetActive(false);
            scratch.Add(m.root);
            models[(file, style)] = m;
            return m;
        }

        /// <summary>A fresh active copy of <paramref name="m"/>'s hierarchy sharing its assets.</summary>
        public GameObject Instance(GlbModel m, Transform parent, string name)
        {
            var go = Object.Instantiate(m.root, parent, false);
            go.name = name;
            go.SetActive(true);
            return go;
        }

        /// <summary>Destroys the loaded model hierarchies (they must not be saved with the scene).</summary>
        public void DestroyModels()
        {
            foreach (var go in scratch)
                if (go != null) Object.DestroyImmediate(go);
            scratch.Clear();
        }
    }
}
