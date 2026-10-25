using System;
using System.Collections.Generic;
using MHK.Assets;
using MHK.Modding;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.UI
{
    public partial class ModsScreen
    {
        [SerializeField, Tooltip("Question when Back leaves with changed switches or load order.")]
        string discardQuestion = "Leave without applying? Your changes to enabled and disabled mods are discarded.";
        [SerializeField, Tooltip("Confirm button text of the leave question.")]
        string discardText = "Discard";
        [SerializeField, Tooltip("Tooltip of Apply while nothing changed since the mods were loaded.")]
        string nothingToApplyTip = "Nothing changed since the mods were loaded.";

        // Switches and load order when the page opened, which is the loaded state: leaving either applies (reload) or puts them back.
        List<string> loadedDisabledPacks, loadedDisabledAssetPacks, loadedModOrder, loadedAssetPackOrder, loadedMods, loadedPacks;
        Button apply;
        readonly VisualElement[] tabChips = new VisualElement[2];

        void BindApply()
        {
            apply = OnClick("apply", Apply);
            for (var i = 0; i < tabChips.Length && i < sections.childCount; i++)
                tabChips[i] = sections[i].Q<Button>() ?? sections[i];
            loadedDisabledPacks = new List<string>(Settings.disabledPacks);
            loadedDisabledAssetPacks = new List<string>(Settings.disabledAssetPacks);
            loadedModOrder = new List<string>(Settings.modOrder);
            loadedAssetPackOrder = new List<string>(Settings.assetPackOrder);
            loadedMods = ModOrderIds();
            loadedPacks = PackOrderIds();
            ShowApply();
        }

        void UnbindApply()
        {
            apply = null;
            Array.Clear(tabChips, 0, tabChips.Length);
        }

        List<string> ModOrderIds() => ModOrder.Sort(ModLoader.AvailablePacks, Settings.modOrder).ConvertAll(p => p.packId);

        List<string> PackOrderIds()
        {
            var order = new List<AssetPacks.Info>();
            PendingPackOrder(order);
            return order.ConvertAll(p => p.id);
        }

        bool SwitchesOrOrderChanged() =>
            loadedMods != null && (!SameSet(loadedDisabledPacks, Settings.disabledPacks) ||
                                   !SameSet(loadedDisabledAssetPacks, Settings.disabledAssetPacks) ||
                                   !SameOrder(loadedMods, ModOrderIds()) || !SameOrder(loadedPacks, PackOrderIds()));

        static bool SameSet(List<string> a, List<string> b)
        {
            foreach (var id in a)
                if (IndexOf(b, id) < 0) return false;
            foreach (var id in b)
                if (IndexOf(a, id) < 0) return false;
            return true;
        }

        static bool SameOrder(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Apply is enabled once something changed since the last load. The tab chips show a red dot for their section's updates.</summary>
        void ShowApply()
        {
            if (apply == null) return;
            var changed = ModSources.PendingChanges || SwitchesOrOrderChanged();
            apply.SetEnabled(changed);
            apply.tooltip = changed ? null : nothingToApplyTip;
            NotifyDot.ShowOnChip(tabChips[0], ModSources.HasUpdates(ModSources.Kind.Mod));
            NotifyDot.ShowOnChip(tabChips[1], ModSources.HasUpdates(ModSources.Kind.AssetPack));
        }

        protected override void GoBack()
        {
            if (!SwitchesOrOrderChanged())
            {
                Flow.Back();
                return;
            }
            var from = Root.focusController?.focusedElement as VisualElement ?? Root.Q<Button>("back");
            ConfirmPrompt.Show(from, discardQuestion, discardText, () =>
            {
                Settings.disabledPacks = new List<string>(loadedDisabledPacks);
                Settings.disabledAssetPacks = new List<string>(loadedDisabledAssetPacks);
                Settings.modOrder = new List<string>(loadedModOrder);
                Settings.assetPackOrder = new List<string>(loadedAssetPackOrder);
                Flow.Back();
            });
        }

        void Apply() => ModInstalls.ReloadContent();

        void Reload()
        {
            MenuFlow.OpenOnStart = typeof(ModsScreen);
            ModInstalls.ReloadContent();
        }
    }
}
