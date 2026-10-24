using MHK.Core;
using MHK.Modding;
using MHK.Race;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Loading screen (Loading.uxml) shown by <see cref="SceneFlow"/> between scenes: track card and chips before a race,
    /// the logo otherwise, with a progress bar and a random tip.
    /// </summary>
    public class LoadingScreen : UiScreen
    {
        [Header("Race")]
        [SerializeField, Tooltip("Appended to the game name when the track has a variant. {0} = variant.")]
        string variantFormat = " | {0}";
        [SerializeField, Tooltip("Chip text in battles.")]
        string battleText = "Battle";
        [SerializeField, Tooltip("Chip text for a one-lap race.")]
        string oneLapText = "1 lap";
        [SerializeField, Tooltip("Chip text for several laps ({0} = lap count).")]
        string lapsFormat = "{0} laps";
        [SerializeField, Tooltip("Championship chip ({0} = race number, {1} = races in the cup). The cup name is appended.")]
        string raceOfCupFormat = "Race {0} of {1}";
        [SerializeField, Tooltip("Chip text in time trials.")]
        string timeTrialText = "Time trial";
        [SerializeField, Tooltip("Chip text in single races and battles ({0} = racer count).")]
        string racersFormat = "{0} racers";

        [Header("Progress")]
        [SerializeField, Tooltip("Text next to the progress bar ({0} = percent, 0-100).")]
        string percentFormat = "{0}%";

        [Header("Tips")]
        [SerializeField, Tooltip("One of these is shown at random.")]
        string[] tips =
        {
            "Hold the throttle when the countdown says 1 for a power start. Too early and the engine stalls!",
            "Drive through item boxes without an item to pick up a powerup.",
            "Fake item boxes look almost like the real thing. Watch closely.",
            "Shortcuts are risky, but the computer drivers know them too.",
            "Sand, snow and grass slow you down. Stay on the road.",
            "In a battle, hammers and quad damage make every hit count.",
            "Every character can drive every kart.",
            "30 fps on Low keeps laptops and phones cool during long sessions.",
        };

        [Header("Animation")]
        [Tooltip("Seconds the fade-in takes (.loading-page transition in MHKLoading.uss). Scene loading starts after it.")]
        public float fadeInSeconds = 0.3f;
        [SerializeField, Tooltip("Seconds the fade-out takes (.loading-page.out in MHKLoading.uss). The screen closes after it.")]
        float fadeOutSeconds = 0.5f;
        [SerializeField, Tooltip("Scroll speed of the stripes in the progress bar (pixels per second).")]
        float stripSpeed = 48f;
        [SerializeField, Tooltip("How fast the progress bar catches up with the loading progress (1/s, higher = snappier).")]
        float barSmoothing = 6f;
        [SerializeField, Tooltip("Catch-up speed factor while fading out (the bar runs full within the fade).")]
        float hideSmoothingFactor = 3f;

        // Horizontal period of loading_stripes.png as shown (.loading-bar-stripes).
        const float StripePeriod = 32f;
        // Pictures narrower than this width / height (MHK2-style previews with a black border) get .square-art.
        const float SquareAspect = 1.3f;
        // The bar width moves in 0.1 % steps.
        const float BarSteps = 1000f, PercentPerStep = 0.1f;

        readonly string[] percentTexts = new string[101];

        GameSession session;
        TrackDefinition track;
        VisualElement page, strip, barFill;
        Label percent;
        float shownProgress, stripOffset, boundAt, closeAt, writtenWidth;
        int shownPercent;
        bool hiding;

        /// <summary>True once the screen has faded in and covers everything, until <see cref="Hide"/>.</summary>
        public bool Covering => IsBound && !hiding && Time.realtimeSinceStartup - boundAt >= fadeInSeconds;

        /// <summary>
        /// Opens (fades in) the screen for a race on <paramref name="track"/> of <paramref name="session"/>, or the generic
        /// variant when <paramref name="track"/> is null. An open screen starts over with the new content.
        /// </summary>
        public void Show(GameSession session, TrackDefinition track)
        {
            this.session = session;
            this.track = track;
            hiding = false;
            if (gameObject.activeSelf) Rebuild();
            else gameObject.SetActive(true);
        }

        /// <summary>Fades the screen out, then closes it (deactivates the GameObject).</summary>
        public void Hide()
        {
            if (!gameObject.activeSelf || hiding) return;
            if (!IsBound)
            {
                gameObject.SetActive(false);
                return;
            }
            hiding = true;
            // Fading out: clicks reach the new scene's UI already.
            Root.NoPick();
            page.AddToClassList("out");
            closeAt = Time.realtimeSinceStartup + fadeOutSeconds;
        }

        protected override void Bind()
        {
            hiding = false;
            boundAt = Time.realtimeSinceStartup;
            shownProgress = 0f;
            writtenWidth = -1f;
            shownPercent = -1;
            page = Q<VisualElement>("page");
            strip = Q<VisualElement>("strip");
            barFill = Q<VisualElement>("bar-fill");
            percent = Q<Label>("percent");
            ShowProgress();

            var race = track != null;
            page.EnableInClassList("menu-variant", !race);
            var logo = race ? null : GameRoot.Instance.Config.gameLogo;
            Q<VisualElement>("logo-wrap").SetVisible(logo != null);
            if (logo != null) Q<VisualElement>("logo").SetImage(logo);
            if (race) FillRace();
            ShowTip();
            FadeIn();
        }

        void ShowTip()
        {
            var tip = Q<Label>("tip");
            var hasTip = tips != null && tips.Length > 0;
            tip.parent.SetVisible(hasTip);
            if (hasTip) tip.text = tips[Random.Range(0, tips.Length)];
        }

        void FadeIn()
        {
            page.AddToClassList("out");
            page.schedule.Execute(() =>
            {
                if (!hiding) page.RemoveFromClassList("out");
            }).ExecuteLater(UiAnim.FrameMs);
        }

        void FillRace()
        {
            Q<Label>("track").text = track.DisplayName;
            Q<Label>("game").text = UiText.GameWithVariant(track, variantFormat);
            ShowTrackPicture(track.Picture);
            var battle = session != null && session.mode == GameMode.Battle;
            var laps = RaceManager.LapsFor(session, track);
            if (battle) SetChip("mode", "swords", battleText);
            else SetChip("mode", "flag", laps == 1 ? oneLapText : string.Format(lapsFormat, laps));
            Q<VisualElement>("chip-session").SetVisible(session != null);
            if (session == null) return;
            if (session.mode == GameMode.Championship) SetChip("session", "emoji_events", UiText.RaceOfCup(session, raceOfCupFormat));
            else if (session.mode == GameMode.TimeTrial) SetChip("session", "timer", timeTrialText);
            else SetChip("session", "sports_motorsports", string.Format(racersFormat, session.racers.Count));
        }

        void ShowTrackPicture(Texture2D image)
        {
            var hasImage = image != null;
            var backdrop = Q<VisualElement>("backdrop");
            backdrop.SetVisible(hasImage);
            Q<VisualElement>("card").SetVisible(hasImage);
            page.EnableInClassList("square-art", hasImage && image.width < image.height * SquareAspect);
            if (!hasImage) return;
            backdrop.SetImage(image);
            Q<VisualElement>("preview").SetImage(image);
        }

        void SetChip(string chip, string icon, string text)
        {
            Q<VisualElement>("chip-" + chip + "-icon").SetIcon(icon);
            Q<Label>("chip-" + chip + "-label").text = text;
        }

        void Update()
        {
            if (!IsBound) return;
            if (hiding && Time.realtimeSinceStartup >= closeAt)
            {
                gameObject.SetActive(false);
                return;
            }
            // Real time: a race left from the pause menu still runs at time scale 0.
            var dt = Time.unscaledDeltaTime;
            var target = hiding ? 1f : SceneFlow.Progress;
            var rate = hiding ? barSmoothing * hideSmoothingFactor : barSmoothing;
            shownProgress = Mathf.Lerp(shownProgress, target, Smoothing.Factor(rate, dt));
            ShowProgress();
            stripOffset = (stripOffset + stripSpeed * dt) % StripePeriod;
            strip.style.translate = new Translate(-stripOffset, 0f);
        }

        void ShowProgress()
        {
            var width = Mathf.Round(shownProgress * BarSteps) * PercentPerStep;
            if (width != writtenWidth)
            {
                writtenWidth = width;
                barFill.style.width = Length.Percent(width);
            }
            var p = Mathf.Clamp(Mathf.RoundToInt(shownProgress * 100f), 0, 100);
            if (p == shownPercent) return;
            shownPercent = p;
            percent.text = percentTexts[p] ??= string.Format(percentFormat, p);
        }
    }
}
