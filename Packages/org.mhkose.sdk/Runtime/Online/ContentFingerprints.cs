using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MHK.Ghosts;
using MHK.Modding;
using MHK.Powerups;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Gameplay fingerprints of content definitions for the pack manifests (docs/protocol.md, content agreement): hex of
    /// the first 128 bits of SHA-256 over the values that change how a race plays, never visuals or sounds. Karts, effects
    /// and track definitions share the ghost hashes' value lists (<see cref="GhostFingerprint"/>); powerups add the
    /// serialized fields of the behaviours on their prefabs, with asset references written as content ids or names.
    /// </summary>
    public static class ContentFingerprints
    {
        /// <summary>First token of every fingerprint. Change it whenever a value list here changes.</summary>
        public const string Scheme = "cf1";

        const int CurveSamples = 11;
        const int MaxDepth = 6;

        static readonly Dictionary<Type, FieldInfo[]> fieldsOf = new();

        /// <summary>The manifest kind of <paramref name="d"/>, null for definitions that are not gameplay content (reactions of other kinds, ...).</summary>
        public static ContentKind? KindOf(ContentDefinition d) => d switch
        {
            TrackDefinition t => t.layout == TrackLayout.Arena ? ContentKind.Arena : ContentKind.RaceTrack,
            KartDefinition => ContentKind.Kart,
            CharacterDefinition => ContentKind.Character,
            PowerupDefinition => ContentKind.Powerup,
            StatusEffectDefinition => ContentKind.StatusEffect,
            CupDefinition => ContentKind.Cup,
            _ => null,
        };

        /// <summary>Fingerprint of <paramref name="d"/> (one of the kinds <see cref="KindOf"/> knows).</summary>
        public static string Of(ContentDefinition d)
        {
            using var b = new FingerprintBuilder();
            b.Add(Scheme);
            switch (d)
            {
                case TrackDefinition t:
                    b.Add("track");
                    GhostFingerprint.AddContentTrack(b, t);
                    b.Add(t.scene != null ? t.scene.Path : "");
                    b.Add(t.defaultLaps);
                    b.Add((int)t.layout);
                    b.Add(t.variant ?? "");
                    break;
                case KartDefinition k:
                    b.Add("kart");
                    GhostFingerprint.AddContentKart(b, k);
                    break;
                case CharacterDefinition c:
                    b.Add("character");
                    b.Add(c.specialPowerup != null ? c.specialPowerup.Id : "");
                    b.Add(c.defaultKart != null ? c.defaultKart.Id : "");
                    break;
                case PowerupDefinition p:
                    b.Add("powerup");
                    AddPowerup(b, p);
                    break;
                case StatusEffectDefinition e:
                    b.Add("effect");
                    GhostFingerprint.AddContentEffect(b, e);
                    break;
                case CupDefinition cup:
                    b.Add("cup");
                    b.Add(cup.tracks.Count);
                    foreach (var t in cup.tracks) b.Add(t != null ? t.Id : "null");
                    b.Add(cup.lapsOverride);
                    break;
            }
            return b.Finish();
        }

        static void AddPowerup(FingerprintBuilder b, PowerupDefinition p)
        {
            b.Add(p.charges);
            b.Add(p.battleCharges);
            b.Add(p.useCooldown);
            b.Add(p.excludeFromItemBoxes);
            b.Add((int)p.heldMount);
            b.Add(p.family ?? "");
            for (var i = 0; i < CurveSamples; i++) b.Add(p.WeightFor(i / (CurveSamples - 1f)));
            AddBehaviours(b, p.usePrefab);
            AddBehaviours(b, p.heldPrefab);
        }

        // Every PowerupBehaviour on the prefab in hierarchy order: its path below the prefab root, type and serialized fields.
        static void AddBehaviours(FingerprintBuilder b, GameObject prefab)
        {
            if (b.AddNull(prefab)) return;
            var behaviours = prefab.GetComponentsInChildren<PowerupBehaviour>(true);
            b.Add(behaviours.Length);
            foreach (var behaviour in behaviours)
            {
                b.Add(PathBelow(prefab.transform, behaviour.transform));
                b.Add(behaviour.GetType().FullName);
                AddFields(b, behaviour, behaviour.GetType(), 0);
            }
        }

        static string PathBelow(Transform root, Transform t)
        {
            var path = "";
            for (var p = t; p != null && p != root; p = p.parent) path = path.Length == 0 ? p.name : p.name + "/" + path;
            return path;
        }

        static void AddFields(FingerprintBuilder b, object target, Type type, int depth)
        {
            foreach (var f in SerializedFields(type))
            {
                if (IsPresentation(f.FieldType)) continue;
                b.Add(f.Name);
                AddValue(b, f.GetValue(target), depth + 1);
            }
        }

        // Fields Unity serializes (public or [SerializeField], not [NonSerialized]) of the type and its bases up to the
        // Unity classes, base classes first, each class's fields by name: reflection order is not guaranteed.
        static FieldInfo[] SerializedFields(Type type)
        {
            if (fieldsOf.TryGetValue(type, out var cached)) return cached;
            var chain = new List<Type>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject) && t != typeof(object); t = t.BaseType)
                chain.Insert(0, t);
            var list = new List<FieldInfo>();
            foreach (var t in chain)
            {
                var own = new List<FieldInfo>();
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsNotSerialized || f.IsInitOnly) continue;
                    if (!f.IsPublic && f.GetCustomAttribute<SerializeField>() == null && f.GetCustomAttribute<SerializeReference>() == null) continue;
                    own.Add(f);
                }
                own.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
                list.AddRange(own);
            }
            var fields = list.ToArray();
            fieldsOf[type] = fields;
            return fields;
        }

        // Looks and sounds: never gameplay.
        static bool IsPresentation(Type t) =>
            typeof(AudioClip).IsAssignableFrom(t) || typeof(Texture).IsAssignableFrom(t) || typeof(Sprite).IsAssignableFrom(t)
            || typeof(Material).IsAssignableFrom(t) || typeof(Shader).IsAssignableFrom(t) || typeof(Renderer).IsAssignableFrom(t)
            || typeof(ParticleSystem).IsAssignableFrom(t) || typeof(AudioSource).IsAssignableFrom(t) || typeof(Light).IsAssignableFrom(t)
            || t == typeof(Color) || t == typeof(Color32) || t == typeof(Gradient)
            || (t.IsArray && IsPresentation(t.GetElementType()))
            || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && IsPresentation(t.GetGenericArguments()[0]));

        static void AddValue(FingerprintBuilder b, object v, int depth)
        {
            switch (v)
            {
                case null:
                    b.Add("null");
                    return;
                case UnityEngine.Object o:
                    AddReference(b, o);
                    return;
                case string s:
                    b.Add(s);
                    return;
                case bool flag:
                    b.Add(flag);
                    return;
                case float f:
                    b.Add(f);
                    return;
                case double d:
                    b.Add((float)d);
                    return;
                case Enum e:
                    b.Add(Convert.ToInt64(e).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case int or long or short or byte or sbyte or uint or ushort or ulong or char:
                    b.Add(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case Vector2 v2:
                    b.Add(v2);
                    return;
                case Vector3 v3:
                    b.Add(v3);
                    return;
                case Vector4 v4:
                    b.Add(v4.x);
                    b.Add(v4.y);
                    b.Add(v4.z);
                    b.Add(v4.w);
                    return;
                case Vector2Int vi2:
                    b.Add(vi2.x);
                    b.Add(vi2.y);
                    return;
                case Vector3Int vi3:
                    b.Add(vi3.x);
                    b.Add(vi3.y);
                    b.Add(vi3.z);
                    return;
                case Quaternion q:
                    b.Add(q.x);
                    b.Add(q.y);
                    b.Add(q.z);
                    b.Add(q.w);
                    return;
                case LayerMask mask:
                    b.Add(mask.value);
                    return;
                case AnimationCurve curve:
                    b.Add(curve.length);
                    if (curve.length == 0) return;
                    var keys = curve.keys;
                    var start = keys[0].time;
                    var end = keys[keys.Length - 1].time;
                    for (var i = 0; i < CurveSamples; i++) b.Add(curve.Evaluate(Mathf.Lerp(start, end, i / (CurveSamples - 1f))));
                    return;
                case IList list:
                    b.Add(list.Count);
                    foreach (var item in list) AddValue(b, item, depth);
                    return;
            }
            var type = v.GetType();
            if (depth >= MaxDepth || !(type.IsSerializable || type.IsValueType))
            {
                b.Add(type.Name);
                return;
            }
            AddFields(b, v, type, depth);
        }

        // Assets are named by what identifies them on every machine: a content id, else the asset's type and name.
        static void AddReference(FingerprintBuilder b, UnityEngine.Object o)
        {
            if (b.AddNull(o)) return;
            switch (o)
            {
                case ContentDefinition d:
                    b.Add(d.Id);
                    return;
                case AnimationClip clip:
                    b.Add(clip.name);
                    b.Add(clip.length);
                    return;
                default:
                    b.Add(o.GetType().Name);
                    b.Add(o.name);
                    return;
            }
        }
    }
}
