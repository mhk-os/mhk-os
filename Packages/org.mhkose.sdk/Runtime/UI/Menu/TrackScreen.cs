using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Track browser (Track.uxml): a card per track and the info panel of the hovered or focused one (laps or arena,
    /// records). Battles list arenas first, the other modes circuits only. Time trials open the ghost picker, the other
    /// modes the race settings.
    /// </summary>
    public class TrackScreen : CardGridScreen<TrackDefinition>
    {
        [SerializeField, Tooltip("Header title in battles (the UXML title is used for the other modes).")]
        string battleTitle = "Choose an arena";
        [SerializeField, Tooltip("Tooltip of the swords badge on arena cards in battles.")]
        string arenaTip = "Arena, built for battles.";
        [SerializeField, Tooltip("Appended to the game name when the track has a variant. {0} = variant.")]
        string variantFormat = " | {0}";
        [SerializeField, Tooltip("Length fact for one lap. {0} = laps.")]
        string lapFormat = "{0} lap";
        [SerializeField, Tooltip("Length fact for several laps. {0} = laps.")]
        string lapsFormat = "{0} laps";
        [SerializeField, Tooltip("A record. {0} = time, {1} = driver.")]
        string recordFormat = "{0} | {1}";
        [SerializeField, Tooltip("Shown instead of a record time when there is none.")]
        string noRecord = "—";

        List<TrackDefinition> tracks;
        bool battle;
        VisualElement arenaLine, lapsLine;
        Label lapsText, bestLap, bestRace;

        void Reset()
        {
            showcaseVisible = false;
            cardStep = 0.014f;
        }

        protected override IEnumerable<TrackDefinition> Items => tracks;

        protected override void BindPage()
        {
            battle = Selection.mode == GameMode.Battle;
            if (battle) Q<Label>("title").text = battleTitle;
            // Arenas only host battles, but battles may also be fought on any circuit (arenas listed first).
            tracks = battle
                ? Content.All<TrackDefinition>().OrderBy(t => t.layout == TrackLayout.Arena ? 0 : 1).ToList()
                : Content.All<TrackDefinition>().Where(t => t.layout == TrackLayout.Circuit).ToList();
            arenaLine = Q<VisualElement>("info-arena");
            lapsLine = Q<VisualElement>("info-laps");
            lapsText = Q<Label>("info-laps-text");
            bestLap = Q<Label>("info-best-lap");
            bestRace = Q<Label>("info-best-race");
        }

        protected override string CardTag(TrackDefinition t) => UiText.GameName(t.SourceGame);

        protected override Texture2D CardImage(TrackDefinition t) => t.Picture;

        protected override string InfoGame(TrackDefinition t) => UiText.GameWithVariant(t, variantFormat);

        // Arenas get the swords badge (the gold won-cup badge of cup cards) with a hover tooltip.
        protected override void OnCardSpawned(Button card, TrackDefinition t)
        {
            var badge = card.Q("card-badge");
            if (badge == null) return;
            var arena = battle && t.layout == TrackLayout.Arena;
            badge.SetVisible(arena);
            if (!arena) return;
            badge.pickingMode = PickingMode.Position;
            badge.tooltip = arenaTip;
            var icon = badge.Q(className: "icon");
            icon?.RemoveFromClassList("i-emoji_events");
            icon?.AddToClassList("i-swords");
        }

        protected override void Describe(TrackDefinition t)
        {
            var arena = t.layout == TrackLayout.Arena;
            arenaLine.SetVisible(arena);
            lapsLine.SetVisible(!arena);
            if (!arena) lapsText.text = string.Format(t.defaultLaps == 1 ? lapFormat : lapsFormat, t.defaultLaps);
            var rec = GameRoot.Instance.Save.records.Find(r => r.trackId == t.Id);
            bestLap.text = rec != null ? Record(rec.bestLap, rec.bestLapCharacter) : noRecord;
            bestRace.text = rec != null ? Record(rec.bestRace, rec.bestRaceCharacter) : noRecord;
        }

        string Record(float time, string characterId) =>
            time > 0 ? string.Format(recordFormat, UiText.Clock(time), DriverName(characterId)) : noRecord;

        static string DriverName(string characterId)
        {
            var character = Content.Get<CharacterDefinition>(characterId);
            return character != null ? character.DisplayName : characterId;
        }

        protected override void Pick(TrackDefinition track) => Continue(track);

        /// <summary>
        /// What follows the track choice for <paramref name="track"/>: the ghost picker in time trials, else the race
        /// settings. The kart page calls it directly when the track was fixed beforehand (<see cref="MenuSelection.trackFixed"/>).
        /// </summary>
        public void Continue(TrackDefinition track)
        {
            var selection = Selection;
            selection.track = track;
            if (selection.mode == GameMode.TimeTrial)
            {
                Flow.Get<GhostScreen>().Open(track);
                return;
            }
            Flow.Get<RaceSettingsScreen>().Open(() => SceneFlow.StartSession(selection.BuildSession(new[] { track }, selection.opponents)),
                track.DisplayName, track.Picture);
        }
    }
}
