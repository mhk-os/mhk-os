using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace MHK.Modding
{
    /// <summary>
    /// Points the definition references of registered content (fields of a <see cref="ContentDefinition"/> type, lists
    /// and arrays of them) at the registered definition with the same id. An AssetBundle build copies content from other
    /// packs, so a mod's reference to a core kart or track becomes the registered one, and replacements are followed.
    /// </summary>
    static partial class ContentReferenceResolver
    {
        [NoAutoStaticsCleanup] static readonly Dictionary<Type, FieldInfo[]> ReferenceFieldsByType = new();
        [NoAutoStaticsCleanup] static readonly List<Change> recorded = new();

        struct Change
        {
            public ContentDefinition def;
            public FieldInfo field;
            public int index;
            public ContentDefinition original;
        }

        sealed class Context
        {
            public ContentRegistry registry;
            public bool shared;
            public HashSet<string> ownPacks;
            public List<string> dropped;
        }

        /// <summary>
        /// Rewires the references of every definition in <paramref name="pack"/>, returns how many changed.
        /// <paramref name="shared"/> packs outlive the mods (the game's own and project packs): their changes are recorded
        /// for <see cref="RestoreRecorded"/>. In the other (bundle) packs a reference to another pack's content that is
        /// not registered (its mod is disabled or missing) is a bundle stub: it is removed, its id added to
        /// <paramref name="dropped"/> and the field's declared ids kept on the definition (<see cref="ContentDefinition.TryGetUnresolved"/>).
        /// </summary>
        public static int Resolve(ContentRegistry registry, ContentPack pack, bool shared, List<string> dropped)
        {
            var ctx = new Context { registry = registry, shared = shared, ownPacks = OwnPacks(registry.RootOf(pack) ?? pack), dropped = dropped };
            var remapped = 0;
            foreach (var def in pack.content)
            {
                if (def == null) continue;
                foreach (var f in ReferenceFields(def.GetType())) remapped += ResolveField(ctx, def, f);
            }
            return remapped;
        }

        /// <summary>Undoes every recorded change, newest first.</summary>
        public static void RestoreRecorded()
        {
            for (var i = recorded.Count - 1; i >= 0; i--)
            {
                var c = recorded[i];
                if (c.def == null) continue;
                if (c.index < 0) c.field.SetValue(c.def, c.original);
                else if (c.field.GetValue(c.def) is IList list && c.index < list.Count) list[c.index] = c.original;
            }
            recorded.Clear();
        }

        static HashSet<string> OwnPacks(ContentPack root)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in root.SelfAndIncluded()) ids.Add(p.packId);
            return ids;
        }

        static int ResolveField(Context ctx, ContentDefinition def, FieldInfo f)
        {
            var value = f.GetValue(def);
            if (value is ContentDefinition single)
            {
                var resolved = Registered(ctx, single);
                if (resolved == single) return 0;
                if (ctx.shared) recorded.Add(new Change { def = def, field = f, index = -1, original = single });
                if (resolved == null) def.RecordUnresolved(f.Name, new[] { single.Id });
                f.SetValue(def, resolved);
                return 1;
            }
            return value is IList list ? ResolveList(ctx, def, f, list) : 0;
        }

        // A dropped list entry is removed (a List) or left null (an array).
        static int ResolveList(Context ctx, ContentDefinition def, FieldInfo f, IList list)
        {
            var remapped = 0;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is not ContentDefinition item) continue;
                var resolved = Registered(ctx, item);
                if (resolved == item) continue;
                remapped++;
                if (ctx.shared) recorded.Add(new Change { def = def, field = f, index = i, original = item });
                // Before the first removal entries after i are unchanged or resolved to the same id: the list still
                // holds the declared ids.
                if (resolved == null && !def.TryGetUnresolved(f.Name, out _)) def.RecordUnresolved(f.Name, DeclaredIds(list));
                if (resolved == null && !list.IsFixedSize) list.RemoveAt(i);
                else list[i] = resolved;
            }
            return remapped;
        }

        static string[] DeclaredIds(IList list)
        {
            var ids = new string[list.Count];
            for (var i = 0; i < ids.Length; i++) ids[i] = list[i] is ContentDefinition d && d != null ? d.Id : null;
            return ids;
        }

        static ContentDefinition Registered(Context ctx, ContentDefinition d)
        {
            if (d == null) return null;
            var registered = ctx.registry.Get<ContentDefinition>(d.Id);
            if (registered != null) return registered.GetType() != d.GetType() ? d : registered;
            if (ctx.shared || d.OwnerPack != null || ctx.ownPacks.Contains(IdPack(d.Id))) return d;
            ctx.dropped?.Add(d.Id);
            return null;
        }

        static string IdPack(string id)
        {
            var dot = id.IndexOf('.');
            return dot > 0 ? id.Substring(0, dot) : id;
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
