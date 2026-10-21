using System.Collections.Generic;
using System.Reflection;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Keeps status effects in step with their shared reaction (<see cref="StatusEffectDefinition.reaction"/>): every
    /// field not listed in <see cref="StatusEffectDefinition.overrides"/> is copied from the reaction whenever an effect
    /// asset changes. The game reads the copied values, so nothing is resolved at run time.
    /// </summary>
    public class EffectReactions : AssetPostprocessor
    {
        const int MaxChainDepth = 8;

        static FieldInfo[] inheritable;
        static HashSet<string> inheritableNames;

        /// <summary>Fields an effect takes from its reaction: every public field of the definition except the reaction link itself.</summary>
        public static FieldInfo[] Inheritable => inheritable ??= Collect();

        /// <summary>Whether <paramref name="field"/> (a top-level serialized field name) can come from a reaction.</summary>
        public static bool IsInheritable(string field)
        {
            if (inheritableNames == null)
            {
                inheritableNames = new HashSet<string>();
                foreach (var f in Inheritable) inheritableNames.Add(f.Name);
            }
            return inheritableNames.Contains(field);
        }

        /// <summary>Whether <paramref name="effect"/> sets <paramref name="field"/> itself instead of taking it from its reaction.</summary>
        public static bool IsOverridden(StatusEffectDefinition effect, string field) =>
            effect.overrides != null && effect.overrides.Contains(field);

        static FieldInfo[] Collect()
        {
            var list = new List<FieldInfo>();
            foreach (var f in typeof(StatusEffectDefinition).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (f.Name != nameof(StatusEffectDefinition.reaction) && f.Name != nameof(StatusEffectDefinition.overrides))
                    list.Add(f);
            return list.ToArray();
        }

        /// <summary>
        /// Copies the fields <paramref name="effect"/> does not override from its reaction as the reaction is now. True
        /// when a value changed. The asset is then marked dirty, saving is up to the caller.
        /// </summary>
        public static bool Bake(StatusEffectDefinition effect)
        {
            var reaction = effect.reaction;
            if (reaction == null || LeadsBackTo(reaction, effect)) return false;
            var changed = false;
            foreach (var f in Inheritable)
            {
                if (IsOverridden(effect, f.Name)) continue;
                var value = f.GetValue(reaction);
                if (SameValue(f, f.GetValue(effect), value)) continue;
                f.SetValue(effect, CopyOf(f, value));
                changed = true;
            }
            if (changed) EditorUtility.SetDirty(effect);
            return changed;
        }

        // Nested settings (bodyBob) are compared and copied by value: sharing the reaction's instance would tie both assets.
        static bool IsNested(FieldInfo f) => f.FieldType.IsClass && f.FieldType != typeof(string) && !typeof(Object).IsAssignableFrom(f.FieldType);

        static bool SameValue(FieldInfo f, object a, object b) =>
            IsNested(f) ? JsonUtility.ToJson(a) == JsonUtility.ToJson(b) : Equals(a, b);

        static object CopyOf(FieldInfo f, object value) =>
            IsNested(f) && value != null ? JsonUtility.FromJson(JsonUtility.ToJson(value), f.FieldType) : value;

        /// <summary>True when following the reaction links from <paramref name="start"/> reaches <paramref name="effect"/> (a cycle).</summary>
        public static bool LeadsBackTo(StatusEffectDefinition start, StatusEffectDefinition effect)
        {
            var r = start;
            for (var i = 0; r != null && i <= MaxChainDepth; i++, r = r.reaction)
                if (r == effect) return true;
            return false;
        }

        /// <summary>Every effect asset of the project whose reaction chain contains <paramref name="reaction"/>.</summary>
        public static List<StatusEffectDefinition> BasedOn(StatusEffectDefinition reaction)
        {
            var result = new List<StatusEffectDefinition>();
            foreach (var e in AllEffects())
                if (e != reaction && e.reaction != null && LeadsBackTo(e.reaction, reaction)) result.Add(e);
            return result;
        }

        /// <summary>
        /// Bakes every effect of the project that is based on a reaction, reactions before the effects based on them, and
        /// saves the ones that changed.
        /// </summary>
        [MenuItem("MHK/Content/Sync Effect Reactions")]
        public static void SyncAll()
        {
            var effects = new List<StatusEffectDefinition>(AllEffects());
            effects.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
            var changed = new List<string>();
            foreach (var e in effects)
            {
                if (!Bake(e)) continue;
                AssetDatabase.SaveAssetIfDirty(e);
                changed.Add(e.Id);
            }
            if (changed.Count > 0) Debug.Log($"Effect reactions: updated {string.Join(", ", changed)} from their reactions.");
        }

        static int Depth(StatusEffectDefinition effect)
        {
            var depth = 0;
            for (var r = effect.reaction; r != null && depth <= MaxChainDepth; r = r.reaction) depth++;
            return depth;
        }

        static IEnumerable<StatusEffectDefinition> AllEffects()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(StatusEffectDefinition)))
            {
                var e = AssetDatabase.LoadAssetAtPath<StatusEffectDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (e != null) yield return e;
            }
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (var path in imported)
            {
                if (!path.EndsWith(".asset") || AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(StatusEffectDefinition)) continue;
                // Saving inside the import callback would re-enter it: sync once the import has finished.
                EditorApplication.delayCall -= SyncAll;
                EditorApplication.delayCall += SyncAll;
                return;
            }
        }
    }
}
