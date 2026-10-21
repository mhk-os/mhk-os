using System.Collections.Generic;
using MHK.Modding;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Status effect inspector: fields an effect takes from its reaction follow it, edited fields become overrides (bold
    /// with a blue bar), right-click reverts one to the reaction's value.
    /// </summary>
    [CustomEditor(typeof(StatusEffectDefinition))]
    public class StatusEffectDefinitionEditor : UnityEditor.Editor
    {
        static readonly Color OverrideBar = new(0.06f, 0.5f, 0.9f);

        readonly Dictionary<string, object> snapshot = new();
        readonly Dictionary<string, PropertyField> fields = new();
        StatusEffectDefinition snapshotReaction;
        string snapshotOverrides;
        HelpBox info;
        bool syncing;

        StatusEffectDefinition Effect => (StatusEffectDefinition)target;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            info = new HelpBox("", HelpBoxMessageType.Info);
            root.Add(info);
            var it = serializedObject.GetIterator();
            for (var enter = true; it.NextVisible(enter); enter = false)
            {
                var field = new PropertyField(it.Copy());
                if (it.propertyPath == "m_Script") field.SetEnabled(false);
                if (EffectReactions.IsInheritable(it.name)) fields[it.name] = field;
                root.Add(field);
            }
            TakeSnapshot();
            root.TrackSerializedObjectValue(serializedObject, _ => OnChanged());
            Refresh();
            return root;
        }

        void OnChanged()
        {
            if (syncing) return;
            syncing = true;
            var e = Effect;
            Undo.RecordObject(e, "Effect reaction");
            if (e.reaction != null && e.reaction == snapshotReaction && string.Join(",", e.overrides) == snapshotOverrides)
                MarkEditedAsOverrides(e);
            if (e.reaction != null && EffectReactions.LeadsBackTo(e.reaction, e))
                Debug.LogWarning($"Effect {e.Id}: its reaction leads back to itself, pick another reaction.");
            EffectReactions.Bake(e);
            serializedObject.Update();
            TakeSnapshot();
            Refresh();
            syncing = false;
        }

        void MarkEditedAsOverrides(StatusEffectDefinition e)
        {
            foreach (var f in EffectReactions.Inheritable)
                if (!Equals(f.GetValue(e), snapshot[f.Name]) && !e.overrides.Contains(f.Name)) e.overrides.Add(f.Name);
            EditorUtility.SetDirty(e);
        }

        void TakeSnapshot()
        {
            var e = Effect;
            foreach (var f in EffectReactions.Inheritable) snapshot[f.Name] = f.GetValue(e);
            snapshotReaction = e.reaction;
            snapshotOverrides = string.Join(",", e.overrides);
        }

        void Refresh()
        {
            var e = Effect;
            foreach (var pair in fields)
            {
                var overridden = e.reaction != null && EffectReactions.IsOverridden(e, pair.Key);
                pair.Value.style.unityFontStyleAndWeight = overridden ? FontStyle.Bold : FontStyle.Normal;
                pair.Value.style.borderLeftWidth = overridden ? 2f : 0f;
                pair.Value.style.borderLeftColor = OverrideBar;
            }
            info.text = InfoText(e);
        }

        static string InfoText(StatusEffectDefinition e)
        {
            var usedBy = EffectReactions.BasedOn(e).Count;
            var users = usedBy > 0 ? $"\n{usedBy} effect(s) are based on this one and follow its changes." : "";
            if (e.reaction == null)
                return "Standalone effect. Pick a shared reaction (core.reaction.blast, flatten, spinout...) to take its " +
                       "values and change only what differs." + users;
            return $"Based on {e.reaction.DisplayName} ({e.reaction.Id}). Bold fields with a blue bar are this effect's own " +
                   "values, the others follow the reaction. Editing a field overrides it. Right-click a field and choose " +
                   "Revert to Reaction Value to follow the reaction again." + users;
        }

        [InitializeOnLoadMethod]
        static void RegisterRevertMenu() => EditorApplication.contextualPropertyMenu += AddRevertItem;

        static void AddRevertItem(GenericMenu menu, SerializedProperty property)
        {
            if (property.serializedObject.targetObject is not StatusEffectDefinition e || e.reaction == null) return;
            var dot = property.propertyPath.IndexOf('.');
            var name = dot < 0 ? property.propertyPath : property.propertyPath[..dot];
            if (!EffectReactions.IsOverridden(e, name)) return;
            menu.AddItem(new GUIContent("Revert to Reaction Value"), false, () => Revert(e, name));
        }

        static void Revert(StatusEffectDefinition e, string field)
        {
            Undo.RecordObject(e, "Revert to reaction value");
            e.overrides.Remove(field);
            EffectReactions.Bake(e);
            EditorUtility.SetDirty(e);
        }
    }
}
