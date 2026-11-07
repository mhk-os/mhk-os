using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using MHK.Cameras;
using MHK.Core;
using MHK.Race;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Photo mode (Photo.uxml), opened by the pause menu's Screenshot button while the race stays paused. The HUD and every
    /// other race UI screen (siblings of this GameObject) are hidden, and the race camera flies freely through the frozen
    /// race (<see cref="PhotoCamera"/>, unscaled time). Keyboard: WASD or arrows move, Q/E or Ctrl/Space go down/up, Shift
    /// is faster. Mouse: dragging with any button looks, the wheel changes the speed. Gamepad: left stick moves, right
    /// stick looks, triggers go down/up, the left stick button is faster, the shoulder buttons change the speed. Touch: one
    /// finger looks, two fingers slide, a pinch moves forward or back. A click or tap that does not drag, Enter or the
    /// gamepad's A takes a photo of the camera view without UI at screen resolution, saved as PNG in
    /// <see cref="DataPaths.Screenshots"/> (encoded and written off the main thread). The preview then offers Back (more
    /// photos), Open and Open folder (desktop only) and Delete. Escape, the gamepad's B, the Pause key or the close button go
    /// back one step: from the preview to flying, from flying to the pause menu, with the race camera as it was.
    /// </summary>
    public class PhotoMode : RaceLayer
    {
        [Header("Flying")]
        [SerializeField, Tooltip("Flying speed (m/s) at speed 1x.")]
        [Min(0.1f)] float moveSpeed = 8f;
        [SerializeField, Tooltip("Speed factor while Shift (or the gamepad's left stick button) is held.")]
        [Min(1f)] float fastFactor = 4f;
        [SerializeField, Tooltip("Factor one mouse wheel notch or gamepad shoulder button press changes the speed by.")]
        [Min(1.01f)] float speedStep = 1.25f;
        [SerializeField, Tooltip("Slowest (x) and fastest (y) speed the wheel and the shoulder buttons reach (1 = moveSpeed).")]
        Vector2 speedRange = new(0.125f, 8f);
        [SerializeField, Tooltip("Degrees the view turns per UI pixel of a mouse drag.")]
        [Min(0f)] float mouseLook = 0.15f;
        [SerializeField, Tooltip("Degrees the view turns per UI pixel of a one-finger drag.")]
        [Min(0f)] float touchLook = 0.2f;
        [SerializeField, Tooltip("Degrees per second the view turns with the gamepad's right stick fully pushed.")]
        [Min(0f)] float padLook = 120f;
        [SerializeField, Tooltip("Metres the camera slides per UI pixel of a two-finger drag at speed 1x.")]
        [Min(0f)] float touchSlide = 0.02f;
        [SerializeField, Tooltip("Metres the camera moves forward per UI pixel the fingers of a pinch spread at speed 1x.")]
        [Min(0f)] float pinchDolly = 0.04f;
        [SerializeField, Tooltip("UI pixels a click or tap may move and still take a photo; further, it is a drag.")]
        [Min(0f)] float tapSlop = 12f;

        [Header("Preview")]
        [SerializeField, Tooltip("Largest size (UI pixels) of the photo in the preview, its aspect is kept.")]
        Vector2 previewSize = new(1024f, 576f);
        [SerializeField, Tooltip("Speed shown in the hint bar ({0} = factor, e.g. 1.5).")]
        string speedFormat = "Speed {0}x";
        [SerializeField, Tooltip("Preview file line while the photo is written.")]
        string savingText = "Saving...";
        [SerializeField, Tooltip("Preview file line once the photo is written ({0} = file name).")]
        string savedFormat = "Saved as {0}";
        [SerializeField, Tooltip("Preview file line when the photo could not be written.")]
        string saveFailedText = "The photo could not be saved.";
        [SerializeField, Tooltip("Question before deleting the photo.")]
        string deleteQuestion = "Delete this photo?";
        [SerializeField, Tooltip("Confirm button of the delete question.")]
        string deleteConfirm = "Delete";

        enum Hints { Keys, Pad, Touch }

        const int NoPointer = int.MinValue;
        // Longest frame step a move takes, so a hitch does not throw the camera far.
        const float MaxStep = 0.1f;
        // Squared stick / summed trigger input that counts as using the gamepad (hint bar).
        const float PadActive = 0.04f;

        readonly Dictionary<int, Vector2> pointers = new();
        readonly List<VisualElement> hiddenScreens = new();
        VisualElement root, surface, preview, previewColumn, previewImage, flash, hintsKeys, hintsPad, hintsTouch;
        Label speedLabel, fileLabel;
        Button backButton, openButton, folderButton, deleteButton;
        PhotoCamera rig;
        Texture2D shot;
        string shotPath, savingPath;
        Task<bool> saving;
        float speedScale;
        int tapPointer;
        Vector2 tapStart;
        Hints hints;
        bool previewOpen, promptOpen;
        // The frame of the last step (open, shot, back, close): the other inputs of that frame (Escape is also the Pause
        // key and the UI cancel, A also the UI submit) take no further step.
        int stepFrame = -1;

        /// <summary>The photo mode closed (back to the pause menu, the race camera restored).</summary>
        public event Action Closed;

        protected override bool InsetBySafeArea => true;

        /// <summary>Opens the photo mode over the paused <paramref name="race"/>.</summary>
        public void Open(RaceManager race, RaceHud hud)
        {
            Race = race;
            Hud = hud;
            gameObject.SetActive(true);
        }

        /// <summary>Closes the photo mode: the race camera and the hidden screens come back as they were.</summary>
        public void Close()
        {
            if (!IsBound) return;
            stepFrame = Time.frameCount;
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        /// <summary>
        /// The Pause key, while the photo mode is open or closed this frame: one step back (a delete question, the preview,
        /// the photo mode) and true. False while it is closed: the key is the pause menu's.
        /// </summary>
        public bool TakePauseKey()
        {
            if (stepFrame == Time.frameCount) return true;
            if (!IsBound) return false;
            if (promptOpen) CancelPrompt();
            else StepBack();
            return true;
        }

        protected override void Bind()
        {
            root = Q<VisualElement>("photo");
            root.EnableInClassList("touch", Settings.TouchControlsActive);
            BindSurface();
            Q<Button>("close").clicked += Close;
            hintsKeys = Q<VisualElement>("hints-keys");
            hintsPad = Q<VisualElement>("hints-pad");
            hintsTouch = Q<VisualElement>("hints-touch");
            speedLabel = Q<Label>("speed");
            flash = Q<VisualElement>("flash");
            BindPreview();
            Root.RegisterCallback<NavigationCancelEvent>(OnCancel);

            HideOtherScreens();
            rig = new PhotoCamera(Race.RaceCamera, Race.ChaseCamera);
            speedScale = 1f;
            RefreshSpeed();
            var pad = Gamepad.current;
            // No group shown yet, so the first one is applied whichever it is.
            hints = (Hints)(-1);
            ShowHints(Settings.TouchControlsActive ? Hints.Touch : pad != null && pad.buttonSouth.isPressed ? Hints.Pad : Hints.Keys);
            pointers.Clear();
            tapPointer = NoPointer;
            previewOpen = promptOpen = false;
            // The press that opened the photo mode (A, Enter) takes no photo.
            stepFrame = Time.frameCount;
        }

        // No element keeps the focus while flying: the move keys and sticks are also UI navigation, A and Enter the submit.
        protected override void AfterBind() => ClearFocus();

        void BindSurface()
        {
            surface = Q<VisualElement>("surface");
            surface.RegisterCallback<PointerDownEvent>(OnPointerDown);
            surface.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            surface.RegisterCallback<PointerUpEvent>(OnPointerUp);
            surface.RegisterCallback<PointerCancelEvent>(e => DropPointer(e.pointerId));
            surface.RegisterCallback<PointerCaptureOutEvent>(e => DropPointer(e.pointerId));
            surface.RegisterCallback<WheelEvent>(OnWheel);
        }

        void BindPreview()
        {
            preview = Q<VisualElement>("preview");
            preview.SetVisible(false);
            previewColumn = Q<VisualElement>("preview-column");
            previewImage = Q<VisualElement>("preview-image");
            fileLabel = Q<Label>("preview-file");
            backButton = Q<Button>("back");
            // A, Enter: the press that took the photo (and focused Back) is no click on Back.
            backButton.clicked += () =>
            {
                if (stepFrame != Time.frameCount) ClosePreview();
            };
            openButton = Q<Button>("open");
            openButton.clicked += () => OpenFile(shotPath);
            folderButton = Q<Button>("open-folder");
            folderButton.clicked += () => OpenFile(DataPaths.Screenshots);
            // Desktop only: a phone has no file browser to hand a file:// address to.
            var desktop = !Application.isMobilePlatform;
            openButton.SetVisible(desktop);
            folderButton.SetVisible(desktop);
            deleteButton = Q<Button>("delete");
            deleteButton.clicked += AskDelete;
        }

        protected override void Unbind()
        {
            FreeShot();
            rig?.Release();
            rig = null;
            foreach (var e in hiddenScreens) e.style.display = StyleKeyword.Null;
            hiddenScreens.Clear();
            pointers.Clear();
            tapPointer = NoPointer;
            previewOpen = promptOpen = false;
        }

        // The HUD, the pause menu and the other race layers: every bound screen next to this one.
        void HideOtherScreens()
        {
            hiddenScreens.Clear();
            var layers = transform.parent;
            if (layers == null) return;
            foreach (var screen in layers.GetComponentsInChildren<UiScreen>())
            {
                if (screen == this || !screen.IsBound) continue;
                screen.Root.style.display = DisplayStyle.None;
                hiddenScreens.Add(screen.Root);
            }
        }

        void ClearFocus() => Root?.focusController?.focusedElement?.Blur();

        void Update()
        {
            if (!IsBound) return;
            PollSaving();
            if (previewOpen) return;
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            TrackHints(kb, pad);
            if (stepFrame != Time.frameCount)
            {
                if (Pressed(kb?.escapeKey) || Pressed(pad?.buttonEast))
                {
                    Close();
                    return;
                }
                if (Pressed(kb?.enterKey) || Pressed(kb?.numpadEnterKey) || Pressed(pad?.buttonSouth))
                {
                    TakePhoto();
                    return;
                }
            }
            Fly(kb, pad, Mathf.Min(Time.unscaledDeltaTime, MaxStep));
        }

        static bool Pressed(ButtonControl button) => button != null && button.wasPressedThisFrame;

        static float Held(ButtonControl a, ButtonControl b) => a.isPressed || b.isPressed ? 1f : 0f;

        void Fly(Keyboard kb, Gamepad pad, float dt)
        {
            var move = Vector2.zero;
            var rise = 0f;
            var fast = false;
            if (kb != null)
            {
                move.x += Held(kb.dKey, kb.rightArrowKey) - Held(kb.aKey, kb.leftArrowKey);
                move.y += Held(kb.wKey, kb.upArrowKey) - Held(kb.sKey, kb.downArrowKey);
                rise += Held(kb.eKey, kb.spaceKey) - Held(kb.qKey, kb.ctrlKey);
                fast |= kb.shiftKey.isPressed;
            }
            if (pad != null)
            {
                move += pad.leftStick.ReadValue();
                rise += pad.rightTrigger.ReadValue() - pad.leftTrigger.ReadValue();
                fast |= pad.leftStickButton.isPressed;
                var look = pad.rightStick.ReadValue() * (padLook * dt);
                rig.Look(look.x, -look.y);
                if (pad.rightShoulder.wasPressedThisFrame) ChangeSpeed(1);
                if (pad.leftShoulder.wasPressedThisFrame) ChangeSpeed(-1);
            }
            move = Vector2.ClampMagnitude(move, 1f);
            rise = Mathf.Clamp(rise, -1f, 1f);
            if (move == Vector2.zero && rise == 0f) return;
            var t = rig.Transform;
            var step = moveSpeed * speedScale * (fast ? fastFactor : 1f) * dt;
            rig.Move((t.right * move.x + t.forward * move.y + Vector3.up * rise) * step);
        }

        void ChangeSpeed(int steps)
        {
            speedScale = Mathf.Clamp(speedScale * Mathf.Pow(speedStep, steps), speedRange.x, speedRange.y);
            RefreshSpeed();
        }

        void RefreshSpeed() => speedLabel.text = string.Format(speedFormat, speedScale.ToString("0.##", CultureInfo.InvariantCulture));

        // The hint bar follows the device used last (pointers in the pointer handlers).
        void TrackHints(Keyboard kb, Gamepad pad)
        {
            if (pad != null && (pad.leftStick.ReadValue().sqrMagnitude > PadActive || pad.rightStick.ReadValue().sqrMagnitude > PadActive ||
                                pad.leftTrigger.ReadValue() + pad.rightTrigger.ReadValue() > PadActive ||
                                pad.leftShoulder.isPressed || pad.rightShoulder.isPressed || pad.buttonSouth.isPressed))
                ShowHints(Hints.Pad);
            else if (kb != null && kb.anyKey.wasPressedThisFrame) ShowHints(Hints.Keys);
        }

        void ShowHints(Hints shown)
        {
            if (hints == shown) return;
            hints = shown;
            hintsKeys.SetVisible(shown == Hints.Keys);
            hintsPad.SetVisible(shown == Hints.Pad);
            hintsTouch.SetVisible(shown == Hints.Touch);
        }

        void OnPointerDown(PointerDownEvent e)
        {
            var touch = e.pointerType == UnityEngine.UIElements.PointerType.touch;
            ShowHints(touch ? Hints.Touch : Hints.Keys);
            pointers[e.pointerId] = e.position;
            surface.CapturePointer(e.pointerId);
            // A tap is one finger or the left mouse button alone; a second finger makes it a gesture.
            if (pointers.Count == 1 && (touch || e.button == 0))
            {
                tapPointer = e.pointerId;
                tapStart = e.position;
            }
            else tapPointer = NoPointer;
            e.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent e)
        {
            if (!pointers.TryGetValue(e.pointerId, out var last)) return;
            Vector2 position = e.position;
            pointers[e.pointerId] = position;
            if (tapPointer == e.pointerId)
            {
                if ((position - tapStart).sqrMagnitude <= tapSlop * tapSlop) return;
                tapPointer = NoPointer;
            }
            if (pointers.Count >= 2) Gesture(e.pointerId, last, position);
            else
            {
                var look = e.pointerType == UnityEngine.UIElements.PointerType.touch ? touchLook : mouseLook;
                var delta = position - last;
                rig.Look(delta.x * look, delta.y * look);
            }
            e.StopPropagation();
        }

        // Two fingers: their midpoint slides the camera (the picture follows the fingers), their spread moves it forward.
        void Gesture(int moved, Vector2 from, Vector2 to)
        {
            var other = Vector2.zero;
            foreach (var p in pointers)
            {
                if (p.Key == moved) continue;
                other = p.Value;
                break;
            }
            var slide = (to - from) * 0.5f;
            var spread = (to - other).magnitude - (from - other).magnitude;
            var t = rig.Transform;
            rig.Move(((t.up * slide.y - t.right * slide.x) * touchSlide + t.forward * (spread * pinchDolly)) * speedScale);
        }

        void OnPointerUp(PointerUpEvent e)
        {
            if (!pointers.Remove(e.pointerId)) return;
            if (surface.HasPointerCapture(e.pointerId)) surface.ReleasePointer(e.pointerId);
            var tap = tapPointer == e.pointerId && pointers.Count == 0;
            if (tapPointer == e.pointerId) tapPointer = NoPointer;
            e.StopPropagation();
            if (tap && stepFrame != Time.frameCount) TakePhoto();
        }

        void DropPointer(int id)
        {
            pointers.Remove(id);
            if (tapPointer == id) tapPointer = NoPointer;
        }

        void OnWheel(WheelEvent e)
        {
            if (e.delta.y == 0f) return;
            ShowHints(Hints.Keys);
            ChangeSpeed(e.delta.y < 0f ? 1 : -1);
            e.StopPropagation();
        }

        void ReleasePointers()
        {
            foreach (var id in pointers.Keys)
                if (surface.HasPointerCapture(id)) surface.ReleasePointer(id);
            pointers.Clear();
            tapPointer = NoPointer;
        }

        void OnCancel(NavigationCancelEvent e)
        {
            e.StopPropagation();
            if (stepFrame != Time.frameCount) StepBack();
        }

        void StepBack()
        {
            if (previewOpen) ClosePreview();
            else Close();
        }

        // The delete question takes the cancel itself (on the focused element, so its own cancel handling applies).
        void CancelPrompt()
        {
            var target = Root.focusController?.focusedElement as VisualElement ?? Root;
            using var e = NavigationCancelEvent.GetPooled();
            target.SendEvent(e);
        }

        // Renders the view, shows it at once and writes the PNG on a worker thread (the preview's file buttons wait for it).
        void TakePhoto()
        {
            stepFrame = Time.frameCount;
            int width = Screen.width, height = Screen.height;
            if (width <= 0 || height <= 0) return;
            var tex = rig.Capture(width, height);
            var pixels = tex.GetRawTextureData<byte>().ToArray();
            var format = tex.graphicsFormat;
            tex.Apply(false, true);
            var folder = DataPaths.Screenshots;
            var path = NewPhotoPath(folder);
            savingPath = path;
            saving = Task.Run(() => Save(pixels, format, (uint)width, (uint)height, folder, path));
            FlashScreen();
            ShowPreview(tex, path);
        }

        // <track id>_<date>_<time>.png, numbered when that name is taken (or still being written).
        string NewPhotoPath(string folder)
        {
            var id = Race.Track != null ? Race.Track.Id : "race";
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            var stem = id + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var path = Path.Combine(folder, stem + ".png");
            for (var n = 2; File.Exists(path) || path == savingPath; n++) path = Path.Combine(folder, stem + "_" + n + ".png");
            return path;
        }

        static bool Save(byte[] pixels, GraphicsFormat format, uint width, uint height, string folder, string path)
        {
            try
            {
                var png = ImageConversion.EncodeArrayToPNG(pixels, format, width, height);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(path, png);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }

        void PollSaving()
        {
            if (saving == null || !saving.IsCompleted) return;
            var saved = saving.Result;
            var path = savingPath;
            saving = null;
            if (!previewOpen || path != shotPath) return;
            fileLabel.text = saved ? string.Format(savedFormat, Path.GetFileName(path)) : saveFailedText;
            SetFileButtons(saved);
        }

        void FlashScreen()
        {
            flash.AddToClassList("flash-on");
            flash.schedule.Execute(() => flash.RemoveFromClassList("flash-on")).ExecuteLater(UiAnim.FrameMs);
        }

        void ShowPreview(Texture2D tex, string path)
        {
            ReleasePointers();
            FreeShot();
            shot = tex;
            shotPath = path;
            previewOpen = true;
            previewImage.style.backgroundImage = new StyleBackground(tex);
            var fit = Mathf.Min(previewSize.x / tex.width, previewSize.y / tex.height);
            previewImage.style.width = tex.width * fit;
            previewImage.style.height = tex.height * fit;
            fileLabel.text = savingText;
            SetFileButtons(false);
            preview.SetVisible(true);
            previewColumn.RemoveFromClassList("shown");
            previewColumn.schedule.Execute(() => previewColumn.AddToClassList("shown")).ExecuteLater(UiAnim.FrameMs);
            backButton.Focus();
        }

        void SetFileButtons(bool saved)
        {
            openButton.SetEnabled(saved);
            folderButton.SetEnabled(saved);
            deleteButton.SetEnabled(saved);
        }

        // Back to flying: the preview texture is freed.
        void ClosePreview()
        {
            stepFrame = Time.frameCount;
            FreeShot();
            previewOpen = false;
            preview.SetVisible(false);
            ClearFocus();
        }

        void FreeShot()
        {
            if (previewImage != null) previewImage.style.backgroundImage = StyleKeyword.Null;
            if (shot != null) Destroy(shot);
            shot = null;
            shotPath = null;
        }

        static void OpenFile(string path)
        {
            if (!string.IsNullOrEmpty(path)) Application.OpenURL(new Uri(path).AbsoluteUri);
        }

        void AskDelete()
        {
            promptOpen = ConfirmPrompt.Show(deleteButton, deleteQuestion, deleteConfirm, DeleteShot, () =>
            {
                promptOpen = false;
                stepFrame = Time.frameCount;
            }) != null;
        }

        void DeleteShot()
        {
            promptOpen = false;
            try
            {
                File.Delete(shotPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Debug.LogException(e);
            }
            ClosePreview();
        }
    }
}
