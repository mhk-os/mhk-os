using System.Collections.Generic;
using System.Linq;
using MHK.Core;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Championship browser (Cup.uxml): a card per cup (won cups carry a trophy badge) and the info panel of the
    /// hovered or focused cup with its track list. Cards and panel cross-fade through the cup's track pictures.
    /// Picking a cup opens the race settings.
    /// </summary>
    public class CupScreen : CardGridScreen<CupDefinition>
    {
        [SerializeField, Tooltip("Track of the cup preview list (Templates/CupTrackItem.uxml).")]
        VisualTreeAsset trackItemTemplate;
        [SerializeField, Tooltip("Card tag and preview line. {0} = game, {1} = number of races.")]
        string racesFormat = "{0} | {1} races";
        [SerializeField, Min(0.5f), Tooltip("Seconds each track picture of a cup shows on its card and in the info panel. The cards change one after another, the fade time is the .image-fade transition in MHKMenu.uss.")]
        float pictureSeconds = 3.5f;

        const int CupOpponents = 5;

        readonly Dictionary<CupDefinition, List<Texture2D>> pictures = new();
        readonly List<ImageCycle> cardCycles = new();
        readonly List<CupDefinition> cardCups = new();
        List<CupDefinition> cups;
        VisualElement trackList, wonLine;
        ImageCycle infoCycle;
        CupDefinition infoCup;
        IVisualElementScheduledItem cycleTimer;
        int nextCard;

        void Reset()
        {
            showcaseVisible = false;
            cardStep = 0.022f;
        }

        protected override IEnumerable<CupDefinition> Items => cups;

        protected override void BindPage()
        {
            cups = Content.All<CupDefinition>().OrderBy(c => c.order).ToList();
            pictures.Clear();
            foreach (var c in cups) pictures[c] = PicturesOf(c);
            trackList = Q<VisualElement>("cup-tracks");
            wonLine = Q<VisualElement>("info-won");
            infoCycle = new ImageCycle(Q<VisualElement>("info-image"));
            infoCup = null;
            cycleTimer = Root.schedule.Execute(StepNextCard).StartingIn(UiAnim.Ms(pictureSeconds)).Every(UiAnim.Ms(pictureSeconds));
        }

        protected override void Unbind()
        {
            base.Unbind();
            cycleTimer?.Pause();
            cycleTimer = null;
            cardCycles.Clear();
            cardCups.Clear();
            infoCycle = null;
            infoCup = null;
        }

        // The cup's track pictures in race order, else its own icon (a mod cup whose tracks have none).
        static List<Texture2D> PicturesOf(CupDefinition c)
        {
            var list = c.tracks.Where(t => t != null && t.Picture != null).Select(t => t.Picture).Distinct().ToList();
            if (list.Count == 0 && c.Icon != null) list.Add(c.Icon);
            return list;
        }

        protected override string CardTag(CupDefinition c) => string.Format(racesFormat, UiText.GameName(c.SourceGame), c.tracks.Count);

        protected override Texture2D CardImage(CupDefinition c) => pictures[c].Count > 0 ? pictures[c][0] : null;

        protected override string InfoGame(CupDefinition c) => CardTag(c);

        protected override void OnGridCleared()
        {
            cardCycles.Clear();
            cardCups.Clear();
            nextCard = 0;
        }

        protected override void OnCardSpawned(Button card, CupDefinition c)
        {
            card.Q("card-badge")?.SetVisible(GameRoot.Instance.Save.wonCups.Contains(c.Id));
            var image = card.Q("card-image");
            if (image == null) return;
            var cycle = new ImageCycle(image);
            cycle.Show(pictures[c]);
            cardCycles.Add(cycle);
            cardCups.Add(c);
            if (c == infoCup) infoCycle.Show(pictures[c]);
            // One card changes per tick, so each card changes every pictureSeconds.
            cycleTimer.Every(UiAnim.Ms(pictureSeconds) / cardCycles.Count);
        }

        void StepNextCard()
        {
            if (cardCycles.Count == 0) return;
            if (nextCard >= cardCycles.Count) nextCard = 0;
            cardCycles[nextCard].Step();
            if (cardCups[nextCard] == infoCup) infoCycle.Step();
            nextCard++;
        }

        protected override void Describe(CupDefinition c)
        {
            infoCup = c;
            var card = cardCups.IndexOf(c);
            infoCycle.Show(pictures[c], card >= 0 ? cardCycles[card].Index : 0);
            trackList.Clear();
            var number = 1;
            foreach (var t in c.tracks)
            {
                if (t == null) continue;
                var item = UiTree.Spawn<VisualElement>(trackItemTemplate, trackList);
                if (item == null) break;
                item.SetText("number", (number++).ToString());
                item.SetText("track-name", t.DisplayName);
            }
            wonLine.SetVisible(GameRoot.Instance.Save.wonCups.Contains(c.Id));
        }

        protected override void Pick(CupDefinition cup)
        {
            var selection = Selection;
            Flow.Get<RaceSettingsScreen>().Open(() =>
            {
                var session = selection.BuildSession(cup.tracks.Where(t => t != null), CupOpponents);
                session.cupId = cup.Id;
                if (cup.lapsOverride > 0)
                {
                    session.lapsFromTrack = false;
                    session.laps = cup.lapsOverride;
                }
                SceneFlow.StartSession(session);
            }, cup.DisplayName, CardImage(cup));
        }
    }
}
