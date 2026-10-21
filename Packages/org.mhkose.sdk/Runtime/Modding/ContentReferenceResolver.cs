using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Points the definition references of bundle-loaded content (fields of a <see cref="ContentDefinition"/> type, lists
    /// and arrays of them) at the registered definition with the same id. An AssetBundle build copies content from other
    /// packs, so a mod's reference to a core kart or track becomes the registered one, and overrides are followed.
    /// </summary>
    static partial class ContentReferenceResolver
    {
        [NoAutoStaticsCleanup] static readonly Dictionary<Type, FieldInfo[]> ReferenceFieldsByType = new();

        /// <summary>Rewires the references of every definition in <paramref name="pack"/>, returns how many changed.</summary>
        public static int Resolve(ContentRegistry registry, ContentPack pack)
        {
            var remapped = 0;
            foreach (var def in pack.content)
            {
                if (def == null) continue;
                foreach (var f in ReferenceFields(def.GetType())) remapped += ResolveField(registry, def, f);
            }
            return remapped;
        }

        static int ResolveField(ContentRegistry registry, ContentDefinition def, FieldInfo f)
        {
            var remapped = 0;
            var value = f.GetValue(def);
            if (value is ContentDefinition single)
            {
                var resolved = Registered(registry, single);
                if (resolved != single) remapped++;
                f.SetValue(def, resolved);
            }
            else if (value is IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is not ContentDefinition item) continue;
                    var resolved = Registered(registry, item);
                    if (resolved != item) remapped++;
                    list[i] = resolved;
                }
            }
            return remapped;
        }

        static ContentDefinition Registered(ContentRegistry registry, ContentDefinition d)
        {
            if (d == null) return null;
            var registered = registry.Get<ContentDefinition>(d.Id);
            return registered == null || registered.GetType() != d.GetType() ? d : registered;
        }

        static FieldInfo[] ReferenceFields(Type type)
        {
            if (ReferenceFieldsByType.TryGetValue(type, out var fields)) return fields;
            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(ScriptableObject); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (IsReferenceField(f)) list.Add(f);
            return ReferenceFieldsByType[type] = list.ToArray();
        }

        static bool IsReferenceField(FieldInfo f)
        {
            if (!f.IsPublic && f.GetCustomAttribute<SerializeField>() == null) return false;
            var ft = f.FieldType;
            var element = ft.IsArray ? ft.GetElementType()
                : ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>) ? ft.GetGenericArguments()[0]
                : ft;
            return typeof(ContentDefinition).IsAssignableFrom(element);
        }
    }
}
