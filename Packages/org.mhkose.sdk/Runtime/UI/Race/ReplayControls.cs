using System;
using System.Collections.Generic;
using System.Globalization;
using MHK.Ghosts;
using MHK.Karts;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Replay controls (Replay.uxml), open while a time-trial replay runs: seekbar with lap marks, play and pause, playback
    /// speeds, slow motion while held (its button or the fire key) and the camera view. They fade out without input.
    /// </summary>
    public class ReplayControls : RaceLayer
    {
        [SerializeField, Tooltip("Playback speeds of the speed buttons, in their order (1 = real time).")]
        float[] speeds = { 0.25f, 0.5f, 1f, 2f, 4f };
        [SerializeField, Tooltip("Index in speeds a replay starts with.")]
        int startSpeed = 2;
        [SerializeField, Tooltip("Playback speed while slow motion is held (its button, or the fire key or button).")]
        [Range(0.05f, 1f)] float slowMotionSpeed = 0.25f;
        [SerializeField, Tooltip("Seconds without input before the controls fade out.")]
        [Min(0.5f)] float hideDelay = 3f;
        [SerializeField, Tooltip("Replay seconds a full steering input moves per real second (seeking with keys or a gamepad).")]
        [Min(0f)] float scrubRate = 10f;
        [SerializeField, Tooltip("Steering input below this share does not seek.")]
        [Range(0f, 1f)] float scrubDeadZone = 0.3f;
        [SerializeField, Tooltip("Text of a speed button ({0} = speed).")]
        string speedFormat = "{0}x";
        [SerializeField, Tooltip("Tooltip of the play button while the replay runs.")]
        string pauseTooltip = "Pause";
        [SerializeField, Tooltip("Tooltip of the play button while the replay is paused.")]
        string playTooltip = "Play";

        const float MouseMoveThreshold = 0.25f;
        const int MaxClockMinutes = 99;

        readonly List<Button> speedButtons = new();
        readonly List<VisualElement> pickable = new();
        readonly HashSet<int> slowPointers = new();
        VisualElement root, bar, seek, fill, knob, playIcon, slow;
        Button play;
        Label elapsed;
        IDisposable anyButton;
        int speedIndex, shownSecond;
        bool playing, dragging, hovered, hidden, fireHeld, shownPlaying;
        float idle, pendingSeek, shownFraction;

        /// <summary>Speed the replay runs at now: 0 while paused or dragging the seekbar, the slow motion speed while held.</summary>
        public float EffectiveSpeed
        {
            get
            {
                if (dragging) return 0f;
                if (fireHeld || slowPointers.Count > 0) return slowMotionSpeed;
                return playing ? speeds[speedIndex] : 0f;
            }
        }

        protected override bool InsetBySafeArea => true;

        protected override void Bind()
        {
            root = Q<VisualElement>("replay");
            root.EnableInClassList("touch", Settings.TouchControlsActive);
            bar = Q<VisualElement>("bar");
            elapsed = Q<Label>("elapsed");
            ResetState();
            BindSeekBar();
            BindSpeeds();
            BindButtons();
            pickable.Clear();
            bar.Query<VisualElement>().ForEach(e =>
            {
                if (e.pickingMode == PickingMode.Position) pickable.Add(e);
            });
            anyButton = InputSystem.onAnyButtonPress.Call(_ => Wake());
            var tt = Race.TimeTrial;
            if (tt != null && tt.Spectated != null) BindRecording(tt);
            Race.PlaybackSpeed = EffectiveSpeed;
        }

        void ResetState()
        {
            speedIndex = Mathf.Clamp(startSpeed, 0, speeds.Length - 1);
            playing = true;
            dragging = hovered = hidden = fireHeld = false;
            shownPlaying = false;
            shownSecond = int.MinValue;
            shownFraction = -1f;
            pendingSeek = -1f;
            idle = 0f;
            slowPointers.Clear();
        }

        protected override void Unbind()
        {
            anyButton?.Dispose();
            anyButton = null;
            slowPointers.Clear();
            dragging = false;
            if (Race != null) Race.PlaybackSpeed = 1f;
        }

        void BindSeekBar()
        {
            seek = Q<VisualElement>("seek");
            fill = Q<VisualElement>("seek-fill");
            knob = Q<VisualElement>("seek-knob");
            seek.RegisterCallback<PointerDownEvent>(e =>
            {
                dragging = true;
                seek.CapturePointer(e.pointerId);
                SeekToPointer(e.localPosition.x);
            });
            seek.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (dragging && seek.HasPointerCapture(e.pointerId)) SeekToPointer(e.localPosition.x);
            });
            seek.RegisterCallback<PointerUpEvent>(e =>
            {
                if (seek.HasPointerCapture(e.pointerId)) seek.ReleasePointer(e.pointerId);
                dragging = false;
            });
            seek.RegisterCallback<PointerCaptureOutEvent>(_ => dragging = false);
            bar.RegisterCallback<PointerEnterEvent>(_ => hovered = true);
            bar.RegisterCallback<PointerLeaveEvent>(_ => hovered = false);
        }

        void SeekToPointer(float x)
        {
            var width = seek.layout.width;
            var tt = Race.TimeTrial;
            if (width <= 0f || tt == null) return;
            pendingSeek = Mathf.Clamp01(x / width) * tt.ReplayLength;
            Wake();
        }

        void BindSpeeds()
        {
            var group = Q<VisualElement>("speeds");
            group.Clear();
            speedButtons.Clear();
            for (var i = 0; i < speeds.Length; i++)
            {
                var index = i;
                var b = new Button(() => SelectSpeed(index))
                {
                    text = string.Format(CultureInfo.InvariantCulture, speedFormat, speeds[i]),
                    focusable = false,
                };
                b.AddToClassList("chip");
                b.AddToClassList("replay-speed");
                group.Add(b);
                speedButtons.Add(b);
            }
            SelectSpeed(speedIndex);
        }

        void SelectSpeed(int index)
        {
            speedIndex = index;
            for (var i = 0; i < speedButtons.Count; i++) speedButtons[i].EnableInClassList("selected", i == index);
        }

        void BindButtons()
        {
            play = Q<Button>("play");
            playIcon = Q<VisualElement>("play-icon");
            play.clicked += () => playing = !playing;
            Q<Button>("camera").clicked += () =>
            {
                if (Race.ChaseCamera != null) Race.ChaseCamera.CycleView();
            };
            slow = Q<VisualElement>("slow");
            slow.RegisterCallback<PointerDownEvent>(e =>
            {
                slowPointers.Add(e.pointerId);
                slow.CapturePointer(e.pointerId);
                slow.AddToClassList("pressed");
            });
            slow.RegisterCallback<PointerUpEvent>(e => ReleaseSlow(e.pointerId));
            slow.RegisterCallback<PointerCancelEvent>(e => ReleaseSlow(e.pointerId));
            slow.RegisterCallback<PointerCaptureOutEvent>(e => ReleaseSlow(e.pointerId));
        }

        void ReleaseSlow(int pointerId)
        {
            slowPointers.Remove(pointerId);
            if (slow.HasPointerCapture(pointerId)) slow.ReleasePointer(pointerId);
            if (slowPointers.Count == 0) slow.RemoveFromClassList("pressed");
        }

        // Total time and a mark at the end of each lap (the last one is the finish line, the recording runs on after it).
        void BindRecording(TimeTrialRun tt)
        {
            var length = tt.ReplayLength;
            var whole = Mathf.FloorToInt(length);
            Q<Label>("total").text = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", Mathf.Min(whole / 60, MaxClockMinutes), whole % 60);
            var marks = Q<VisualElement>("seek-marks");
            marks.Clear();
            var h = tt.Spectated.Data.header;
            if (length <= 0f) return;
            for (var lap = 1; lap <= h.lapTimes.Length; lap++)
            {
                var mark = new VisualElement { pickingMode = PickingMode.Ignore };
                mark.AddToClassList("replay-seek-mark");
                mark.style.left = Length.Percent(Mathf.Clamp01(h.LapEndTime(lap) / length) * 100f);
                marks.Add(mark);
            }
        }

        void Update()
        {
            if (!IsBound || root.panel == null || Race == null) return;
            var tt = Race.TimeTrial;
            if (tt == null || tt.Spectated == null) return;
            var dt = Time.unscaledDeltaTime;
            if (!Race.Paused) PollViewer(tt, dt);
            if (pendingSeek >= 0f)
            {
                tt.Seek(pendingSeek);
                pendingSeek = -1f;
            }
            var speed = EffectiveSpeed;
            if (!Mathf.Approximately(Race.PlaybackSpeed, speed)) Race.PlaybackSpeed = speed;
            Refresh(tt);
            TickHide(dt);
        }

        // The viewer's own keys: fire held is slow motion, steering seeks.
        void PollViewer(TimeTrialRun tt, float dt)
        {
            var viewer = Race.ChaseCamera != null ? Race.ChaseCamera.Viewer : null;
            fireHeld = false;
            if (viewer == null) return;
            var input = default(KartInput);
            viewer.UpdateInput(null, ref input);
            fireHeld = input.fire;
            if (Mathf.Abs(input.steer) < scrubDeadZone) return;
            Wake();
            var from = pendingSeek >= 0f ? pendingSeek : tt.Spectated.ShownTime;
            pendingSeek = Mathf.Clamp(from + input.steer * scrubRate * dt, 0f, tt.ReplayLength);
        }

        void Refresh(TimeTrialRun tt)
        {
            var length = tt.ReplayLength;
            var time = Mathf.Clamp(tt.Spectated.ShownTime, 0f, length);
            var f = length > 0f ? time / length : 0f;
            if (!Mathf.Approximately(f, shownFraction))
            {
                shownFraction = f;
                fill.style.width = Length.Percent(f * 100f);
                knob.style.left = Length.Percent(f * 100f);
            }
            var second = Mathf.FloorToInt(time);
            if (second != shownSecond)
            {
                shownSecond = second;
                WriteClock(elapsed, second);
            }
            if (playing == shownPlaying) return;
            shownPlaying = playing;
            playIcon.SetIcon(playing ? "pause" : "play_arrow");
            play.tooltip = playing ? pauseTooltip : playTooltip;
        }

        static void WriteClock(Label label, int second)
        {
            Span<char> text = stackalloc char[16];
            var n = UiText.WriteMinutesSeconds(text, Mathf.Min(second / 60, MaxClockMinutes), second % 60);
            label.SetText(text.Slice(0, n));
        }

        void TickHide(float dt)
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.delta.ReadValue().sqrMagnitude > MouseMoveThreshold) Wake();
            if (dragging || hovered || slowPointers.Count > 0) idle = 0f;
            else idle += dt;
            SetHidden(idle >= hideDelay);
        }

        void Wake() => idle = 0f;

        // Faded out the bar takes no pointer input, so a tap that brings it back presses nothing.
        void SetHidden(bool value)
        {
            if (value == hidden) return;
            hidden = value;
            bar.EnableInClassList("replay-hidden", value);
            foreach (var e in pickable) e.pickingMode = value ? PickingMode.Ignore : PickingMode.Position;
        }
    }
}
