using System;
using System.Collections.Generic;
using MHK.Modding;

namespace MHK.Core
{
    /// <summary>
    /// Best lap and race time of one track, with the driver and kart that set them. Kart ids are empty in records set
    /// before karts were stored.
    /// </summary>
    [Serializable]
    public class TrackRecord
    {
        public string trackId;
        public float bestLap = -1;
        public float bestRace = -1;
        public string bestLapCharacter;
        public string bestRaceCharacter;
        public string bestLapKart;
        public string bestRaceKart;
    }

    /// <summary>
    /// Display name of a content id as it was last loaded, so records and ghosts of a pack that is disabled or removed
    /// still show what they were set on.
    /// </summary>
    [Serializable]
    public class KnownContent
    {
        public string id;
        public string name;
        /// <summary>Title of the source game (the pack with the definition's source id).</summary>
        public string game;
    }

    /// <summary>Progress and records, persisted as JSON. Field names are JSON keys: never rename them.</summary>
    [Serializable]
    public class SaveData
    {
        public List<TrackRecord> records = new();
        public List<string> wonCups = new();
        public List<KnownContent> knownTracks = new();
        public List<KnownContent> knownCharacters = new();
        public List<KnownContent> knownKarts = new();

        /// <summary>The record of <paramref name="trackId"/>, created when missing.</summary>
        public TrackRecord GetRecord(string trackId)
        {
            var r = records.Find(x => x.trackId == trackId);
            if (r != null) return r;
            r = new TrackRecord { trackId = trackId };
            records.Add(r);
            return r;
        }

        /// <summary>
        /// Stores the names of every loaded track, character and kart (older saves get them on their first load).
        /// Returns true when something changed and the save should be written.
        /// </summary>
        public bool RememberContent(ContentRegistry content)
        {
            var changed = Remember(knownTracks, content.All<TrackDefinition>(), content);
            changed |= Remember(knownCharacters, content.All<CharacterDefinition>(), content);
            changed |= Remember(knownKarts, content.All<KartDefinition>(), content);
            return changed;
        }

        /// <summary>The stored name of <paramref name="id"/> in <paramref name="list"/>, null when it was never loaded.</summary>
        public static KnownContent Find(List<KnownContent> list, string id) =>
            string.IsNullOrEmpty(id) ? null : list.Find(k => k.id == id);

        static bool Remember<T>(List<KnownContent> list, IReadOnlyList<T> defs, ContentRegistry content) where T : ContentDefinition
        {
            var changed = false;
            foreach (var def in defs)
            {
                var game = GameTitle(content, def.SourceGame);
                var known = Find(list, def.Id);
                if (known == null) list.Add(known = new KnownContent { id = def.Id });
                else if (known.name == def.DisplayName && known.game == game) continue;
                known.name = def.DisplayName;
                known.game = game;
                changed = true;
            }
            return changed;
        }

        static string GameTitle(ContentRegistry content, string sourceGame)
        {
            foreach (var p in content.Packs)
                if (string.Equals(p.packId, sourceGame, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(p.title))
                    return p.title;
            return sourceGame;
        }
    }
}
