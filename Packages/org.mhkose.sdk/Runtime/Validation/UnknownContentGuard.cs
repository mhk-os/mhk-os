using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using MHK.Online;
using MHK.UI;
using Unity.Scripting.LifecycleManagement;

namespace MHK.Validation
{
    /// <summary>
    /// Online races: resolves the character and kart a remote racer chose. Content this game does not have gets a stand-in
    /// (the usual fallbacks: the first character, the character's default kart) that wears a '?' sign
    /// (<see cref="Karts.UnknownContentMarker"/>), the server hears about it once per racer and id
    /// (<see cref="UnknownContent"/>: one reporter about a racer means the reporter's copy is the odd one), and the
    /// player sees a notice.
    /// </summary>
    public static partial class UnknownContentGuard
    {
        [AutoStaticsCleanup] static long raceId;
        [AutoStaticsCleanup] static readonly HashSet<(int slot, string id)> sent = new();
        [AutoStaticsCleanup] static readonly HashSet<string> unknownCharacters = new(), unknownKarts = new();

        /// <summary>
        /// Character and kart of grid slot <paramref name="slot"/>. False when either is not installed: then
        /// <paramref name="character"/> and <paramref name="kart"/> are the stand-ins (null only without any content).
        /// </summary>
        public static bool Resolve(int slot, string characterId, string kartId, out CharacterDefinition character, out KartDefinition kart)
        {
            var race = OnlineRace.Current;
            BeginRace(race != null ? race.RaceId : 0);
            var content = GameRoot.Instance.Content;
            var known = true;
            character = content.Get<CharacterDefinition>(characterId);
            if (character == null)
            {
                known = false;
                unknownCharacters.Add(characterId ?? "");
                Report(race, slot, ContentKind.Character, characterId);
                var all = content.All<CharacterDefinition>();
                character = all.Count > 0 ? all[0] : null;
            }
            kart = content.Get<KartDefinition>(kartId);
            if (kart == null)
            {
                // No kart id at all is no claim about content: the character's default kart is what the racer drives.
                if (!string.IsNullOrEmpty(kartId))
                {
                    known = false;
                    unknownKarts.Add(kartId);
                    Report(race, slot, ContentKind.Kart, kartId);
                }
                kart = content.ResolveKart(kartId, character);
            }
            return known;
        }

        /// <summary>
        /// The kart of <paramref name="setup"/> stands in for content that is not installed (<see cref="Resolve"/> of the
        /// current online race), so its visual shows the '?' sign.
        /// </summary>
        public static bool IsPlaceholder(RacerSetup setup)
        {
            if (setup == null || (unknownCharacters.Count == 0 && unknownKarts.Count == 0)) return false;
            var content = GameRoot.Instance.Content;
            return (setup.characterId != null && unknownCharacters.Contains(setup.characterId) && content.Get<CharacterDefinition>(setup.characterId) == null)
                   || (setup.kartId != null && unknownKarts.Contains(setup.kartId) && content.Get<KartDefinition>(setup.kartId) == null);
        }

        static void BeginRace(long id)
        {
            if (id == raceId) return;
            raceId = id;
            sent.Clear();
            unknownCharacters.Clear();
            unknownKarts.Clear();
        }

        static void Report(OnlineRace race, int slot, ContentKind kind, string id)
        {
            id ??= "";
            if (!sent.Add((slot, id))) return;
            if (race == null) return;
            race.Notify(new UnknownContent { raceId = race.RaceId, slot = slot, kind = (int)kind, id = id });
            var name = NameOf(race, slot);
            OnlineNotices.Post(kind == ContentKind.Character
                ? $"{name} drives a character you don't have ({id}): shown as '?'"
                : $"{name} drives a kart you don't have ({id}): shown as '?'", NoticeKind.Info);
        }

        static string NameOf(OnlineRace race, int slot)
        {
            var grid = race.Setup != null ? race.Setup.grid : null;
            if (grid != null)
                foreach (var s in grid)
                    if (s.slot == slot && !string.IsNullOrEmpty(s.username)) return s.username;
            return $"Racer {slot + 1}";
        }
    }
}
