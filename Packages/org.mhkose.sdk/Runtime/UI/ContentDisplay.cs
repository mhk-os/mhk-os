using System.Collections.Generic;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Names and pictures of the tracks, drivers and karts that records, ghosts and results refer to by id. Content
    /// that is not loaded shows the name stored in the save (<see cref="SaveData.RememberContent"/>) and a placeholder picture.
    /// </summary>
    public static class ContentDisplay
    {
        /// <summary>Tooltip of a track whose pack is not loaded (disabled Play buttons, missing record rows).</summary>
        public const string MissingTrackText = "No mod has this track.";
        const string MissingDriverFormat = "{0}\nNo mod has this driver.";
        const string MissingKartFormat = "{0}\nNo mod has this kart.";
        /// <summary>Class of a picture whose content is not loaded (a neutral placeholder in USS).</summary>
        public const string MissingClass = "missing";

        static ContentRegistry Content => GameRoot.Instance.Content;
        static SaveData Save => GameRoot.Instance.Save;

        public static string TrackName(string id)
        {
            var track = Content.Get<TrackDefinition>(id);
            return track != null ? track.DisplayName : Known(Save.knownTracks, id, false);
        }

        /// <summary>Title of the track's source game.</summary>
        public static string TrackGame(string id)
        {
            var track = Content.Get<TrackDefinition>(id);
            return track != null ? UiText.GameName(track.SourceGame) : Known(Save.knownTracks, id, true);
        }

        public static string CharacterName(string id)
        {
            var character = Content.Get<CharacterDefinition>(id);
            return character != null ? character.DisplayName : Known(Save.knownCharacters, id, false);
        }

        public static string KartName(string id)
        {
            var kart = Content.Get<KartDefinition>(id);
            return kart != null ? kart.DisplayName : Known(Save.knownKarts, id, false);
        }

        /// <summary>
        /// Shows the driver's portrait in <c>driver-portrait</c> and the kart's icon in <c>driver-kart</c> below
        /// <paramref name="parent"/>, their names as tooltips. An empty id hides its picture (old records have no kart).
        /// </summary>
        public static void SetDriver(VisualElement parent, string characterId, string kartId)
        {
            var character = Content.Get<CharacterDefinition>(characterId);
            var portrait = character == null ? null : character.portrait != null ? character.portrait : character.Icon;
            SetPicture(parent.Q("driver-portrait"), characterId, character != null, portrait, CharacterName(characterId), MissingDriverFormat);
            var kart = Content.Get<KartDefinition>(kartId);
            SetPicture(parent.Q("driver-kart"), kartId, kart != null, kart != null ? kart.Icon : null, KartName(kartId), MissingKartFormat);
        }

        static void SetPicture(VisualElement e, string id, bool loaded, Texture2D image, string name, string missingFormat)
        {
            if (e == null) return;
            var hasId = !string.IsNullOrEmpty(id);
            e.style.visibility = hasId ? Visibility.Visible : Visibility.Hidden;
            // Null instead of None: the placeholder picture comes from USS.
            e.style.backgroundImage = image != null ? new StyleBackground(image) : new StyleBackground(StyleKeyword.Null);
            e.EnableInClassList(MissingClass, hasId && !loaded);
            e.tooltip = !hasId ? "" : loaded ? name : string.Format(missingFormat, name);
        }

        static string Known(List<KnownContent> list, string id, bool game)
        {
            var known = SaveData.Find(list, id);
            if (known == null) return game ? "" : id;
            return game ? known.game : known.name;
        }
    }
}
