using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.UI
{
    /// <summary>
    /// Sets UI Builder's canvas of every UXML under Assets/MHK/UI to its in-game size and background picture, for
    /// untouched canvases on load and for all from MHK ▸ UI ▸ Reset UI Builder Canvases. Uses UI Builder internals by
    /// reflection and only logs a warning when a Unity version renames them.
    /// </summary>
    [InitializeOnLoad]
    static class UiBuilderCanvasSetup
    {
        const string LayoutRoot = "Assets/MHK/UI";
        const string PreviewFolder = "Assets/MHK/Editor/UI/Preview/";
        const int Width = 1920, Height = 1080;
        const int TemplateWidth = 960, TemplateHeight = 540;
        // UI Builder's size for a canvas nobody has set.
        const int DefaultWidth = 350, DefaultHeight = 450;
        const float ZoomPage = 0.5f, ZoomTemplate = 1f;
        static readonly Vector2 PanOffset = new(20f, 20f);

        static readonly string[] FieldNames =
        {
            "CanvasWidth", "CanvasHeight", "MatchGameView", "EnableCanvasBackground", "CanvasBackgroundMode",
            "CanvasBackgroundImage", "CanvasBackgroundImageScaleMode", "ImageModeCanvasBackgroundOpacity", "ZoomScale", "PanOffset",
        };

        static bool failureLogged;

        static UiBuilderCanvasSetup() => EditorApplication.delayCall += () => Apply(false);

        [MenuItem("MHK/UI/Reset UI Builder Canvases")]
        static void ResetAll() => Apply(true);

        sealed class SettingsApi
        {
            public MethodInfo Load, Save;
            public Dictionary<string, FieldInfo> Fields;
            public object ImageMode;
        }

        static void Apply(bool force)
        {
            try
            {
                var api = ResolveSettingsApi();
                if (api == null)
                {
                    Debug.LogWarning("UI Builder canvas setup: this Unity version's UI Builder settings are not recognised; set the canvases by hand (1920x1080, Canvas Background ▸ Image).");
                    return;
                }
                var changed = EditorAssets.FindPaths("t:VisualTreeAsset", LayoutRoot).Count(path => ConfigureCanvas(api, path, force));
                if (force || changed > 0)
                    Debug.Log($"UI Builder canvas setup: {changed} layout(s) set to their in-game size and background. Reopen open documents in UI Builder to see it.");
            }
            catch (Exception e)
            {
                if (failureLogged) return;
                failureLogged = true;
                Debug.LogWarning($"UI Builder canvas setup failed: {e}");
            }
        }

        static SettingsApi ResolveSettingsApi()
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var type = Type.GetType("Unity.UI.Builder.BuilderDocumentSettings, UnityEditor.UIBuilderModule");
            if (type == null) return null;
            var api = new SettingsApi
            {
                Load = type.GetMethod("CreateOrLoadSettingsObject", Any),
                Save = type.GetMethod("SaveSettingsToDisk", Any),
                Fields = FieldNames.ToDictionary(n => n, n => type.GetField(n, Any)),
            };
            if (api.Load == null || api.Save == null || api.Fields.Values.Any(f => f == null)) return null;
            api.ImageMode = Enum.Parse(api.Fields["CanvasBackgroundMode"].FieldType, "Image");
            return api;
        }

        static bool ConfigureCanvas(SettingsApi api, string path, bool force)
        {
            var settings = api.Load.Invoke(null, new object[] { null, path });
            T Get<T>(string field) => (T)api.Fields[field].GetValue(settings);
            void Set(string field, object value) => api.Fields[field].SetValue(settings, value);
            var untouched = Get<int>("CanvasWidth") == DefaultWidth && Get<int>("CanvasHeight") == DefaultHeight && !Get<bool>("EnableCanvasBackground");
            if (!force && !untouched) return false;

            var template = path.Contains("/Templates/");
            Set("CanvasWidth", template ? TemplateWidth : Width);
            Set("CanvasHeight", template ? TemplateHeight : Height);
            Set("MatchGameView", false);
            Set("EnableCanvasBackground", true);
            Set("CanvasBackgroundMode", api.ImageMode);
            Set("CanvasBackgroundImage", AssetDatabase.LoadAssetAtPath<Texture2D>(PreviewFolder + Background(path) + ".png"));
            Set("CanvasBackgroundImageScaleMode", ScaleMode.ScaleAndCrop);
            Set("ImageModeCanvasBackgroundOpacity", 1f);
            Set("ZoomScale", template ? ZoomTemplate : ZoomPage);
            Set("PanOffset", PanOffset);
            api.Save.Invoke(settings, null);
            return true;
        }

        static string Background(string path)
        {
            if (path.StartsWith(LayoutRoot + "/Race/")) return "RaceView";
            var text = File.ReadAllText(path);
            return text.Contains("page-main wide") || text.Contains("wide page-main") ? "MenuWide" : "MenuStage";
        }
    }
}
