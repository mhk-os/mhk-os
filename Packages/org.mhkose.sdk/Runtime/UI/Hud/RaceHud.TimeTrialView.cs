using System.Collections.Generic;
using MHK.Core;
using MHK.Ghosts;
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

            readonly RaceHud hud;
            readonly List<(GhostPlayer ghost, VisualElement anchor)> tags = new();
            VisualElement topLeft, splitRow, ghostTags, replayPlate;
            Label splitCaption, splitTime, replayName;
            TimeTrialRun bound;

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
                if (tags.Count > 0) UpdateTags();
            }

            void BindRun(TimeTrialRun tt)
            {
                if (bound != null) bound.Split -= OnSplit;
                bound = tt;
                ghostTags.Clear();
                tags.Clear();
                if (tt == null) return;
                tt.Split += OnSplit;
                topLeft.SetVisible(false);
                ghostTags.SetVisible(true);
                var spectated = tt.Spectated;
                replayPlate.SetVisible(spectated != null);
                if (spectated != null)
                {
                    var h = spectated.Data.header;
                    replayName.text = string.Format(hud.replayFormat, h.playerName, h.engineClassLabel, UiText.Clock(h.finishTime));
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
                    : string.Format(hud.ghostTagFormat, h.playerName, time);
                var anchor = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                anchor.AddToClassList("hud-ghost-anchor");
                anchor.SetVisible(false);
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("hud-ghost-tag");
                anchor.Add(label);
                ghostTags.Add(anchor);
                tags.Add((ghost, anchor));
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

            void UpdateTags()
            {
                var race = hud.race;
                var cam = race.RaceCamera;
                var range = GameRoot.Instance.Config.ghostTagDistance;
                var size = ghostTags.layout.size;
                foreach (var (ghost, anchor) in tags)
                {
                    var top = Vector3.zero;
                    var visible = cam != null && ghost != null && ghost.isActiveAndEnabled && race.Phase != RacePhase.Results
                                  && TryTagPoint(ghost, cam, range, out top);
                    if (!visible)
                    {
                        if (anchor.resolvedStyle.display != DisplayStyle.None) anchor.SetVisible(false);
                        continue;
                    }
                    // Viewport coordinates, not screen pixels: the race camera may render at a lower resolution than the
                    // screen (GraphicsQuality render scale), which CameraTransformWorldToPanel's pixel mapping ignores.
                    var v = cam.WorldToViewportPoint(top);
                    anchor.SetVisible(true);
                    anchor.style.translate = new Translate(v.x * size.x, (1f - v.y) * size.y);
                    anchor.style.opacity = Mathf.Clamp01(ghost.Visual != null ? ghost.Visual.ShownOpacity * TagOpacityGain : 1f);
                }
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
