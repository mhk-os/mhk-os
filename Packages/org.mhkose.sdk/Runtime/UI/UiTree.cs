using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>Visual-tree helpers for UXML screens: named lookups, template instances, visibility, classes and screen entry.</summary>
    public static class UiTree
    {
        /// <summary>
        /// Element <paramref name="name"/> of a screen. A missing element (renamed or deleted in UI Builder) is logged
        /// and replaced by a detached stand-in, so the rest of the screen keeps working.
        /// </summary>
        public static T Require<T>(VisualElement root, string name, UnityEngine.Object context) where T : VisualElement, new()
        {
            var e = root.Q<T>(name);
            if (e != null) return e;
            Debug.LogError($"{(context != null ? context.name : "UI")}: no {typeof(T).Name} named '{name}' in its UXML; that part of the screen does nothing.", context);
            return new T();
        }

        /// <summary>
        /// Clones <paramref name="template"/> into <paramref name="parent"/> (no TemplateContainer wrapper) and returns
        /// the template's first root element, or null when the template is missing or its root is not a <typeparamref name="T"/>.
        /// </summary>
        public static T Spawn<T>(VisualTreeAsset template, VisualElement parent) where T : VisualElement
        {
            if (template == null || parent == null) return null;
            template.CloneTree(parent, out var first, out var count);
            return count > 0 ? parent[first] as T : null;
        }

        /// <summary>
        /// Starts the declarative effects of a freshly cloned screen: <c>.anim</c> elements animate in, <c>.bob</c> and
        /// <c>.pulse</c> loop, <c>.desktop-only</c> / <c>.mobile-only</c> hide on the other platform and <c>preview-*</c>
        /// classes (UI Builder previews of other states) are removed.
        /// </summary>
        public static void PlayEntry(VisualElement root)
        {
            root.Query().ForEach(RemovePreviewClasses);
            root.Query(className: "anim").ForEach(e => UiAnim.AnimateIn(e));
            root.Query(className: "bob").ForEach(e => UiAnim.Bob(e, UiAnim.EntryBobPixels, UiAnim.EntryBobPeriod));
            root.Query(className: "pulse").ForEach(e => UiAnim.Pulse(e, UiAnim.EntryPulseAmount, UiAnim.EntryPulsePeriod));
            var mobile = UnityEngine.Device.Application.isMobilePlatform;
            root.Query(className: mobile ? "desktop-only" : "mobile-only").ForEach(e => e.SetVisible(false));
        }

        static void RemovePreviewClasses(VisualElement e) => RemoveClasses(e, "preview-");

        static void RemoveClasses(VisualElement e, string prefix)
        {
            using var _ = ListPool<string>.Get(out var found);
            foreach (var c in e.GetClasses())
                if (c.StartsWith(prefix, StringComparison.Ordinal)) found.Add(c);
            foreach (var c in found) e.RemoveFromClassList(c);
        }

        /// <summary>Shows (display flex) or hides (display none) the element.</summary>
        public static void SetVisible(this VisualElement e, bool visible) =>
            e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>Sets the text of the Label <paramref name="name"/> below <paramref name="parent"/>, no-op when it is missing.</summary>
        public static void SetText(this VisualElement parent, string name, string text)
        {
            var label = parent.Q<Label>(name);
            if (label != null) label.text = text;
        }

        /// <summary>Sets the element's background picture (null removes it, also a USS one).</summary>
        public static void SetImage(this VisualElement e, Texture2D image) =>
            e.style.backgroundImage = image != null ? new StyleBackground(image) : new StyleBackground(StyleKeyword.None);

        /// <summary>Enables <c>classes[index]</c> and disables the others (an index outside the list disables all).</summary>
        public static void SetExclusiveClass(this VisualElement e, int index, IReadOnlyList<string> classes)
        {
            for (var i = 0; i < classes.Count; i++) e.EnableInClassList(classes[i], i == index);
        }

        /// <summary>Replaces the <c>i-*</c> icon class of <paramref name="icon"/> (Material Symbols name, e.g. "flag").</summary>
        public static void SetIcon(this VisualElement icon, string name)
        {
            RemoveClasses(icon, "i-");
            icon.AddToClassList("i-" + name);
        }

        /// <summary>Inline transition delay (entry animation of a <c>.anim</c> element that depends on runtime content).</summary>
        public static void SetTransitionDelay(this VisualElement e, long ms) =>
            e.style.transitionDelay = new List<TimeValue> { new(ms, TimeUnit.Millisecond) };

        /// <summary>The element and its children never take pointer input (layers below the touch controls and menus).</summary>
        public static void NoPick(this VisualElement e)
        {
            e.pickingMode = PickingMode.Ignore;
            for (var i = 0; i < e.childCount; i++) e[i].NoPick();
        }

        /// <summary>Reads the USS custom colour <paramref name="property"/> into <paramref name="color"/>, which keeps its value when the property is not set.</summary>
        public static void ReadColor(this ICustomStyle style, CustomStyleProperty<Color> property, ref Color color)
        {
            if (style.TryGetValue(property, out var value)) color = value;
        }
    }
}
