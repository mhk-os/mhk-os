using System;
using System.Collections.Generic;
using System.Linq;
using MHK.Modding;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Shrinks the clips and meshes stored in <see cref="ModelLibrary"/> assets: drops redundant animation keys and rig
    /// curves, merges identical clips across a pack's libraries and makes render-only meshes non-readable.
    /// </summary>
    public static class ModelLibraryOptimizer
    {
        /// <summary>Largest change of a rotation curve in quaternion components (about 0.001 degrees).</summary>
        public const float RotationTolerance = 1e-5f;
        /// <summary>Largest change of other curves, relative to their magnitude (positions: to the clip's largest offset).</summary>
        public const float RelativeTolerance = 1e-5f;
        /// <summary>Upper bound of the position tolerance in model units, for props placed far from their origin.</summary>
        public const float MaxPositionTolerance = 1e-4f;

        const string PositionProperty = "m_LocalPosition";
        const string RotationProperty = "m_LocalRotation";
        const string ScaleProperty = "m_LocalScale";
        const string EditorCurvesProperty = "m_EditorCurves";
        const string EulerEditorCurvesProperty = "m_EulerEditorCurves";
        const float TangentMatch = 1e-5f;
        const float MinPositionScale = 1e-3f;

        /// <summary>Totals of an optimizer run.</summary>
        public sealed class Report
        {
            public int clips, curvesBefore, curvesAfter, keysBefore, keysAfter, clipsMerged;
            public int meshesReadable, meshesNonReadable;
            public long nonReadableCpuBytes;

            public override string ToString() =>
                $"{clips} clips: curves {curvesBefore} -> {curvesAfter}, keys {keysBefore} -> {keysAfter}, {clipsMerged} merged. " +
                $"Meshes: {meshesReadable} readable, {meshesNonReadable} non-readable ({nonReadableCpuBytes / 1048576f:F1} MB without CPU copy).";
        }

        /// <summary>The curves of one animated property: all components of a position, rotation or scale, else one curve.</summary>
        sealed class CurveGroup
        {
            public string path, property;
            public Type type;
            public readonly List<EditorCurveBinding> bindings = new();
            public readonly List<AnimationCurve> curves = new();

            public (string, Type, string) Key => (path, type, property);

            public bool IsTransform => type == typeof(Transform) &&
                                       property is PositionProperty or RotationProperty or ScaleProperty;
        }

        sealed class ClipData
        {
            public AnimationClip clip;
            public List<CurveGroup> groups;
            public float positionScale;
        }

        /// <summary>Clips and the rigs that play them. Definitions that share a clip or a library form one family.</summary>
        sealed class Family
        {
            public readonly HashSet<AnimationClip> clips = new();
            public readonly HashSet<GameObject> rigs = new();
            public bool rigsKnown = true;
        }

        [MenuItem("MHK/Content/Optimize Model Libraries")]
        static void OptimizeAllMenu() => Debug.Log("[ModelLibraryOptimizer] " + OptimizeAll());

        /// <summary>Runs <see cref="OptimizeLibraries"/> over every library in the project.</summary>
        public static Report OptimizeAll() => OptimizeLibraries(EditorAssets.FindAll<ModelLibrary>().ToList());

        /// <summary>
        /// Merges identical clips per pack folder, optimizes the clips of <paramref name="libraries"/> with the rigs of
        /// the character and kart definitions that play them, and updates mesh readability. Run it after the scenes,
        /// prefabs and definitions that use the libraries are saved (an importer: after the whole pack).
        /// </summary>
        public static Report OptimizeLibraries(IReadOnlyCollection<ModelLibrary> libraries, bool meshes = true)
        {
            var report = new Report();
            foreach (var pack in libraries.GroupBy(PackFolder))
                report.clipsMerged += Math.Max(0, MergeIdenticalClips(pack, pack.Key));
            foreach (var family in Families(libraries))
                OptimizeClips(family.clips, family.rigsKnown ? family.rigs : Array.Empty<GameObject>(), report);
            if (meshes) UpdateMeshReadability(libraries, report);
            AssetDatabase.SaveAssets();
            return report;
        }

        /// <summary>
        /// Drops keys of <paramref name="clips"/> that linear interpolation reproduces within the tolerances, collapses
        /// constant curves to their two end keys and, when <paramref name="rigs"/> lists every model that plays the
        /// clips, removes the transform curves that hold a rig part at its rest pose in all clips (or target no part).
        /// Clip lengths stay. Empty <paramref name="rigs"/>: keys only.
        /// </summary>
        public static void OptimizeClips(IEnumerable<AnimationClip> clips, IReadOnlyCollection<GameObject> rigs, Report report = null)
        {
            report ??= new Report();
            var data = clips.Where(c => c != null).Distinct().Select(Read).ToList();
            var removable = rigs.Count > 0 ? RemovableGroups(data, rigs) : new HashSet<(string, Type, string)>();
            foreach (var clip in data) Apply(clip, removable, report);
        }

        /// <summary>
        /// Merges clips with identical curves and settings across <paramref name="libraries"/> (one pack: mods stay
        /// self-contained, so references are repointed under its folder <paramref name="packFolder"/>). References move
        /// to the first copy in library path order and the other copies are deleted. Returns the number of removed clips,
        /// -1 when an open scene blocked the merge.
        /// </summary>
        public static int MergeIdenticalClips(IEnumerable<ModelLibrary> libraries, string packFolder)
        {
            var survivors = new Dictionary<Hash128, AnimationClip>();
            var map = new Dictionary<AssetReferences.Ref, AssetReferences.Ref>();
            var copies = new List<AnimationClip>();
            foreach (var clip in libraries.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal).SelectMany(SubAssets<AnimationClip>))
            {
                var key = ContentHash(clip);
                if (!survivors.TryGetValue(key, out var keep))
                {
                    survivors[key] = clip;
                    continue;
                }
                if (!AssetReferences.Ref.TryOf(clip, out var from) || !AssetReferences.Ref.TryOf(keep, out var to)) continue;
                map[from] = to;
                copies.Add(clip);
            }
            if (copies.Count == 0) return 0;
            if (!AssetReferences.Repoint(map, out _, packFolder)) return -1;
            foreach (var clip in copies)
            {
                var library = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(clip));
                AssetDatabase.RemoveObjectFromAsset(clip);
                Object.DestroyImmediate(clip, true);
                EditorUtility.SetDirty(library);
            }
            AssetDatabase.SaveAssets();
            return copies.Count;
        }

        /// <summary>
        /// Sets Read/Write on every mesh of <paramref name="libraries"/>: on for MeshCollider, SkinnedMeshRenderer and
        /// particle meshes in any scene or prefab, off for the rest. Run it after the scenes that use the libraries are saved.
        /// </summary>
        public static void UpdateMeshReadability(IEnumerable<ModelLibrary> libraries, Report report = null)
        {
            report ??= new Report();
            var keep = AssetReferences.CpuMeshReferences();
            foreach (var mesh in libraries.SelectMany(SubAssets<Mesh>))
            {
                var readable = AssetReferences.Ref.TryOf(mesh, out var r) && keep.Contains(r);
                if (readable) report.meshesReadable++;
                else
                {
                    report.meshesNonReadable++;
                    report.nonReadableCpuBytes += CpuBytes(mesh);
                }
                if (mesh.isReadable == readable) continue;
                MeshWriteScope.SetReadable(mesh, readable);
                EditorUtility.SetDirty(mesh);
            }
        }

        /// <summary>Bytes of the CPU copy a readable mesh keeps: vertex streams and index buffer.</summary>
        public static long CpuBytes(Mesh mesh)
        {
            long bytes = 0;
            for (var s = 0; s < mesh.vertexBufferCount; s++) bytes += (long)mesh.GetVertexBufferStride(s) * mesh.vertexCount;
            var indexSize = mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2;
            for (var i = 0; i < mesh.subMeshCount; i++) bytes += (long)mesh.GetIndexCount(i) * indexSize;
            return bytes;
        }

        static IEnumerable<T> SubAssets<T>(ModelLibrary library) where T : Object =>
            AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(library)).OfType<T>().OrderBy(o => o.name, StringComparer.Ordinal);

        // "Assets/Mods/<pack>" or "Packages/<package>".
        static string PackFolder(ModelLibrary library)
        {
            var parts = AssetDatabase.GetAssetPath(library).Split('/');
            return string.Join("/", parts.Take(parts[0] == "Packages" ? 2 : 3));
        }

        // ------------------------------------------------------------------ families

        static List<Family> Families(IReadOnlyCollection<ModelLibrary> libraries)
        {
            var families = new List<Family>();
            var byClip = new Dictionary<AnimationClip, Family>();
            var libraryClips = new HashSet<AnimationClip>(libraries.SelectMany(SubAssets<AnimationClip>));
            foreach (var library in libraries) Join(families, byClip, SubAssets<AnimationClip>(library), null);
            foreach (var c in EditorAssets.FindAll<CharacterDefinition>())
                JoinDefinition(families, byClip, libraryClips, c.animations, c.driverPrefab);
            foreach (var k in EditorAssets.FindAll<KartDefinition>())
                JoinDefinition(families, byClip, libraryClips, k.animations, k.kartPrefab);
            return families;
        }

        static void JoinDefinition(List<Family> families, Dictionary<AnimationClip, Family> byClip, HashSet<AnimationClip> libraryClips,
                                   List<CharacterAnimBinding> animations, GameObject prefab)
        {
            var clips = animations.Select(a => a.clip).Where(libraryClips.Contains).ToList();
            if (clips.Count == 0) return;
            var animator = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
            var family = Join(families, byClip, clips, animator != null ? animator.gameObject : null);
            if (animator == null) family.rigsKnown = false;
        }

        static Family Join(List<Family> families, Dictionary<AnimationClip, Family> byClip, IEnumerable<AnimationClip> clips, GameObject rig)
        {
            var family = new Family();
            families.Add(family);
            if (rig != null) family.rigs.Add(rig);
            foreach (var clip in clips)
            {
                if (byClip.TryGetValue(clip, out var other) && other != family)
                {
                    family.clips.UnionWith(other.clips);
                    family.rigs.UnionWith(other.rigs);
                    family.rigsKnown &= other.rigsKnown;
                    foreach (var moved in other.clips) byClip[moved] = family;
                    families.Remove(other);
                }
                family.clips.Add(clip);
                byClip[clip] = family;
            }
            return family;
        }

        static ClipData Read(AnimationClip clip)
        {
            var groups = new Dictionary<(string, Type, string), CurveGroup>();
            var data = new ClipData { clip = clip, groups = new List<CurveGroup>() };
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var dot = b.propertyName.LastIndexOf('.');
                var property = b.type == typeof(Transform) && dot > 0 ? b.propertyName.Substring(0, dot) : b.propertyName;
                if (!groups.TryGetValue((b.path, b.type, property), out var g))
                {
                    g = new CurveGroup { path = b.path, type = b.type, property = property };
                    groups[g.Key] = g;
                    data.groups.Add(g);
                }
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                g.bindings.Add(b);
                g.curves.Add(curve);
                if (property != PositionProperty) continue;
                for (var k = 0; k < curve.length; k++) data.positionScale = Mathf.Max(data.positionScale, Mathf.Abs(curve[k].value));
            }
            return data;
        }

        static float Tolerance(CurveGroup g, ClipData data)
        {
            if (g.property == RotationProperty) return RotationTolerance;
            if (g.property == PositionProperty)
                return Mathf.Min(RelativeTolerance * Mathf.Max(data.positionScale, MinPositionScale), MaxPositionTolerance);
            var max = 1f;
            foreach (var c in g.curves)
                for (var k = 0; k < c.length; k++) max = Mathf.Max(max, Mathf.Abs(c[k].value));
            return RelativeTolerance * max;
        }

        static Hash128 ContentHash(AnimationClip clip)
        {
            var h = new Hash128();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            h.Append(clip.frameRate);
            h.Append((int)clip.wrapMode);
            h.Append(settings.loopTime ? 1 : 0);
            h.Append(settings.cycleOffset);
            h.Append(AnimationUtility.GetAnimationEvents(clip).Length);
            foreach (var b in AnimationUtility.GetCurveBindings(clip).OrderBy(b => b.path + "|" + b.type.FullName + "|" + b.propertyName, StringComparer.Ordinal))
            {
                h.Append(b.path);
                h.Append(b.type.FullName);
                h.Append(b.propertyName);
                foreach (var k in AnimationUtility.GetEditorCurve(clip, b).keys)
                {
                    h.Append(k.time);
                    h.Append(k.value);
                    h.Append(k.inTangent);
                    h.Append(k.outTangent);
                    h.Append((int)k.weightedMode);
                }
            }
            return h;
        }

        static HashSet<(string, Type, string)> RemovableGroups(List<ClipData> clips, IReadOnlyCollection<GameObject> rigs)
        {
            var verdict = new Dictionary<(string, Type, string), bool>();
            foreach (var data in clips)
                foreach (var g in data.groups)
                    verdict[g.Key] = (!verdict.TryGetValue(g.Key, out var ok) || ok) && CanRemove(g, data, rigs);
            return new HashSet<(string, Type, string)>(verdict.Where(kv => kv.Value).Select(kv => kv.Key));
        }

        static bool CanRemove(CurveGroup g, ClipData data, IReadOnlyCollection<GameObject> rigs)
        {
            if (!g.IsTransform || g.path.Length == 0 || g.curves.Count != (g.property == RotationProperty ? 4 : 3)) return false;
            var parts = rigs.Select(r => r.transform.Find(g.path)).Where(t => t != null).ToList();
            if (parts.Count == 0) return true;
            var tolerance = Tolerance(g, data);
            var keys = g.curves.Select(c => c.keys).ToArray();
            if (!IsConstant(keys, tolerance)) return false;
            var value = Vector4.zero;
            for (var i = 0; i < g.bindings.Count; i++)
            {
                var name = g.bindings[i].propertyName;
                var axis = "xyzw".IndexOf(name[name.Length - 1]);
                if (axis < 0) return false;
                value[axis] = keys[i][0].value;
            }
            return parts.All(t => AtRest(t, g.property, value, tolerance));
        }

        static bool AtRest(Transform part, string property, Vector4 value, float tolerance)
        {
            switch (property)
            {
                case PositionProperty: return Close(part.localPosition, value, tolerance);
                case ScaleProperty: return Close(part.localScale, value, tolerance);
                default:
                    var q = part.localRotation;
                    var rest = new Vector4(q.x, q.y, q.z, q.w);
                    return Close(rest, value, tolerance) || Close(-rest, value, tolerance);
            }
        }

        static bool Close(Vector4 a, Vector4 b, float tolerance) =>
            Mathf.Abs(a.x - b.x) <= tolerance && Mathf.Abs(a.y - b.y) <= tolerance &&
            Mathf.Abs(a.z - b.z) <= tolerance && Mathf.Abs(a.w - b.w) <= tolerance;

        static void PreserveSpan(ClipData data, HashSet<CurveGroup> removed)
        {
            var (start, end) = Span(data.groups);
            var (s, e) = Span(data.groups.Where(g => !removed.Contains(g)));
            foreach (var g in data.groups.Where(removed.Contains).ToList())
            {
                if (s <= start && e >= end) return;
                var (gs, ge) = Span(new[] { g });
                if (gs >= s && ge <= e) continue;
                removed.Remove(g);
                s = Mathf.Min(s, gs);
                e = Mathf.Max(e, ge);
            }
        }

        static (float start, float end) Span(IEnumerable<CurveGroup> groups)
        {
            float s = float.PositiveInfinity, e = float.NegativeInfinity;
            foreach (var c in groups.SelectMany(g => g.curves))
            {
                if (c.length == 0) continue;
                s = Mathf.Min(s, c[0].time);
                e = Mathf.Max(e, c[c.length - 1].time);
            }
            return (s, e);
        }

        static void Apply(ClipData data, HashSet<(string, Type, string)> removable, Report report)
        {
            var removed = new HashSet<CurveGroup>(data.groups.Where(g => removable.Contains(g.Key)));
            PreserveSpan(data, removed);
            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();
            foreach (var g in data.groups)
            {
                report.curvesBefore += g.curves.Count;
                report.keysBefore += g.curves.Sum(c => c.length);
                if (removed.Contains(g)) continue;
                var reduced = Reduce(g, Tolerance(g, data));
                bindings.AddRange(g.bindings);
                curves.AddRange(reduced ?? g.curves.ToArray());
            }
            report.clips++;
            report.curvesAfter += curves.Count;
            report.keysAfter += curves.Sum(c => c.length);
            // Reading the curves filled the clip's editor caches, so it is written back even when nothing was dropped.
            Write(data.clip, bindings, curves);
        }

        static void Write(AnimationClip clip, List<EditorCurveBinding> bindings, List<AnimationCurve> curves)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var wrap = clip.wrapMode;
            // Runtime SetCurve like the importer, without the editor-only curve copies: they would be baked key by key.
            using (var so = new SerializedObject(clip))
            {
                so.FindProperty(EditorCurvesProperty).ClearArray();
                so.FindProperty(EulerEditorCurvesProperty).ClearArray();
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            clip.ClearCurves();
            for (var i = 0; i < bindings.Count; i++) clip.SetCurve(bindings[i].path, bindings[i].type, bindings[i].propertyName, curves[i]);
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            clip.wrapMode = wrap;
            EditorUtility.SetDirty(clip);
        }

        // ------------------------------------------------------------------ key reduction

        static AnimationCurve[] Reduce(CurveGroup g, float tolerance)
        {
            var keys = g.curves.Select(c => c.keys).ToArray();
            if (SharedTimes(keys)) return ToCurves(g.curves, ReduceKeys(keys, tolerance));
            var result = new AnimationCurve[keys.Length];
            var any = false;
            for (var i = 0; i < keys.Length; i++)
            {
                var single = ReduceKeys(new[] { keys[i] }, tolerance);
                any |= single != null;
                result[i] = single != null ? ToCurves(new[] { g.curves[i] }, single)[0] : g.curves[i];
            }
            return any ? result : null;
        }

        static Keyframe[][] ReduceKeys(Keyframe[][] keys, float tolerance)
        {
            var n = keys[0].Length;
            if (n <= 2) return null;
            if (IsConstant(keys, tolerance)) return keys.Select(k => Collapse(k[0], k[n - 1])).ToArray();
            var kept = KeptKeys(keys, tolerance);
            return kept.Count == n ? null : keys.Select(k => Rebuild(k, kept)).ToArray();
        }

        static AnimationCurve[] ToCurves(IReadOnlyList<AnimationCurve> source, Keyframe[][] keys)
        {
            if (keys == null) return null;
            var result = new AnimationCurve[keys.Length];
            for (var i = 0; i < keys.Length; i++)
                result[i] = new AnimationCurve(keys[i]) { preWrapMode = source[i].preWrapMode, postWrapMode = source[i].postWrapMode };
            return result;
        }

        static bool SharedTimes(Keyframe[][] keys)
        {
            for (var c = 1; c < keys.Length; c++)
            {
                if (keys[c].Length != keys[0].Length) return false;
                for (var k = 0; k < keys[0].Length; k++)
                    if (keys[c][k].time != keys[0][k].time) return false;
            }
            return true;
        }

        static bool IsConstant(Keyframe[][] keys, float tolerance)
        {
            foreach (var curve in keys)
                for (var k = 0; k < curve.Length; k++)
                {
                    if (Mathf.Abs(curve[k].value - curve[0].value) > tolerance) return false;
                    if (k + 1 == curve.Length) continue;
                    var dt = curve[k + 1].time - curve[k].time;
                    if (Steep(curve[k].outTangent, dt, tolerance) || Steep(curve[k + 1].inTangent, dt, tolerance)) return false;
                }
            return true;
        }

        static bool Steep(float tangent, float dt, float tolerance) => !float.IsInfinity(tangent) && Mathf.Abs(tangent) * dt > tolerance;

        static Keyframe[] Collapse(Keyframe first, Keyframe last) =>
            new[] { new Keyframe(first.time, first.value, 0f, 0f), new Keyframe(last.time, first.value, 0f, 0f) };

        static List<int> KeptKeys(Keyframe[][] keys, float tolerance)
        {
            var n = keys[0].Length;
            var kept = new List<int> { 0 };
            var a = 0;
            while (a < n - 1)
            {
                var e = a + 1;
                if (IsLinear(keys, a))
                    while (e + 1 < n && IsLinear(keys, e) && WithinTolerance(keys, a, e + 1, tolerance)) e++;
                kept.Add(e);
                a = e;
            }
            return kept;
        }

        static bool IsLinear(Keyframe[][] keys, int k)
        {
            foreach (var curve in keys)
            {
                var dt = curve[k + 1].time - curve[k].time;
                if (dt <= 0f || curve[k].weightedMode != WeightedMode.None || curve[k + 1].weightedMode != WeightedMode.None) return false;
                var slope = (curve[k + 1].value - curve[k].value) / dt;
                if (!Matches(curve[k].outTangent, slope) || !Matches(curve[k + 1].inTangent, slope)) return false;
            }
            return true;
        }

        static bool Matches(float tangent, float slope) =>
            !float.IsInfinity(tangent) && Mathf.Abs(tangent - slope) <= TangentMatch * Mathf.Max(1f, Mathf.Abs(slope));

        static bool WithinTolerance(Keyframe[][] keys, int from, int to, float tolerance)
        {
            foreach (var curve in keys)
            {
                var a = curve[from];
                var b = curve[to];
                for (var k = from + 1; k < to; k++)
                {
                    var line = a.value + (b.value - a.value) * (curve[k].time - a.time) / (b.time - a.time);
                    if (Mathf.Abs(curve[k].value - line) > tolerance) return false;
                }
            }
            return true;
        }

        static Keyframe[] Rebuild(Keyframe[] curve, List<int> kept)
        {
            var result = new Keyframe[kept.Count];
            for (var i = 0; i < kept.Count; i++)
            {
                var key = curve[kept[i]];
                var inTangent = i > 0 ? SegmentTangent(curve, kept[i - 1], kept[i], true) : key.inTangent;
                var outTangent = i + 1 < kept.Count ? SegmentTangent(curve, kept[i], kept[i + 1], false) : key.outTangent;
                result[i] = new Keyframe(key.time, key.value, inTangent, outTangent);
            }
            return result;
        }

        static float SegmentTangent(Keyframe[] curve, int from, int to, bool incoming)
        {
            if (to == from + 1) return incoming ? curve[to].inTangent : curve[from].outTangent;
            return (curve[to].value - curve[from].value) / (curve[to].time - curve[from].time);
        }
    }
}
