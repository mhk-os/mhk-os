using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MHK.Editor.Lighting
{
    /// <summary>Lightmap UV (UV2) checks, coverage and in-place unwrapping of track meshes, readable or not.</summary>
    internal static class MeshLightmapUVs
    {
        const double MinCoverage = 1e-6;
        const double FirstGuessCoverage = 0.5;
        const float MinPackMargin = 1f / 2048f;
        const float MaxPackMargin = 1f / 16f;
        const float UvTolerance = 0.001f;
        const float MinUvSpan = 0.5f;
        const int MaxUvChannels = 8;
        const int LightmapChannel = 1;

        internal enum UnwrapResult { Present, Unwrapped, Failed }

        internal readonly struct UnwrapOutcome
        {
            public readonly UnwrapResult Result;
            public readonly double Coverage;
            public readonly string Why;

            UnwrapOutcome(UnwrapResult result, double coverage, string why)
            {
                Result = result;
                Coverage = coverage;
                Why = why;
            }

            public static UnwrapOutcome Present(double coverage) => new(UnwrapResult.Present, coverage, null);
            public static UnwrapOutcome Unwrapped(double coverage) => new(UnwrapResult.Unwrapped, coverage, null);
            public static UnwrapOutcome Failed(string why) => new(UnwrapResult.Failed, 0, why);
        }

        /// <summary>
        /// Generates UV2 for <paramref name="mesh"/> in place unless it already has normalised, usable ones (or
        /// <paramref name="regenerate"/>). The island margin depends on the coverage it leads to (<paramref name="marginFor"/>),
        /// so the mesh is unwrapped a few times until both agree. A 16-bit mesh that would exceed its vertex limit stays unchanged.
        /// </summary>
        public static UnwrapOutcome Ensure(Mesh mesh, bool regenerate, Func<double, float> marginFor, TrackBakeSettings settings)
        {
            using (var data = MeshReadback.Of(mesh))
            {
                var why = ValidateForUnwrap(data);
                if (why != null) return UnwrapOutcome.Failed(why);
                if (!regenerate && TryExistingUVs(data, out var coverage)) return UnwrapOutcome.Present(coverage);
            }
            using (MeshWriteScope.Open(mesh)) return Unwrap(mesh, marginFor, settings);
        }

        /// <summary>World-space surface area of <paramref name="mesh"/>, 0 for broken meshes (they stay realtime).</summary>
        public static double WorldArea(Mesh mesh, Matrix4x4 m)
        {
            using var data = MeshReadback.Of(mesh);
            if (!ValidIndices(data)) return 0;
            var v = data.Vertices();
            var t = data.Triangles();
            double area = 0;
            for (var i = 0; i < t.Length; i += 3)
            {
                var a = m.MultiplyPoint3x4(v[t[i]]);
                area += 0.5 * Vector3.Cross(m.MultiplyPoint3x4(v[t[i + 1]]) - a, m.MultiplyPoint3x4(v[t[i + 2]]) - a).magnitude;
            }
            return area;
        }

        static string ValidateForUnwrap(MeshReadback data)
        {
            if (!ValidIndices(data)) return "mesh has indices outside its vertices";
            // NaN or infinite vertex data (some original meshes have it in their texture coordinates) makes the
            // lightmapper fail for the whole scene ("AddGeometry failed").
            return FiniteVertexData(data, out var attribute) ? null : $"mesh has NaN or infinite {attribute}";
        }

        // UV2 without area cannot receive a lightmap, so it is unwrapped again. Tiny parts keep their low but real coverage.
        static bool TryExistingUVs(MeshReadback data, out double coverage)
        {
            coverage = 0;
            if (!HasLightmapUVs(data)) return false;
            coverage = Coverage(data);
            return coverage >= MinCoverage;
        }

        static UnwrapOutcome Unwrap(Mesh mesh, Func<double, float> marginFor, TrackBakeSettings settings)
        {
            UnwrapParam.SetDefaults(out var param);
            param.hardAngle = settings.UnwrapHardAngle;
            var margin = marginFor(FirstGuessCoverage);
            Mesh best = null;
            double coverage = 0;
            try
            {
                for (var i = 0; i < settings.UnwrapIterations; i++)
                {
                    var copy = UnwrapOnce(mesh, param, margin);
                    if (copy == null) break;
                    if (best != null) Object.DestroyImmediate(best);
                    best = copy;
                    coverage = Coverage(copy);
                    var wanted = marginFor(Math.Max(coverage, settings.MinCoverageFloor));
                    if (Mathf.Abs(wanted - margin) < settings.MarginConvergence * margin) break;
                    margin = wanted;
                }
                return Commit(mesh, best, coverage);
            }
            finally
            {
                if (best != null) Object.DestroyImmediate(best);
            }
        }

        static Mesh UnwrapOnce(Mesh mesh, UnwrapParam param, float margin)
        {
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            FlattenBaseVertices(mesh, copy);
            param.packMargin = Mathf.Clamp(margin, MinPackMargin, MaxPackMargin);
            if (Unwrapping.GenerateSecondaryUVSet(copy, param)) return copy;
            Object.DestroyImmediate(copy);
            return null;
        }

        // The unwrapper corrupts submeshes with a base vertex (writes index 65535), so they are flattened first.
        static void FlattenBaseVertices(Mesh mesh, Mesh copy)
        {
            for (var s = 0; s < copy.subMeshCount; s++)
                if (copy.GetSubMesh(s).baseVertex != 0)
                    copy.SetIndices(mesh.GetIndices(s, true), mesh.GetTopology(s), s, false, 0);
        }

        static UnwrapOutcome Commit(Mesh mesh, Mesh best, double coverage)
        {
            if (best == null || coverage <= 0 || !ValidIndices(best)) return UnwrapOutcome.Failed("unwrapping failed");
            if (mesh.indexFormat == IndexFormat.UInt16 && best.vertexCount > ushort.MaxValue)
                return UnwrapOutcome.Failed($"unwrapped mesh would need {best.vertexCount} vertices (16-bit indices)");
            EditorUtility.CopySerialized(best, mesh);
            EditorUtility.SetDirty(mesh);
            return UnwrapOutcome.Unwrapped(coverage);
        }

        // UV2 must lie inside the unit square, not be a slice of a larger unwrap (copied by the chunker).
        static bool HasLightmapUVs(MeshReadback data)
        {
            if (!data.Has(VertexAttribute.TexCoord1)) return false;
            var uvs = new List<Vector2>();
            data.GetUVs(LightmapChannel, uvs);
            if (uvs.Count == 0 || uvs.Count != data.VertexCount) return false;
            Vector2 min = uvs[0], max = uvs[0];
            foreach (var uv in uvs)
            {
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }
            return min.x >= -UvTolerance && min.y >= -UvTolerance && max.x <= 1f + UvTolerance && max.y <= 1f + UvTolerance &&
                   Mathf.Max(max.x - min.x, max.y - min.y) > MinUvSpan;
        }

        static bool FiniteVertexData(MeshReadback data, out VertexAttribute attribute)
        {
            static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
            attribute = VertexAttribute.Position;
            if (!data.Vertices().All(v => Finite(v.x) && Finite(v.y) && Finite(v.z))) return false;
            attribute = VertexAttribute.Normal;
            if (!data.Normals().All(v => Finite(v.x) && Finite(v.y) && Finite(v.z))) return false;
            var uvs = new List<Vector4>();
            for (var channel = 0; channel < MaxUvChannels; channel++)
            {
                attribute = VertexAttribute.TexCoord0 + channel;
                if (!data.Has(attribute)) continue;
                data.GetUVs(channel, uvs);
                if (!uvs.All(v => Finite(v.x) && Finite(v.y) && Finite(v.z) && Finite(v.w))) return false;
            }
            return true;
        }

        static bool ValidIndices(Mesh mesh)
        {
            using var data = MeshReadback.Of(mesh);
            return ValidIndices(data);
        }

        static bool ValidIndices(MeshReadback data)
        {
            for (var s = 0; s < data.SubMeshCount; s++)
                foreach (var i in data.Indices(s))
                    if (i < 0 || i >= data.VertexCount) return false;
            return true;
        }

        static double Coverage(Mesh mesh)
        {
            using var data = MeshReadback.Of(mesh);
            return Coverage(data);
        }

        // Share of the UV2 unit square inside triangles, 0 without complete lightmap UVs. Corrupt indices are ignored.
        static double Coverage(MeshReadback data)
        {
            var uvs = new List<Vector2>();
            data.GetUVs(LightmapChannel, uvs);
            if (uvs.Count != data.VertexCount) return 0;
            var t = data.Triangles();
            double area = 0;
            for (var i = 0; i < t.Length; i += 3)
            {
                if (t[i] >= uvs.Count || t[i + 1] >= uvs.Count || t[i + 2] >= uvs.Count) continue;
                Vector2 a = uvs[t[i]], b = uvs[t[i + 1]], c = uvs[t[i + 2]];
                area += Math.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5;
            }
            return area;
        }
    }
}
