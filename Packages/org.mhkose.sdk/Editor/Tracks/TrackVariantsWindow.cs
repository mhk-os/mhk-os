using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Core;
using MHK.Editor.Modding;
using MHK.Modding;
using MHK.Race;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Editor.Tracks
{
    /// <summary>
    /// MHK ▸ Tracks ▸ Track Variants: the options a track offers (its <see cref="TrackOverlayDefinition"/>s by modifier),
    /// creating overlay definitions and overlay scenes, and editing an overlay scene on top of its track scene. While an
    /// overlay is edited its scene is the active one and the track scene is loaded behind it as a locked reference
    /// (<see cref="TrackOverlayGhost"/>) that the window never saves. Clicks on the track can place a prefab into the
    /// overlay (the SDK roadblock by default, for the side paths an arena's circuit course closes), and placed objects can
    /// be dropped onto the track below them. Preview applies the overlay's environment to the track scene
    /// (<see cref="TrackEnvironment"/>) and puts it back, also before any scene is saved. Closing restores the scenes that
    /// were open before. Opening an overlay scene on its own starts editing it here (<see cref="TrackOverlaySceneOpener"/>).
    /// </summary>
    public sealed class TrackVariantsWindow : EditorWindow
    {
        const string MenuPath = "MHK/Tracks/Track Variants";
        const float OptionWidth = 150f, SceneWidth = 190f, ButtonWidth = 80f;
        const string DefaultPlacePrefab = "Packages/org.mhkose.sdk/Prefabs/Track/Roadblock.prefab";
        // While placing, Shift with the mouse wheel turns the prefab by YawStep degrees. Clicks reach the track up to
        // MaxPlaceDistance m away. Dropping looks for ground from DropAbove m over an object down to DropDepth m under it.
        const float YawStep = 15f, MaxPlaceDistance = 3000f, DropAbove = 5f, DropDepth = 100f, MarkerRadius = 0.4f;
        static readonly Color MarkerColor = new(1f, 0.45f, 0.1f);

        // The window is switching scenes itself (opening a session or putting the old scenes back).
        static bool switching;

        /// <summary>True while the window opens or closes a session's scenes, so scenes opened then are its own doing.</summary>
        internal static bool Switching => switching;

        [Serializable]
        struct OpenScene
        {
            public string path;
            public bool loaded, active;
        }

        // One overlay definition of the track and how the game sees it.
        sealed class Row
        {
            public TrackOverlayDefinition overlay;
            public TrackOptionDefinition option;
            public string modifierId, modifierName;
            public int modifierOrder;
            public bool registered;
            public ContentPack pack;
        }

        [SerializeField] TrackDefinition track;
        [SerializeField] int addOptionIndex, addPackIndex;
        [SerializeField] bool addDefault, addScene = true;
        [SerializeField] TrackOverlayDefinition editing;
        [SerializeField] string trackScenePath, overlayScenePath;
        [SerializeField] List<OpenScene> previousScenes = new();
        [SerializeField] bool xray, showLines = true, preview;
        [SerializeField] List<string> previewPicks = new();
        [SerializeField] GameObject placePrefab;
        [SerializeField] float placeYaw;
        bool placing;

        readonly List<Row> rows = new();
        readonly List<TrackOptionDefinition> freeOptions = new();
        readonly List<ContentPack> packs = new();
        TrackVariantSet set;
        TrackOverlayGhost ghost;
        TrackEnvironment.State previewState;
        SceneLook overlayLook;
        string previewedOverlay;
        bool resumePreviewAfterSave, closing;
        Vector2 scroll;

        [MenuItem(MenuPath)]
        static void Open()
        {
            var window = GetWindow<TrackVariantsWindow>("Track Variants");
            if (window.editing == null && Selection.activeObject is TrackDefinition selected)
            {
                window.track = selected;
                window.TrackChanged();
            }
        }

        void OnEnable()
        {
            Refresh();
            if (editing != null) StartSession();
        }

        void OnDisable() => StopSession();

        void OnDestroy()
        {
            if (editing != null) EndEdit();
        }

        void OnFocus() => Refresh();

        void OnProjectChange()
        {
            Refresh();
            Repaint();
        }

        // Live preview: the overlay's settings changed in the inspector.
        void OnInspectorUpdate()
        {
            if (previewState == null) return;
            var overlay = EditedOverlay();
            if (overlay == null || EditorJsonUtility.ToJson(overlay) == previewedOverlay) return;
            RestorePreview();
            ApplyPreview();
        }

        void Refresh()
        {
            rows.Clear();
            freeOptions.Clear();
            packs.Clear();
            set = null;
            if (track == null) return;
            set = TrackVariants.Of(track, Registry());
            var options = TrackOverlayAssets.AllOptions();
            var modifiers = EditorAssets.FindAll<TrackModifierDefinition>().GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var allPacks = EditorAssets.FindAll<ContentPack>().ToList();
            var registered = new HashSet<TrackOverlayDefinition>(set.Modifiers.SelectMany(m => m.Overlays));
            foreach (var overlay in TrackOverlayAssets.ForTrack(track).Concat(registered).Distinct())
            {
                var option = options.FirstOrDefault(o => string.Equals(o.Id, overlay.optionId, StringComparison.OrdinalIgnoreCase));
                var modifierId = option != null ? option.modifierId : "";
                modifiers.TryGetValue(modifierId ?? "", out var modifier);
                rows.Add(new Row
                {
                    overlay = overlay,
                    option = option,
                    modifierId = modifierId,
                    modifierName = modifier != null ? modifier.DisplayName : string.IsNullOrEmpty(modifierId) ? "Unknown option" : modifierId,
                    modifierOrder = modifier != null ? modifier.order : int.MaxValue,
                    registered = registered.Contains(overlay),
                    pack = allPacks.FirstOrDefault(p => p.content.Contains(overlay)),
                });
            }
            rows.Sort((a, b) => a.modifierOrder != b.modifierOrder ? a.modifierOrder.CompareTo(b.modifierOrder)
                : string.Compare(a.modifierId, b.modifierId, StringComparison.OrdinalIgnoreCase) != 0
                    ? string.Compare(a.modifierId, b.modifierId, StringComparison.OrdinalIgnoreCase)
                    : (a.option != null ? a.option.order : 0).CompareTo(b.option != null ? b.option.order : 0));
            freeOptions.AddRange(options.Where(o => rows.All(r => !string.Equals(r.overlay.optionId, o.Id, StringComparison.OrdinalIgnoreCase))));
            packs.AddRange(ContentPackAssets.ShippedPacks().Concat(ProjectModPacks.All()).SelectMany(p => p.SelfAndIncluded()).Distinct());
            addPackIndex = Mathf.Clamp(addPackIndex, 0, Mathf.Max(0, packs.Count - 1));
            addOptionIndex = Mathf.Clamp(addOptionIndex, 0, Mathf.Max(0, freeOptions.Count - 1));
        }

        // A new track: its options, and its own pack as the target of new overlays.
        void TrackChanged()
        {
            Refresh();
            var home = packs.FindIndex(p => p.content.Contains(track));
            if (home >= 0) addPackIndex = home;
        }

        // The game's content while playing, else the project's packs in load order.
        static ContentRegistry Registry()
        {
            if (EditorApplication.isPlaying && GameRoot.Instance != null) return GameRoot.Instance.Content;
            var registry = new ContentRegistry();
            var roots = ContentPackAssets.BuiltinPacks().Concat(ContentPackAssets.ShippedPacks()).Concat(ProjectModPacks.All());
            foreach (var pack in ContentPackAssets.TopLevel(roots)) registry.RegisterRoot(pack);
            return registry;
        }

        void OnGUI()
        {
            using (new EditorGUI.DisabledScope(editing != null))
            {
                EditorGUI.BeginChangeCheck();
                track = (TrackDefinition)EditorGUILayout.ObjectField("Track", track, typeof(TrackDefinition), false);
                if (EditorGUI.EndChangeCheck()) TrackChanged();
            }
            if (track == null)
            {
                EditorGUILayout.HelpBox("Pick a track definition to see and edit the options it offers.", MessageType.Info);
                return;
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawOptions();
            EditorGUILayout.Space();
            if (editing != null) DrawSession();
            else DrawCreate();
            EditorGUILayout.EndScrollView();
        }

        void DrawOptions()
        {
            EditorGUILayout.LabelField("Offered options", EditorStyles.boldLabel);
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("The track offers no options: no overlay definition names it.", MessageType.None);
                return;
            }
            string modifier = null;
            foreach (var row in rows)
            {
                if (row.modifierId != modifier)
                {
                    modifier = row.modifierId;
                    var count = rows.Count(r => r.modifierId == modifier);
                    EditorGUILayout.LabelField(count > 1 ? row.modifierName : row.modifierName + " (always, no choice)", EditorStyles.miniBoldLabel);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    var name = row.option != null ? row.option.DisplayName : row.overlay.optionId;
                    EditorGUILayout.LabelField(new GUIContent(row.overlay.isDefault ? name + " (default)" : name, row.overlay.optionId), GUILayout.Width(OptionWidth));
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(row.overlay, typeof(TrackOverlayDefinition), false);
                    EditorGUILayout.LabelField(row.overlay.scene.IsSet ? Path.GetFileName(row.overlay.scene.Path) : "no overlay scene", GUILayout.Width(SceneWidth));
                    using (new EditorGUI.DisabledScope(!CanEdit()))
                        if (GUILayout.Button(row.overlay.scene.IsSet ? "Edit" : "Add scene", GUILayout.Width(ButtonWidth)))
                        {
                            var overlay = row.overlay;
                            EditorApplication.delayCall += () => BeginEdit(overlay);
                        }
                }
                if (row.pack == null)
                    EditorGUILayout.HelpBox($"{row.overlay.Id} is in no pack's content list: the game does not load it.", MessageType.Warning);
                else if (!row.registered)
                    EditorGUILayout.HelpBox($"{row.overlay.Id} is not used: the overlay of another pack for this track and option wins (loaded later).", MessageType.Info);
            }
        }

        void DrawCreate()
        {
            EditorGUILayout.LabelField("Offer another option", EditorStyles.boldLabel);
            if (freeOptions.Count == 0)
            {
                EditorGUILayout.LabelField("The track offers every option of the project.");
                return;
            }
            addOptionIndex = EditorGUILayout.Popup(new GUIContent("Option", "Track option the new overlay definition offers on this track."),
                addOptionIndex, freeOptions.Select(o => new GUIContent($"{ModifierName(o.modifierId)}/{o.DisplayName}")).ToArray());
            addDefault = EditorGUILayout.Toggle(new GUIContent("Default", "The track is in this option unless a variant picks another one of the modifier."), addDefault);
            using (new EditorGUI.DisabledScope(!HasTrackScene()))
                addScene = EditorGUILayout.Toggle(new GUIContent("Overlay scene", "Create the overlay scene next to the track scene and edit it."), addScene);
            if (packs.Count > 0)
                addPackIndex = EditorGUILayout.Popup(new GUIContent("Add to pack", "Pack whose content lists the new overlay definition."),
                    addPackIndex, packs.Select(p => new GUIContent($"{p.packId} ({AssetDatabase.GetAssetPath(p)})")).ToArray());
            else EditorGUILayout.HelpBox("The project has no mod pack: the definition is created outside any pack.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Create overlay definition")) EditorApplication.delayCall += Create;
        }

        void DrawSession()
        {
            EditorGUILayout.LabelField("Editing " + editing.Id, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Overlay scene", overlayScenePath);
            EditorGUILayout.LabelField("Track scene (locked, not saved)", trackScenePath);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUI.BeginChangeCheck();
                xray = EditorGUILayout.Toggle(new GUIContent("X-ray track", "Draw the track scene as a see-through silhouette."), xray);
                showLines = EditorGUILayout.Toggle(new GUIContent("Racing lines", "Draw the track's racing lines with arrows in the driving direction."), showLines);
                if (EditorGUI.EndChangeCheck() && ghost != null)
                {
                    ghost.Xray = xray;
                    ghost.ShowLines = showLines;
                    SceneView.RepaintAll();
                }
                EditorGUI.BeginChangeCheck();
                preview = EditorGUILayout.Toggle(new GUIContent("Preview environment",
                    "Apply the overlay's lights, fog, background, baked light and sky to the track scene (put back when off)."), preview);
                var picksChanged = DrawPreviewPicks();
                if (EditorGUI.EndChangeCheck() || picksChanged)
                {
                    RestorePreview();
                    if (preview) ApplyPreview();
                }
                DrawPlacing();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Select overlay"))
                    {
                        var overlay = EditedOverlay();
                        if (overlay != null) Selection.activeGameObject = overlay.gameObject;
                    }
                    if (GUILayout.Button("Save overlay scene"))
                    {
                        var scene = SceneManager.GetSceneByPath(overlayScenePath);
                        if (scene.isLoaded) EditorSceneManager.SaveScene(scene);
                    }
                    if (GUILayout.Button("Close")) EditorApplication.delayCall += EndEdit;
                }
            }
        }

        void DrawPlacing()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Placing objects", EditorStyles.boldLabel);
            placePrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Prefab",
                "Prefab a click on the track places into the overlay. Empty uses the SDK roadblock."), PlacePrefab(), typeof(GameObject), false);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(PlacePrefab() == null))
                {
                    var on = GUILayout.Toggle(placing, new GUIContent(placing ? "Stop placing (Esc)" : "Place by clicking",
                        "Each click on the track in the Scene view places the prefab there, turned across your view."), "Button");
                    if (on != placing) SetPlacing(on);
                }
                if (GUILayout.Button(new GUIContent("Drop selection onto track",
                        "Moves the selected objects of the overlay up or down onto the ground right under them.")))
                    DropSelection();
            }
            if (placing)
                EditorGUILayout.HelpBox("Click the track in the Scene view to place the prefab facing the camera. Shift with the " +
                                        "mouse wheel turns it and Esc stops. Placed objects go under '" + PlacedGroup() + "' in the overlay.",
                    MessageType.None);
        }

        GameObject PlacePrefab()
        {
            if (placePrefab == null) placePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPlacePrefab);
            return placePrefab;
        }

        string PlacedGroup() => PlacePrefab() != null ? PlacePrefab().name + "s" : "Placed";

        void SetPlacing(bool on)
        {
            placing = on;
            SceneView.RepaintAll();
            Repaint();
        }

        // The placing tool in the Scene view. A marker shows where a click would place the prefab, and a click places it.
        void OnPlaceGui(SceneView view)
        {
            if (!placing || editing == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var e = Event.current;
            var id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                SetPlacing(false);
                e.Use();
                return;
            }
            if (e.type == EventType.ScrollWheel && e.shift)
            {
                // macOS turns Shift and the wheel into sideways scrolling.
                var delta = Mathf.Abs(e.delta.y) > Mathf.Abs(e.delta.x) ? e.delta.y : e.delta.x;
                placeYaw = Mathf.Repeat(placeYaw + Mathf.Sign(delta) * YawStep, 360f);
                e.Use();
                view.Repaint();
                return;
            }
            if (!TryPlacePose(view, e.mousePosition, out var pose)) return;
            switch (e.type)
            {
                case EventType.Repaint:
                    DrawPlaceMarker(pose);
                    break;
                case EventType.MouseMove:
                    view.Repaint();
                    break;
                case EventType.MouseDown when e.button == 0 && !e.alt:
                    Place(pose);
                    e.Use();
                    break;
            }
        }

        // Where the mouse points on the track, the prefab facing the camera (its width across the view) and turned by placeYaw.
        bool TryPlacePose(SceneView view, Vector2 mouse, out Pose pose)
        {
            pose = default;
            var ray = HandleUtility.GUIPointToWorldRay(mouse);
            if (!Physics.Raycast(ray, out var hit, MaxPlaceDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
            var facing = Vector3.ProjectOnPlane(view.camera.transform.forward, Vector3.up);
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(view.camera.transform.up, Vector3.up);
            pose = new Pose(hit.point, Quaternion.LookRotation(facing.normalized) * Quaternion.Euler(0f, placeYaw, 0f));
            return true;
        }

        void DrawPlaceMarker(Pose pose)
        {
            var half = PrefabHalfWidth();
            var across = pose.rotation * Vector3.right * half;
            using (new Handles.DrawingScope(MarkerColor))
            {
                Handles.DrawWireDisc(pose.position, Vector3.up, MarkerRadius);
                Handles.DrawLine(pose.position - across, pose.position + across, 3f);
                Handles.DrawLine(pose.position, pose.position + pose.rotation * Vector3.forward * MarkerRadius * 2f, 2f);
            }
        }

        // Half the width of the prefab (its renderers' bounds across), at least the marker's radius.
        float PrefabHalfWidth()
        {
            var prefab = PlacePrefab();
            if (prefab == null) return MarkerRadius;
            var renderers = prefab.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return MarkerRadius;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            return Mathf.Max(MarkerRadius, bounds.extents.x);
        }

        void Place(Pose pose)
        {
            var scene = SceneManager.GetSceneByPath(overlayScenePath);
            var overlay = EditedOverlay();
            var prefab = PlacePrefab();
            if (!scene.isLoaded || overlay == null || prefab == null) return;
            var groupName = PlacedGroup();
            var group = overlay.transform.Find(groupName);
            if (group == null)
            {
                group = new GameObject(groupName).transform;
                Undo.RegisterCreatedObjectUndo(group.gameObject, "Place " + prefab.name);
                Undo.SetTransformParent(group, overlay.transform, "Place " + prefab.name);
            }
            var placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            placed.transform.SetParent(group, false);
            placed.transform.SetPositionAndRotation(pose.position, pose.rotation);
            Undo.RegisterCreatedObjectUndo(placed, "Place " + prefab.name);
            Selection.activeGameObject = placed;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        // The selected objects of the overlay stand on the nearest ground under them (their own colliders aside).
        void DropSelection()
        {
            var scene = SceneManager.GetSceneByPath(overlayScenePath);
            if (!scene.isLoaded) return;
            Physics.SyncTransforms();
            var moved = false;
            foreach (var t in Selection.transforms)
            {
                if (t.gameObject.scene != scene || !TryGroundUnder(t, out var y)) continue;
                Undo.RecordObject(t, "Drop onto track");
                var p = t.position;
                p.y = y;
                t.position = p;
                moved = true;
            }
            if (moved) EditorSceneManager.MarkSceneDirty(scene);
        }

        static bool TryGroundUnder(Transform t, out float y)
        {
            y = 0f;
            var best = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(t.position + Vector3.up * DropAbove, Vector3.down, DropAbove + DropDepth,
                         Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= best || hit.collider.transform.IsChildOf(t)) continue;
                best = hit.distance;
                y = hit.point.y;
            }
            return !float.IsPositiveInfinity(best);
        }

        // The options of the other modifiers the preview assumes (they decide sky textures that require an option).
        bool DrawPreviewPicks()
        {
            if (set == null) return false;
            var changed = false;
            foreach (var choice in set.Modifiers)
            {
                // Every modifier with options, the course too (players never pick it, but overlays are made for each).
                if (choice.Overlays.Count < 2 || choice.IndexOf(editing.optionId) >= 0) continue;
                var current = PickedIndex(choice);
                var picked = EditorGUILayout.Popup(new GUIContent("Preview with " + choice.DisplayName, "Option of this modifier the preview assumes."),
                    current, choice.Options.Select(o => new GUIContent(o.DisplayName)).ToArray());
                if (picked == current) continue;
                previewPicks.RemoveAll(id => choice.IndexOf(id) >= 0);
                previewPicks.Add(choice.Options[picked].Id);
                changed = true;
            }
            return changed;
        }

        int PickedIndex(TrackModifierChoice choice)
        {
            foreach (var id in previewPicks)
            {
                var index = choice.IndexOf(id);
                if (index >= 0) return index;
            }
            return choice.DefaultIndex;
        }

        TrackVariant PreviewVariant()
        {
            if (set == null) return null;
            var variant = set.Default;
            for (var m = 0; m < set.Modifiers.Count; m++)
            {
                var choice = set.Modifiers[m];
                var index = choice.IndexOf(editing.optionId);
                variant = set.With(variant, m, index >= 0 ? index : PickedIndex(choice));
            }
            return variant;
        }

        string ModifierName(string modifierId)
        {
            var modifier = TrackOverlayAssets.Modifier(modifierId);
            return modifier != null ? modifier.DisplayName : modifierId;
        }

        bool HasTrackScene() => track != null && track.scene.IsSet && AssetDatabase.LoadAssetAtPath<SceneAsset>(track.scene.Path) != null;

        bool CanEdit() => editing == null && HasTrackScene() && !EditorApplication.isPlayingOrWillChangePlaymode;

        // The definition goes into the track's folder when the pack holds the track, else into the pack's folder.
        void Create()
        {
            var option = freeOptions[addOptionIndex];
            var pack = packs.Count > 0 ? packs[addPackIndex] : null;
            var trackFolder = TrackOverlayAssets.TrackFolder(track);
            var packFolder = pack != null ? AssetPaths.DirectoryOf(pack) : null;
            var folder = packFolder == null || trackFolder.StartsWith(packFolder + "/", StringComparison.Ordinal) ? trackFolder : packFolder;
            var def = TrackOverlayAssets.CreateOrUpdate(track, option, addDefault, folder, pack);
            AssetDatabase.SaveAssets();
            Refresh();
            if (addScene && HasTrackScene()) BeginEdit(def);
            else EditorGUIUtility.PingObject(def);
        }

        /// <summary>
        /// Starts editing <paramref name="def"/> on top of its track scene, as the window's Edit button does. Closing the
        /// session afterwards leaves an empty scene. Does nothing while another overlay is being edited.
        /// </summary>
        public static void EditOverlay(TrackOverlayDefinition def)
        {
            if (def == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var track = TrackScenes.AllTracks().FirstOrDefault(t => string.Equals(t.Id, def.trackId, StringComparison.OrdinalIgnoreCase));
            if (track == null || !track.scene.IsSet)
            {
                Debug.LogWarning($"[Variants] {def.Id}: no track definition with a scene for '{def.trackId}', the overlay opens on its own.");
                return;
            }
            var window = GetWindow<TrackVariantsWindow>("Track Variants");
            if (window.editing != null) return;
            window.track = track;
            window.TrackChanged();
            window.BeginEdit(def, false);
        }

        // Opens the track scene alone, then the overlay scene (created when missing) as the active scene. Closing reopens
        // the scenes open now when keepScenes is set.
        void BeginEdit(TrackOverlayDefinition def, bool keepScenes = true)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            previousScenes = keepScenes
                ? EditorSceneManager.GetSceneManagerSetup()
                    .Where(s => !string.IsNullOrEmpty(s.path))
                    .Select(s => new OpenScene { path = s.path, loaded = s.isLoaded, active = s.isActive }).ToList()
                : new List<OpenScene>();
            Scene trackScene, overlayScene;
            switching = true;
            try
            {
                trackScene = EditorSceneManager.OpenScene(track.scene.Path, OpenSceneMode.Single);
                overlayScene = TrackOverlayAssets.OpenOrCreateScene(def, trackScene);
            }
            finally
            {
                switching = false;
            }
            AssetDatabase.SaveAssetIfDirty(def);
            SceneManager.SetActiveScene(overlayScene);
            editing = def;
            trackScenePath = trackScene.path;
            overlayScenePath = overlayScene.path;
            preview = false;
            previewPicks.Clear();
            StartSession();
            var overlay = EditedOverlay();
            if (overlay != null) Selection.activeGameObject = overlay.gameObject;
            Refresh();
        }

        void StartSession()
        {
            if (ghost != null) return;
            var trackScene = SceneManager.GetSceneByPath(trackScenePath);
            if (!trackScene.isLoaded || !SceneManager.GetSceneByPath(overlayScenePath).isLoaded)
            {
                ForgetSession();
                return;
            }
            var option = TrackOverlayAssets.Option(editing.optionId);
            ghost = new TrackOverlayGhost(trackScene) { Xray = xray, ShowLines = showLines, Reverse = option != null && option.rules.reverse };
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            SceneView.duringSceneGui += OnPlaceGui;
            if (preview && !EditorApplication.isPlayingOrWillChangePlaymode) ApplyPreview();
        }

        // Puts the track scene back as it was and lets go of it; the session itself stays (script reloads, play mode).
        void StopSession()
        {
            RestorePreview();
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            SceneView.duringSceneGui -= OnPlaceGui;
            placing = false;
            ghost?.Dispose();
            ghost = null;
        }

        void ForgetSession()
        {
            editing = null;
            trackScenePath = overlayScenePath = null;
            previousScenes.Clear();
            preview = false;
        }

        // Offers to save the overlay scene, then reopens the scenes that were open before (the track scene is discarded).
        void EndEdit()
        {
            RestorePreview();
            preview = false;
            var overlayScene = SceneManager.GetSceneByPath(overlayScenePath);
            if (overlayScene.isLoaded && overlayScene.isDirty && !EditorSceneManager.SaveModifiedScenesIfUserWantsTo(new[] { overlayScene })) return;
            StopSession();
            closing = true;
            switching = true;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var setup = previousScenes.Where(s => AssetDatabase.LoadAssetAtPath<SceneAsset>(s.path) != null)
                    .Select(s => new SceneSetup { path = s.path, isLoaded = s.loaded, isActive = s.active }).ToArray();
                if (setup.Length > 0)
                {
                    if (!setup.Any(s => s.isActive && s.isLoaded))
                    {
                        var first = setup.FirstOrDefault(s => s.isLoaded) ?? setup[0];
                        first.isLoaded = true;
                        first.isActive = true;
                    }
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
            }
            finally
            {
                closing = false;
                switching = false;
            }
            ForgetSession();
            Refresh();
            Repaint();
        }

        TrackOverlay EditedOverlay()
        {
            var scene = SceneManager.GetSceneByPath(overlayScenePath);
            return scene.isLoaded ? TrackSceneLayout.FindOverlay(scene) : null;
        }

        // Applies the overlay to the track scene and shows the result's ambient light and fog through the overlay scene,
        // the active one, whose own look is kept to put back.
        void ApplyPreview()
        {
            if (previewState != null || editing == null) return;
            var trackScene = SceneManager.GetSceneByPath(trackScenePath);
            var overlayScene = SceneManager.GetSceneByPath(overlayScenePath);
            var overlay = EditedOverlay();
            if (!trackScene.isLoaded || overlay == null) return;
            var option = TrackOverlayAssets.Option(editing.optionId);
            overlayLook = SceneLook.Of(overlayScene);
            previewState = TrackEnvironment.Apply(trackScene, TrackScenes.FindRuntime(trackScene), new[] { (overlay, option) }, PreviewVariant());
            SceneLook.Of(trackScene).ApplyTo(overlayScene);
            previewedOverlay = EditorJsonUtility.ToJson(overlay);
            SceneView.RepaintAll();
        }

        void RestorePreview()
        {
            if (previewState == null) return;
            previewState.Restore();
            previewState = null;
            var overlayScene = SceneManager.GetSceneByPath(overlayScenePath);
            if (overlayLook != null && overlayScene.isLoaded) overlayLook.ApplyTo(overlayScene);
            overlayLook = null;
            SceneView.RepaintAll();
        }

        // No scene is saved with the preview applied.
        void OnSceneSaving(Scene scene, string path)
        {
            if (previewState == null) return;
            RestorePreview();
            resumePreviewAfterSave = true;
        }

        void OnSceneSaved(Scene scene)
        {
            if (!resumePreviewAfterSave) return;
            resumePreviewAfterSave = false;
            EditorApplication.delayCall += () =>
            {
                if (preview && ghost != null) ApplyPreview();
            };
        }

        // The track or overlay scene closed by hand ends the session where it is.
        void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (closing || (scene.path != trackScenePath && scene.path != overlayScenePath)) return;
            StopSession();
            ForgetSession();
            Repaint();
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) RestorePreview();
            else if (change == PlayModeStateChange.EnteredEditMode && preview) ApplyPreview();
        }
    }
}
