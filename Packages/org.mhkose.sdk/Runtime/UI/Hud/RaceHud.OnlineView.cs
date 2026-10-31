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
        /// Online races (MHKHud.uss, online section): name tags over the other racers (name, country, ping bars), racer
        /// chips while the race waits for everybody to load and after the player's finish (who is done), a connection
        /// trouble icon, and in a solo race someone wants to join, a toast with End race. Explanations are tooltips.
        /// </summary>
        sealed class OnlineView
        {
            const float TagFallbackHeight = 1.2f, TagLift = 0.25f, TagMinForward = 1f, TagOpacityGain = 2f;
            const float DefaultTagRange = 80f;
            const int GoodPingMs = 80, FairPingMs = 160;
            static readonly string[] PingClasses = { "good", "fair", "bad" };
            static readonly string[] PingIcons = { "i-signal_cellular_alt", "i-signal_cellular_alt_2_bar", "i-signal_cellular_alt_1_bar" };

            sealed class Tag
            {
                public int slot;
                public Kart kart;
                public VisualElement anchor, ping;
                public bool visible;
                public int shownPing = -1;
            }

            /// <summary>What the racer chips show: nothing, who has loaded, who has finished.</summary>
            enum ChipMode { None, Loading, Finishing }

            readonly RaceHud hud;
            readonly List<Tag> tags = new();
            readonly List<VisualElement> chips = new();
            // Grid slot of each chip (human racers only: AI karts load and finish on every client).
            readonly List<int> chipSlots = new();
            // Per chip: -1 not shown yet in this mode, 0 waiting, 1 done.
            readonly List<int> chipDone = new();
            VisualElement container, layer, chipRow, trouble, toast;
            Label toastName, toastButtonLabel;
            Button toastButton;
            OnlineRace bound;
            ChipMode shownMode;
            bool shownTrouble, toastShown, toastEnding;
            string shownToast;
            float troublePulse;

            public OnlineView(RaceHud hud) => this.hud = hud;

            public void Bind()
            {
                container = hud.Q<VisualElement>("ghost-tags");
                tags.Clear();
                bound = null;
                layer?.RemoveFromHierarchy();
                layer = null;
                // Tags follow this frame's camera: placed after the camera's LateUpdate.
                if (!hud.TryGetComponent<HudLatePass>(out var late)) late = hud.gameObject.AddComponent<HudLatePass>();
                late.Pass = PlaceTags;
            }

            void PlaceTags()
            {
                var race = hud.race;
                if (bound == null || race == null || race.Online != bound || layer == null) return;
                UpdateTags(race, bound, race.Phase == RacePhase.Results);
            }

            public void Unbind()
            {
                tags.Clear();
                layer?.RemoveFromHierarchy();
                layer = null;
                bound = null;
            }

            public void Tick(float dt)
            {
                var race = hud.race;
                var online = race.Online;
                if (online == null)
                {
                    if (layer != null) layer.SetVisible(false);
                    return;
                }
                if (online != bound || layer == null || layer.panel == null) BindRace(online);
                var results = race.Phase == RacePhase.Results;
                layer.SetVisible(!results);
                if (results) return;
                UpdateChips(race, online);
                UpdateTrouble(online, dt);
                UpdateToast(online);
            }

            void BindRace(OnlineRace online)
            {
                bound = online;
                tags.Clear();
                container.Clear();
                container.SetVisible(true);
                layer?.RemoveFromHierarchy();
                layer = new VisualElement { name = "online-hud", pickingMode = PickingMode.Ignore };
                layer.AddToClassList("bleed");
                layer.style.position = Position.Absolute;
                layer.style.left = layer.style.right = layer.style.top = layer.style.bottom = 0;
                BuildChips(online);
                BuildTrouble();
                BuildToast();
                hud.Root.Add(layer);
                shownMode = ChipMode.None;
                shownTrouble = toastShown = toastEnding = false;
                shownToast = null;
            }

            // One chip per human racer of the grid (the player's own outlined), the state icon says loaded / finished.
            void BuildChips(OnlineRace online)
            {
                var column = new VisualElement { pickingMode = PickingMode.Ignore };
                column.AddToClassList("hud-net-column");
                chipRow = new VisualElement();
                chipRow.AddToClassList(InfoChips.ContainerClass);
                chipRow.AddToClassList("hud-net-waiting");
                chips.Clear();
                chipSlots.Clear();
                chipDone.Clear();
                foreach (var slot in online.Setup.grid)
                {
                    if (slot.ai) continue;
                    var chip = InfoChips.Add(chipRow, "hourglass_top", slot.username, null, InfoChipState.Off);
                    chip.AddToClassList("dark");
                    chip.EnableInClassList("me", slot.slot == online.LocalSlot);
                    chips.Add(chip);
                    chipSlots.Add(slot.slot);
                    chipDone.Add(-1);
                }
                chipRow.SetVisible(false);
                column.Add(chipRow);
                layer.Add(column);
            }

            void BuildTrouble()
            {
                trouble = new VisualElement { tooltip = hud.connectionTroubleText };
                trouble.AddToClassList("hud-net-trouble");
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("icon");
                icon.AddToClassList("i-wifi_off");
                icon.AddToClassList("hud-net-trouble-icon");
                trouble.Add(icon);
                trouble.SetVisible(false);
                layer.Add(trouble);
            }

            // Not focusable from the race (driving keys would move UI focus): keyboard and gamepad reach End race
            // through the pause menu, which offers it while the toast shows.
            void BuildToast()
            {
                var anchor = new VisualElement { pickingMode = PickingMode.Ignore };
                anchor.AddToClassList("hud-net-toast-anchor");
                toast = new VisualElement();
                toast.AddToClassList("hud-net-toast");
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("icon");
                icon.AddToClassList("i-person_add");
                icon.AddToClassList("hud-net-toast-icon");
                toastName = new Label { pickingMode = PickingMode.Ignore };
                toastName.AddToClassList("hud-net-toast-name");
                toastButton = new Button { focusable = false };
                toastButton.AddToClassList("mhk-button");
                toastButton.AddToClassList("danger");
                toastButton.AddToClassList("hud-net-toast-button");
                var buttonIcon = new VisualElement { pickingMode = PickingMode.Ignore };
                buttonIcon.AddToClassList("icon");
                buttonIcon.AddToClassList("i-sports_score");
                toastButtonLabel = new Label(hud.endRaceText) { pickingMode = PickingMode.Ignore };
                toastButtonLabel.AddToClassList("button-label");
                toastButton.Add(buttonIcon);
                toastButton.Add(toastButtonLabel);
                toastButton.clicked += () => bound?.EndSoloRaceEarly();
                toast.Add(icon);
                toast.Add(toastName);
                toast.Add(toastButton);
                toast.AddToClassList("hidden");
                toast.SetVisible(false);
                anchor.Add(toast);
                layer.Add(anchor);
            }

            void UpdateChips(RaceManager race, OnlineRace online)
            {
                var player = race.LocalPlayer;
                var mode = race.WaitingForPlayers ? ChipMode.Loading
                    : race.Phase == RacePhase.Racing && player != null && (player.Progress.Finished || online.LocalLeft) ? ChipMode.Finishing
                    : ChipMode.None;
                if (mode != shownMode)
                {
                    shownMode = mode;
                    chipRow.SetVisible(mode != ChipMode.None);
                    chipRow.tooltip = mode == ChipMode.Loading ? hud.waitingForPlayersText : hud.waitingForResultText;
                    for (var i = 0; i < chipDone.Count; i++) chipDone[i] = -1;
                }
                if (mode == ChipMode.None) return;
                for (var i = 0; i < chips.Count; i++)
                {
                    var slot = chipSlots[i];
                    var left = online.HasLeft(slot);
                    bool done;
                    if (mode == ChipMode.Loading) done = online.IsLoaded(slot);
                    else
                    {
                        var kart = online.KartOf(slot);
                        done = left || (kart != null && (kart.Progress.Finished || kart.Eliminated));
                    }
                    chips[i].SetVisible(!left || mode == ChipMode.Finishing);
                    var state = done ? 1 : 0;
                    if (state == chipDone[i]) continue;
                    chipDone[i] = state;
                    var chip = chips[i];
                    chip.EnableInClassList("good", done);
                    chip.EnableInClassList("off", !done);
                    chip.Q<VisualElement>(className: "info-chip-icon").SetIcon(!done ? "hourglass_top" : mode == ChipMode.Loading ? "check_circle" : "sports_score");
                }
            }

            void UpdateTrouble(OnlineRace online, float dt)
            {
                var show = online.ConnectionTrouble;
                if (show != shownTrouble)
                {
                    shownTrouble = show;
                    trouble.SetVisible(show);
                    troublePulse = 0f;
                }
                if (!show) return;
                troublePulse += dt;
                trouble.EnableInClassList("dim", Mathf.Repeat(troublePulse, 1f) > 0.5f);
            }

            void UpdateToast(OnlineRace online)
            {
                var who = online.Solo && online.Results == null ? online.JoinWaitingUser : null;
                var ending = online.EndRequested;
                if (who == shownToast && ending == toastEnding) return;
                shownToast = who;
                toastEnding = ending;
                var show = who != null;
                if (show != toastShown)
                {
                    toastShown = show;
                    if (show)
                    {
                        toast.SetVisible(true);
                        toast.schedule.Execute(() => toast.RemoveFromClassList("hidden")).ExecuteLater(UiAnim.FrameMs);
                    }
                    else
                    {
                        toast.AddToClassList("hidden");
                        toast.schedule.Execute(() => { if (!toastShown) toast.SetVisible(false); }).ExecuteLater(300);
                    }
                }
                if (!show) return;
                toastName.text = who;
                toast.tooltip = string.Format(hud.joinWaitingFormat, who);
                toastButtonLabel.text = ending ? hud.endingRaceText : hud.endRaceText;
                toastButton.SetEnabled(!ending);
            }

            void UpdateTags(RaceManager race, OnlineRace online, bool results)
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
                    if (cam == null || results || !TryTagPoint(tag.kart, cam, range, out top))
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
                    var ping = online.RttKnown(tag.slot) ? online.RttMs(tag.slot) : -1;
                    if (ping == tag.shownPing) continue;
                    tag.shownPing = ping;
                    var level = ping < 0 ? -1 : ping <= GoodPingMs ? 0 : ping <= FairPingMs ? 1 : 2;
                    tag.ping.SetExclusiveClass(level < 0 ? 2 : level, PingIcons);
                    tag.ping.SetExclusiveClass(level, PingClasses);
                    tag.ping.tooltip = ping >= 0 ? $"{ping} ms" : "";
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
                var tag = new Tag
                {
                    slot = slot,
                    kart = kart,
                    anchor = new VisualElement { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform },
                    ping = new VisualElement { pickingMode = PickingMode.Ignore },
                };
                tag.anchor.AddToClassList("hud-ghost-anchor");
                tag.anchor.SetVisible(false);
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList("hud-ghost-box");
                var plate = new VisualElement { pickingMode = PickingMode.Ignore };
                plate.AddToClassList("hud-net-tag");
                var name = new Label(kart.DisplayName) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("hud-net-name");
                plate.Add(name);
                if (setup != null && !string.IsNullOrEmpty(setup.country))
                {
                    var country = new Label(setup.country.ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
                    country.AddToClassList("hud-net-country");
                    plate.Add(country);
                }
                // The racer's device (desktop or phone), nothing when unknown.
                var platform = OnlineUi.PlatformIcon(setup != null ? setup.platform : 0);
                if (platform.icon != null)
                {
                    var device = new VisualElement { pickingMode = PickingMode.Ignore, tooltip = platform.name };
                    device.AddToClassList("icon");
                    device.AddToClassList("hud-net-platform");
                    device.AddToClassList("i-" + platform.icon);
                    plate.Add(device);
                }
                tag.ping.AddToClassList("icon");
                tag.ping.AddToClassList("hud-net-ping");
                tag.ping.AddToClassList(PingIcons[2]);
                plate.Add(tag.ping);
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
