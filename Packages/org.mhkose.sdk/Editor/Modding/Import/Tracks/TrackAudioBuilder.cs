using System.IO;
using MHK.Audio;
using MHK.Effects;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Ambient sounds of track.json ("sounds": sound, pos, and prop "sound"): looping 3D sounds with optional "volume",
    /// "minDistance" and "maxDistance" (else the settings' defaults), clips copied into the pack's Audio folder. Sounds
    /// with an "event" (period, window [s], maxDistance) are timed one-shots instead (<see cref="TrackEventSound"/>).
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
                if (snd["event"] is JObject ev) AddEventSound(c, go.gameObject, file, ev);
                else AddLoopSound(c, go.gameObject, file, snd);
            }
        }

        static AudioClip ImportClip(TrackImportContext c, string file)
        {
            var src = Path.GetFullPath(Path.Combine(c.Dir, file));
            return ImportAssets.ImportAudio(src, $"{ImportAssets.PackFolder(c.Settings.pack)}/Audio/{AssetPaths.SafeFileName(Path.GetFileNameWithoutExtension(src))}", false);
        }

        static void AddEventSound(TrackImportContext c, GameObject host, string file, JObject ev)
        {
            var clip = ImportClip(c, file);
            if (clip == null) return;
            var window = ExportJson.Items(ev["window"]);
            var shot = host.AddComponent<TrackEventSound>();
            shot.clip = clip;
            shot.period = ExportJson.F(ev["period"], 1f);
            shot.windowStart = window.Count > 0 ? ExportJson.F(window[0]) : 0f;
            shot.windowEnd = window.Count > 1 ? ExportJson.F(window[1]) : shot.windowStart + 0.1f;
            shot.volume = Mathf.Clamp01(ExportJson.F(ev["volume"], 1f));
            shot.range = new SoundRange
            {
                fullVolumeDistance = c.Settings.ambienceMinDistance * c.Scale,
                fadeDistance = ExportJson.F(ev["maxDistance"], c.Settings.ambienceMaxDistance) * c.Scale,
            };
        }

        /// <summary>Adds a looping sound of <paramref name="file"/> (relative to the track folder) to <paramref name="host"/>.</summary>
        public static void AddLoopSound(TrackImportContext c, GameObject host, string file, JToken entry)
        {
            var s = c.Settings;
            var clip = ImportClip(c, file);
            if (clip == null) return;
            host.AddComponent<EffectLoopSound>().Configure(clip,
                Mathf.Clamp01(ExportJson.F(entry["volume"], s.ambienceVolume)),
                ExportJson.F(entry["minDistance"], s.ambienceMinDistance) * c.Scale,
                ExportJson.F(entry["maxDistance"], s.ambienceMaxDistance) * c.Scale);
        }
    }
}
