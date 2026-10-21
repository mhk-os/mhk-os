using System.IO;
using MHK.Effects;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Ambient sounds of track.json ("sounds": sound, pos, and prop "sound"): looping 3D sounds with optional "volume",
    /// "minDistance" and "maxDistance" (else the settings' defaults), clips copied into the pack's Audio folder.
    /// </summary>
    public static class TrackAudioBuilder
    {
        /// <summary>Builds the Sounds group under <paramref name="root"/> when the track has sounds.</summary>
        public static void BuildSounds(TrackImportContext c, Transform root)
        {
            if (c.Json["sounds"] is not JArray sounds || sounds.Count == 0) return;
            var group = ImportHierarchy.CreateChild(root, "Sounds");
            foreach (var snd in sounds)
            {
                var file = ExportJson.Str(snd["sound"]);
                if (file == null) continue;
                var go = ImportHierarchy.CreateChild(group, ExportJson.Str(snd["name"]) ?? Path.GetFileNameWithoutExtension(file));
                go.localPosition = ExportJson.Pos(snd["pos"], c.Scale);
                AddLoopSound(c, go.gameObject, file, snd);
            }
        }

        /// <summary>Adds a looping sound of <paramref name="file"/> (relative to the track folder) to <paramref name="host"/>.</summary>
        public static void AddLoopSound(TrackImportContext c, GameObject host, string file, JToken entry)
        {
            var s = c.Settings;
            var src = Path.GetFullPath(Path.Combine(c.Dir, file));
            var clip = ImportAssets.ImportAudio(src, $"{ImportAssets.PackFolder(s.pack)}/Audio/{AssetPaths.SafeFileName(Path.GetFileNameWithoutExtension(src))}", false);
            if (clip == null) return;
            host.AddComponent<EffectLoopSound>().Configure(clip,
                Mathf.Clamp01(ExportJson.F(entry["volume"], s.ambienceVolume)),
                ExportJson.F(entry["minDistance"], s.ambienceMinDistance) * c.Scale,
                ExportJson.F(entry["maxDistance"], s.ambienceMaxDistance) * c.Scale);
        }
    }
}
