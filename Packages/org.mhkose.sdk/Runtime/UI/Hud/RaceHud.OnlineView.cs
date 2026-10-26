using System.Collections.Generic;
using MHK.Core;
using MHK.Karts;
using MHK.Online;
using MHK.Race;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class RaceHud
    {
        /// <summary>
        /// Online races: name tags over the other racers (name, country, ping colour), the status line ('Waiting for
        /// players', waiting for the result after the finish, connection trouble) and, in a solo race someone wants to
        /// join, the non-blocking 'End race' prompt.
        /// </summary>
        sealed class OnlineView
        {
            const float TagFallbackHeight = 1.2f, TagLift = 0.25f, TagMinForward = 1f, TagOpacityGain = 2f;
            const float DefaultTagRange = 80f;
            const int GoodPingMs = 80, FairPingMs = 160;
            static readonly Color GoodPing = new(0.45f, 0.9f, 0.4f), FairPing = new(1f, 0.82f, 0.3f), BadPing = new(1f, 0.4f, 0.35f);
            static readonly Color UnknownPing = new(0.8f, 0.8f, 0.8f);

            sealed class Tag
            {
                public int slot;
                public Kart kart;
                public VisualElement anchor, dot;
                public bool visible;
                public int shownPing = -1;
            }

            readonly RaceHud hud;
            readonly List<Tag> tags = new();
            VisualElement container, column, statusPlate, prompt;
            Label statusLabel, troubleLabel, promptLabel;
            Button promptButton;
            OnlineRace bound;
            string shownStatus, shownPrompt;
            bool shownTrouble, promptEnding;

            public OnlineView(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                container = hud.Q<VisualElement>("ghost-tags");
                tags.Clear();
                bound = null;
                column?.RemoveFromHierarchy();
                column = null;
                shownStatus = shownPrompt = null;
                shownTrouble = promptEnding = false;
            }

            public void Unbind()
            {
                tags.Clear();
                column?.RemoveFromHierarchy();
                column = null;
                bound = null;
            }

            public void Tick()
            {
                var race = hud.race;
                var online = race.Online;
                if (online == null)
                {
                    if (column != null) column.SetVisible(false);
                    return;
                }
                if (online != bound) BindRace(online);
                UpdateTags(race, online);
                UpdateStatus(race, online);
                UpdatePrompt(online);
            }

            void BindRace(OnlineRace online)
            {
                bound = online;
                tags.Clear();
                container.Clear();
                container.SetVisible(true);
                BuildColumn();
            }

            // Bottom centre: connection warning, status line, solo prompt (the only pickable piece).
            void BuildColumn()
            {
                column?.RemoveFromHierarchy();
                column = new VisualElement { name = "online-status", pickingMode = PickingMode.Ignore };
                var st = column.style;
                st.position = Position.Absolute;
                st.left = 0;
                st.right = 0;
                st.bottom = Length.Percent(16);
                st.alignItems = Align.Center;
                troubleLabel = Plate(column, hud.connectionTroubleText, true, out _);
                troubleLabel.parent.SetVisible(false);
                statusLabel = Plate(column, "", false, out statusPlate);
                statusPlate.SetVisible(false);
                prompt = new VisualElement { pickingMode = PickingMode.Ignore };
                prompt.AddToClassList("hud-flash");
                var ps = prompt.style;
                ps.flexDirection = FlexDirection.Row;
                ps.alignItems = Align.Center;
                ps.paddingTop = 6;
                ps.paddingBottom = 8;
                ps.paddingLeft = 24;
                ps.paddingRight = 12;
                ps.marginTop = 6;
                promptLabel = new Label { pickingMode = PickingMode.Ignore };
                promptLabel.AddToClassList("hud-flash-label");
                promptLabel.style.fontSize = 24;
                promptLabel.style.marginRight = 16;
                promptButton = new Button { focusable = false };
                promptButton.AddToClassList("mhk-button");
                var buttonLabel = new Label(hud.endRaceText) { pickingMode = PickingMode.Ignore };
                buttonLabel.AddToClassList("button-label");
                promptButton.Add(buttonLabel);
                promptButton.clicked += OnEndRace;
                prompt.Add(promptLabel);
                prompt.Add(promptButton);
                prompt.SetVisible(false);
                column.Add(prompt);
                hud.Root.Add(column);
            }

            static Label Plate(VisualElement parent, string text, bool bad, out VisualElement plate)
            {
                plate = new VisualElement { pickingMode = PickingMode.Ignore };
                plate.AddToClassList("hud-flash");
                if (bad) plate.AddToClassList("bad");
                var ps = plate.style;
                ps.paddingTop = 4;
                ps.paddingBottom = 8;
                ps.paddingLeft = 24;
                ps.paddingRight = 24;
                ps.marginTop = 6;
                var label = new Label(text) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("hud-flash-label");
                label.style.fontSize = 24;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                plate.Add(label);
                parent.Add(plate);
                return label;
            }

            void OnEndRace()
            {
                if (bound == null) return;
                bound.EndSoloRaceEarly();
            }

            void UpdateStatus(RaceManager race, OnlineRace online)
            {
                if (column.panel == null) BuildColumn();
                column.SetVisible(race.Phase != RacePhase.Results);
                var trouble = online.ConnectionTrouble && race.Phase != RacePhase.Results;
                if (trouble != shownTrouble)
                {
                    shownTrouble = trouble;
                    troubleLabel.parent.SetVisible(trouble);
                }
                var player = race.LocalPlayer;
                string status = null;
                if (race.WaitingForPlayers) status = hud.waitingForPlayersText;
                else if (race.Phase == RacePhase.Racing && player != null && (player.Progress.Finished || online.LocalLeft) && !online.Solo)
                    status = hud.waitingForResultText;
                if (status == shownStatus) return;
                shownStatus = status;
                statusPlate.SetVisible(status != null);
                if (status != null) statusLabel.text = status;
            }

            void UpdatePrompt(OnlineRace online)
            {
                var text = online.Solo && online.JoinWaitingUser != null && online.Results == null
                    ? string.Format(hud.joinWaitingFormat, online.JoinWaitingUser)
                    : null;
                var ending = online.EndRequested;
                if (text == shownPrompt && ending == promptEnding) return;
                shownPrompt = text;
                promptEnding = ending;
                prompt.SetVisible(text != null);
                if (text == null) return;
                promptLabel.text = ending ? hud.endingRaceText : text;
                promptButton.SetVisible(!ending);
            }

            void UpdateTags(RaceManager race, OnlineRace online)
            {
                var karts = race.Karts;
                for (var i = 0; i < karts.Count; i++)
                {
                    var k = karts[i];
                    if (k == null || !k.IsRemote || HasTag(k)) continue;
                    AddTag(k, online.SlotOf(k));
                }
                var cam = race.RaceCamera;
                var range = GameRoot.Instance.Config.ghostTagDistance > 0f ? GameRoot.Instance.Config.ghostTagDistance : DefaultTagRange;
                var size = container.layout.size;
                for (var i = tags.Count - 1; i >= 0; i--)
                {
                    var tag = tags[i];
                    if (tag.kart == null)
                    {
                        tag.anchor.RemoveFromHierarchy();
                        tags.RemoveAt(i);
                        continue;
                    }
                    var top = Vector3.zero;
                    if (cam == null || race.Phase == RacePhase.Results || !TryTagPoint(tag.kart, cam, range, out top))
                    {
                        if (tag.visible) tag.anchor.SetVisible(false);
                        tag.visible = false;
                        continue;
                    }
                    var v = cam.WorldToViewportPoint(top);
                    if (!tag.visible) tag.anchor.SetVisible(true);
                    tag.visible = true;
                    tag.anchor.style.translate = new Translate(v.x * size.x, (1f - v.y) * size.y);
                    tag.anchor.style.opacity = Mathf.Clamp01(tag.kart.Visual != null ? tag.kart.Visual.ShownOpacity * TagOpacityGain : 1f);
                    var ping = online.RttMs(tag.slot);
                    if (ping != tag.shownPing)
                    {
                        tag.shownPing = ping;
                        tag.dot.style.backgroundColor = ping <= 0 ? UnknownPing : ping <= GoodPingMs ? GoodPing : ping <= FairPingMs ? FairPing : BadPing;
                        tag.dot.tooltip = ping > 0 ? $"{ping} ms" : "";
                    }
                }
            }

            bool HasTag(Kart k)
            {
                for (var i = 0; i < tags.Count; i++)
                    if (tags[i].kart == k) return true;
                return false;
            }

            void AddTag(Kart kart, int slot)
            {
                var setup = kart.Setup;
                var country = setup != null && !string.IsNullOrEmpty(setup.country) ? setup.country.ToUpperInvariant() : "";
                var tag = new Tag
                {
                    slot = slot,
                    kart = kart,
                    anchor = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform },
                    dot = new VisualElement { pickingMode = PickingMode.Ignore },
                };
                tag.anchor.AddToClassList("hud-ghost-anchor");
                tag.anchor.SetVisible(false);
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList("hud-ghost-box");
                var plate = new VisualElement { pickingMode = PickingMode.Ignore };
                plate.AddToClassList("hud-ghost-tag");
                plate.style.flexDirection = FlexDirection.Row;
                plate.style.alignItems = Align.Center;
                var ds = tag.dot.style;
                ds.width = ds.height = 10;
                ds.borderTopLeftRadius = ds.borderTopRightRadius = ds.borderBottomLeftRadius = ds.borderBottomRightRadius = 5;
                ds.marginRight = 6;
                ds.backgroundColor = UnknownPing;
                var name = new Label(country.Length > 0 ? $"{kart.DisplayName}  {country}" : kart.DisplayName) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("hud-ghost-name");
                plate.Add(tag.dot);
                plate.Add(name);
                box.Add(plate);
                tag.anchor.Add(box);
                container.Add(tag.anchor);
                tags.Add(tag);
            }

            static bool TryTagPoint(Kart kart, Camera cam, float range, out Vector3 top)
            {
                var visual = kart.Visual;
                top = visual != null && visual.TryGetDriverTop(out var local)
                    ? visual.Body.TransformPoint(local)
                    : kart.transform.position + kart.transform.up * TagFallbackHeight;
                top += Vector3.up * TagLift;
                var toTag = top - cam.transform.position;
                return Vector3.Dot(toTag, cam.transform.forward) > TagMinForward && toTag.sqrMagnitude < range * range;
            }
        }
    }
}
