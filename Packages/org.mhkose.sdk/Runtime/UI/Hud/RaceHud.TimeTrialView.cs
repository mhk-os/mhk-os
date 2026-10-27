using System.Collections.Generic;
using MHK.Core;
using MHK.Ghosts;
using MHK.Online;
using MHK.Race;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>
        /// Time trial: hides the item slot and position, shows the split time against the reference ghost at every lap
        /// line and name tags over the raced ghosts. A replay shows the replay plate instead.
        /// </summary>
        sealed class TimeTrialView
        {
            const float SplitPopScale = 1.3f;
            const int SplitPopMs = 340;
            const float TagFallbackHeight = 1.2f, TagLift = 0.25f, TagMinForward = 1f, TagOpacityGain = 2f;
            /// <summary>Seconds a tag shows the latest problem the ghost validation found.</summary>
            const float FlagShowSeconds = 4f;

            /// <summary>Name tag of one ghost: name, warning badge with the problem count and the latest problem.</summary>
            sealed class Tag
            {
                public GhostPlayer ghost;
                public VisualElement anchor, badge;
                public Label flagCount, flagText;
                public int shownFlags;
                public bool visible, flagShown;
            }

            readonly RaceHud hud;
            readonly List<Tag> tags = new();
            VisualElement topLeft, splitRow, ghostTags, replayPlate;
            Label splitCaption, splitTime, replayName;
            TimeTrialRun bound;
            bool ghostsShown;

            public TimeTrialView(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                topLeft = hud.Q<VisualElement>("top-left");
                splitRow = hud.Q<VisualElement>("split-row");
                splitCaption = hud.Q<Label>("split-caption");
                splitTime = hud.Q<Label>("split-time");
                ghostTags = hud.Q<VisualElement>("ghost-tags");
                replayPlate = hud.Q<VisualElement>("replay-plate");
                replayName = hud.Q<Label>("replay-name");
                ghostTags.Clear();
                tags.Clear();
                bound = null;
            }

            public void Unbind()
            {
                if (bound != null) bound.Split -= OnSplit;
                bound = null;
                tags.Clear();
            }

            /// <summary>Follows the race's time trial (binds when it changes) and moves the ghost name tags.</summary>
            public void Tick(TimeTrialRun tt)
            {
                if (tt != bound) BindRun(tt);
                if (tt != null && !ghostsShown && tt.GhostsLoaded) ShowGhosts(tt);
                if (tags.Count > 0) UpdateTags();
            }

            void BindRun(TimeTrialRun tt)
            {
                if (bound != null) bound.Split -= OnSplit;
                bound = tt;
                ghostsShown = false;
                ghostTags.Clear();
                tags.Clear();
                if (tt == null) return;
                tt.Split += OnSplit;
                topLeft.SetVisible(false);
                ghostTags.SetVisible(true);
                replayPlate.SetVisible(tt.IsReplay);
            }

            // The ghosts load after the HUD binds: the plate and tags come once they are all spawned.
            void ShowGhosts(TimeTrialRun tt)
            {
                ghostsShown = true;
                var spectated = tt.Spectated;
                replayPlate.SetVisible(spectated != null);
                if (spectated != null)
                {
                    var h = spectated.Data.header;
                    replayName.text = string.Format(hud.replayFormat, spectated.DisplayName, h.engineClassLabel, UiText.Clock(h.finishTime));
                }
                if (GameRoot.Instance.Config.ghostTagDistance <= 0f) return;
                foreach (var g in tt.Ghosts)
                    if (g != spectated) AddTag(g);
            }

            void AddTag(GhostPlayer ghost)
            {
                var h = ghost.Data.header;
                var time = UiText.Clock(h.finishTime);
                var text = ghost.Entry != null && ghost.Entry.kind == GhostKind.Best
                    ? string.Format(hud.ownBestTagFormat, time)
                    : string.Format(hud.ghostTagFormat, ghost.DisplayName, time);
                var tag = new Tag
                {
                    ghost = ghost,
                    anchor = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform },
                    badge = new VisualElement { pickingMode = PickingMode.Ignore },
                    flagCount = new Label { pickingMode = PickingMode.Ignore },
                    flagText = new Label { pickingMode = PickingMode.Ignore },
                };
                tag.anchor.AddToClassList("hud-ghost-anchor");
                tag.anchor.SetVisible(false);
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList("hud-ghost-box");
                tag.flagText.AddToClassList("hud-ghost-flag-text");
                tag.flagText.SetVisible(false);
                var plate = new VisualElement { pickingMode = PickingMode.Ignore };
                plate.AddToClassList("hud-ghost-tag");
                var name = new Label(text) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("hud-ghost-name");
                tag.badge.AddToClassList("hud-ghost-badge");
                tag.badge.SetVisible(false);
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("icon");
                icon.AddToClassList("i-warning");
                icon.AddToClassList("hud-ghost-badge-icon");
                tag.flagCount.AddToClassList("hud-ghost-badge-count");
                tag.badge.Add(icon);
                tag.badge.Add(tag.flagCount);
                plate.Add(name);
                plate.Add(tag.badge);
                box.Add(tag.flagText);
                box.Add(plate);
                tag.anchor.Add(box);
                ghostTags.Add(tag.anchor);
                tags.Add(tag);
            }

            void OnSplit(int lap, float delta)
            {
                if (float.IsNaN(delta)) return;
                splitRow.SetVisible(true);
                splitCaption.text = lap >= hud.race.Laps ? hud.splitFinishText : string.Format(hud.splitLapFormat, lap);
                var ahead = delta < 0f;
                splitTime.text = UiText.SignedSeconds(delta);
                splitTime.EnableInClassList("ahead", ahead);
                splitTime.EnableInClassList("behind", !ahead);
                UiAnim.Pop(splitRow, SplitPopScale, SplitPopMs);
            }

            // Tags of the ghosts shown in full (the nearest few) and the reference; any ghost while its latest problem
            // shows, at any distance. The others are on the minimap. Problems show to moderators only (like the server's flags).
            void UpdateTags()
            {
                var race = hud.race;
                var cam = race.RaceCamera;
                var range = GameRoot.Instance.Config.ghostTagDistance;
                var size = ghostTags.layout.size;
                var now = Time.unscaledTime;
                var moderator = OnlineService.IsModerator;
                var reference = bound != null ? bound.Reference : null;
                foreach (var tag in tags)
                {
                    var ghost = tag.ghost;
                    var top = Vector3.zero;
                    var flags = ghost != null && moderator ? ghost.FlagCount : 0;
                    var flagFresh = flags > 0 && now - ghost.LastFlagTime < FlagShowSeconds;
                    var visible = cam != null && ghost != null && ghost.isActiveAndEnabled && race.Phase != RacePhase.Results
                                  && (flagFresh || ghost.Detailed || ghost == reference)
                                  && TryTagPoint(ghost, cam, flagFresh ? float.PositiveInfinity : range, out top);
                    if (!visible)
                    {
                        if (tag.visible) tag.anchor.SetVisible(false);
                        tag.visible = false;
                        continue;
                    }
                    UpdateFlags(tag, flags, flagFresh);
                    // Viewport coordinates, not screen pixels: the race camera may render at a lower resolution than the
                    // screen (GraphicsQuality render scale), which CameraTransformWorldToPanel's pixel mapping ignores.
                    var v = cam.WorldToViewportPoint(top);
                    if (!tag.visible) tag.anchor.SetVisible(true);
                    tag.visible = true;
                    tag.anchor.style.translate = new Translate(v.x * size.x, (1f - v.y) * size.y);
                    tag.anchor.style.opacity = Mathf.Clamp01(ghost.Visual != null ? ghost.Visual.ShownOpacity * TagOpacityGain : 1f);
                }
            }

            static void UpdateFlags(Tag tag, int flags, bool flagFresh)
            {
                if (flags != tag.shownFlags)
                {
                    tag.shownFlags = flags;
                    tag.flagCount.text = flags.ToString();
                    tag.flagText.text = tag.ghost.LastFlag;
                    tag.badge.SetVisible(flags > 0);
                }
                if (flagFresh == tag.flagShown) return;
                tag.flagShown = flagFresh;
                tag.flagText.SetVisible(flagFresh);
            }

            static bool TryTagPoint(GhostPlayer ghost, Camera cam, float range, out Vector3 top)
            {
                var visual = ghost.Visual;
                top = visual != null && visual.TryGetDriverTop(out var local)
                    ? visual.Body.TransformPoint(local)
                    : ghost.transform.position + ghost.transform.up * TagFallbackHeight;
                top += Vector3.up * TagLift;
                var toTag = top - cam.transform.position;
                return Vector3.Dot(toTag, cam.transform.forward) > TagMinForward && toTag.sqrMagnitude < range * range;
            }
        }
    }
}
