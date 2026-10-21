using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    /// <summary>
    /// Base of every UI screen: a PanelRenderer GameObject whose UXML is re-cloned on open and bound by name in
    /// <see cref="Bind"/>. Live UXML reloads re-bind.
    /// </summary>
    [RequireComponent(typeof(PanelRenderer))]
    public abstract class UiScreen : MonoBehaviour
    {
        PanelRenderer panel;
        // Last tree the renderer handed over (it keeps it while disabled) and its version (changes on reload).
        VisualElement loadedRoot;
        int loadedVersion = -1;
        UiTooltips tooltips;

        public PanelRenderer Panel => panel != null ? panel : panel = GetComponent<PanelRenderer>();

        /// <summary>Root of this screen's UXML while it is bound, else null.</summary>
        public VisualElement Root { get; private set; }

        public bool IsBound => Root != null;

        /// <summary>
        /// False while the owner (menu flow, race HUD) has not opened this screen. Enabling the GameObject by hand
        /// then only shows the raw UXML.
        /// </summary>
        protected virtual bool ReadyToBind => true;

        /// <summary>The page (first root element) is kept inside the screen's safe area (<see cref="SafeArea"/>).</summary>
        protected virtual bool InsetBySafeArea => false;

        protected virtual void Awake() => Panel.RegisterUIReloadCallback(OnUIReload);

        protected virtual void OnDestroy()
        {
            if (panel != null) panel.UnregisterUIReloadCallback(OnUIReload);
        }

        /// <summary>Re-opened: the renderer kept the last tree, so start from the UXML again right away (first open binds in <see cref="OnUIReload"/>).</summary>
        protected virtual void OnEnable()
        {
            if (loadedRoot == null) return;
            Reclone();
            TryBind();
        }

        protected virtual void OnDisable() => Release();

        void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
        {
            var same = root == loadedRoot && version == loadedVersion;
            loadedRoot = root;
            loadedVersion = version;
            if (!isActiveAndEnabled || (same && IsBound)) return;
            // A new version is a fresh clone (first load, or the UXML changed). Bind on the panel's scheduler, not in
            // this callback: Unity calls it while iterating its panel renderers, and binding may open other screens.
            root.schedule.Execute(() =>
            {
                if (root != loadedRoot || version != loadedVersion || !isActiveAndEnabled) return;
                Release();
                TryBind();
            });
        }

        /// <summary>Fills the freshly cloned tree (<see cref="Root"/>) and registers callbacks.</summary>
        protected abstract void Bind();

        /// <summary>Runs after <see cref="Bind"/> and the entry animations started.</summary>
        protected virtual void AfterBind() { }

        /// <summary>Called before the bound tree goes away (close, rebuild, live reload): drop external hooks here.</summary>
        protected virtual void Unbind() { }

        /// <summary>Starts this open screen again from a fresh copy of its UXML (fresh state, entry animations replay).</summary>
        public void Rebuild()
        {
            if (!isActiveAndEnabled || loadedRoot == null) return;
            Release();
            Reclone();
            TryBind();
        }

        /// <summary>Named element of this screen's UXML. A missing one is logged and replaced by a detached stand-in.</summary>
        protected internal T Q<T>(string name) where T : VisualElement, new() => UiTree.Require<T>(Root, name, this);

        void TryBind()
        {
            if (Root != null || loadedRoot == null || !ReadyToBind) return;
            Root = loadedRoot;
            if (tooltips == null || tooltips.Root != Root) tooltips = new UiTooltips(Root);
            Bind();
            UiTree.PlayEntry(Root);
            UiGlass.Apply(Root);
            if (InsetBySafeArea && Root.childCount > 0) SafeArea.Track(Root[0]);
            AfterBind();
        }

        void Reclone()
        {
            loadedRoot.Clear();
            var layout = Panel.visualTreeAsset;
            if (layout != null) layout.CloneTree(loadedRoot);
        }

        void Release()
        {
            if (Root == null) return;
            tooltips?.Hide();
            Unbind();
            Root = null;
        }
    }
}
