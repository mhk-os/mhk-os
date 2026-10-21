using MHK.UI;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.Editor.UI
{
    /// <summary>
    /// Inspector of every UI screen and layer: opens its layout in UI Builder and, for menu screens while playing,
    /// shows the screen in the game. The component's own fields follow.
    /// </summary>
    [CustomEditor(typeof(UiScreen), true)]
    public class UiScreenEditor : UnityEditor.Editor
    {
        const string NoLayout = "The Panel Renderer has no Visual Tree Asset (UXML).";
        const float SpaceHeight = 6f;
        const long RefreshMs = 250;

        Button edit, open;
        HelpBox warning;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.Add(ButtonRow((UiScreen)target));
            warning = new HelpBox(NoLayout, HelpBoxMessageType.Warning);
            root.Add(warning);
            root.Add(new VisualElement { style = { height = SpaceHeight } });
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.RegisterCallback<AttachToPanelEvent>(_ => EditorApplication.playModeStateChanged += OnPlayModeChanged);
            root.RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.playModeStateChanged -= OnPlayModeChanged);
            root.schedule.Execute(Refresh).Every(RefreshMs);
            Refresh();
            return root;
        }

        VisualElement ButtonRow(UiScreen screen)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            edit = RowButton("Edit layout", "Opens this screen's UXML in UI Builder.", () =>
            {
                var layout = Layout(screen);
                if (layout != null) AssetDatabase.OpenAsset(layout);
            });
            row.Add(edit);
            open = null;
            if (screen is MenuScreen menuScreen)
            {
                open = RowButton("Open screen", "Play mode: shows this menu screen (Back returns to the current one).", () =>
                {
                    var flow = menuScreen.GetComponentInParent<MenuFlow>(true);
                    if (Application.isPlaying && flow != null) flow.Show(menuScreen);
                });
                row.Add(open);
            }
            return row;
        }

        static Button RowButton(string text, string tooltip, System.Action click) =>
            new(click) { text = text, tooltip = tooltip, style = { flexGrow = 1, flexBasis = 0 } };

        static VisualTreeAsset Layout(UiScreen screen)
        {
            var renderer = screen.GetComponent<PanelRenderer>();
            return renderer != null ? renderer.visualTreeAsset : null;
        }

        void OnPlayModeChanged(PlayModeStateChange change) => Refresh();

        void Refresh()
        {
            var screen = target as UiScreen;
            if (screen == null) return;
            var hasLayout = Layout(screen) != null;
            edit.SetEnabled(hasLayout);
            warning.style.display = hasLayout ? DisplayStyle.None : DisplayStyle.Flex;
            if (open != null && screen is MenuScreen menuScreen)
                open.SetEnabled(Application.isPlaying && menuScreen.GetComponentInParent<MenuFlow>(true) != null);
        }
    }
}
