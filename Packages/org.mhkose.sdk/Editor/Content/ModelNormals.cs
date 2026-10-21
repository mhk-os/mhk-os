using System.Collections.Generic;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Content
{
    /// <summary>
    /// Repairs driver and kart mesh normals that disagree with their triangles (MHK2 crease artefacts), in place in
    /// their model library. Idempotent.
    /// </summary>
    public static class ModelNormals
    {
        /// <summary>Faces meeting at a sharper angle than this keep separate normals (degrees).</summary>
        public const float CreaseAngle = 60f;
        const float BrokenDot = 0.3f;
        const float DegenerateSqr = 1e-20f;
        const float CancelledShare = 0.5f;
        const float WeldCellsPerMetre = 1e4f;

        [MenuItem("MHK/Content/Repair Driver and Kart Normals")]
        static void RepairAll()
        {
            var repaired = 0;
            var vertices = 0;
            foreach (var mesh in DriverAndKartMeshes())
            {
                var broken = CountBroken(mesh);
                if (broken == 0) continue;
                var normals = CreaseNormals(mesh, CreaseAngle);
                using (MeshWriteScope.Open(mesh))
                {
                    mesh.normals = normals;
                    EditorUtility.SetDirty(mesh);
                }
                repaired++;
                vertices += broken;
                Debug.Log($"[Normals] {AssetDatabase.GetAssetPath(mesh)} / {mesh.name}: {broken} broken normals, recomputed.");
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Normals] Repaired {repaired} meshes ({vertices} broken vertex normals).");
        }

        static IEnumerable<Mesh> DriverAndKartMeshes()
        {
            var seen = new HashSet<Mesh>();
            var prefabs = new List<GameObject>();
            foreach (var c in EditorAssets.FindAll<CharacterDefinition>())
                if (c.driverPrefab != null) prefabs.Add(c.driverPrefab);
            foreach (var k in EditorAssets.FindAll<KartDefinition>())
                if (k.kartPrefab != null) prefabs.Add(k.kartPrefab);
            foreach (var prefab in prefabs)
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
                if (mesh == null || !seen.Add(mesh)) continue;
                if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh)) is ModelImporter) continue; // imported files re-import their normals
                yield return mesh;
            }
        }

        static Vector3[] OwnFaceNormals(Vector3[] v, int[] t, out Vector3[] faceNormals, out float[] cornerWeights)
        {
            var faces = t.Length / 3;
            faceNormals = new Vector3[faces];
            cornerWeights = new float[t.Length];
            var own = new Vector3[v.Length];
            for (var f = 0; f < faces; f++)
            {
                Vector3 a = v[t[3 * f]], b = v[t[3 * f + 1]], c = v[t[3 * f + 2]];
                var cross = Vector3.Cross(b - a, c - a);
                faceNormals[f] = cross.sqrMagnitude > DegenerateSqr ? cross.normalized : Vector3.zero;
                cornerWeights[3 * f] = Vector3.Angle(b - a, c - a) * Mathf.Deg2Rad;
                cornerWeights[3 * f + 1] = Vector3.Angle(c - b, a - b) * Mathf.Deg2Rad;
                cornerWeights[3 * f + 2] = Vector3.Angle(a - c, b - c) * Mathf.Deg2Rad;
                for (var k = 0; k < 3; k++) own[t[3 * f + k]] += faceNormals[f] * cornerWeights[3 * f + k];
            }
            return own;
        }

        /// <summary>
        /// Vertices whose normal points more than ~73° away from the triangles that use it. Vertices whose own
        /// triangles cancel out have no clear direction and are not counted, so a repaired mesh counts 0.
        /// </summary>
        public static int CountBroken(Mesh mesh)
        {
            Vector3[] v, n;
            int[] t;
            using (var data = MeshReadback.Of(mesh))
            {
                v = data.Vertices();
                n = data.Normals();
                t = data.Triangles();
            }
            if (n.Length != v.Length) return v.Length;
            var own = OwnFaceNormals(v, t, out _, out var weights);
            var total = new float[v.Length];
            for (var k = 0; k < t.Length; k++) total[t[k]] += weights[k];
            var broken = 0;
            for (var i = 0; i < v.Length; i++)
            {
                if (own[i].magnitude < CancelledShare * total[i]) continue;
                if (Vector3.Dot(n[i].normalized, own[i].normalized) < BrokenDot) broken++;
            }
            return broken;
        }

        /// <summary>
        /// Smooth normals welded by position: each vertex averages (corner-angle weighted) every triangle at its position
        /// whose normal lies within <paramref name="creaseDegrees"/> of the vertex's own triangles.
        /// </summary>
        public static Vector3[] CreaseNormals(Mesh mesh, float creaseDegrees)
        {
            Vector3[] v, n0;
            int[] t;
            using (var data = MeshReadback.Of(mesh))
            {
                v = data.Vertices();
                n0 = data.Normals();
                t = data.Triangles();
            }
            var own = OwnFaceNormals(v, t, out var faceNormals, out var weights);
            var cosCrease = Mathf.Cos(creaseDegrees * Mathf.Deg2Rad);
            var cornersAt = CornersByPosition(v, t);
            var result = new Vector3[v.Length];
            for (var i = 0; i < v.Length; i++)
            {
                var reference = own[i].sqrMagnitude > DegenerateSqr ? own[i].normalized
                    : n0.Length == v.Length ? n0[i] : Vector3.up;
                var sum = Vector3.zero;
                if (cornersAt.TryGetValue(Key(v[i]), out var corners))
                    foreach (var k in corners)
                    {
                        var f = faceNormals[k / 3];
                        if (Vector3.Dot(f, reference) >= cosCrease) sum += f * weights[k];
                    }
                result[i] = sum.sqrMagnitude > DegenerateSqr ? sum.normalized : reference;
            }
            return result;
        }

        static Dictionary<Vector3Int, List<int>> CornersByPosition(Vector3[] v, int[] t)
        {
            var cornersAt = new Dictionary<Vector3Int, List<int>>();
            for (var k = 0; k < t.Length; k++)
            {
                var key = Key(v[t[k]]);
                if (!cornersAt.TryGetValue(key, out var list)) cornersAt[key] = list = new List<int>();
                list.Add(k);
            }
            return cornersAt;
        }

        static Vector3Int Key(Vector3 p) =>
            new(Mathf.RoundToInt(p.x * WeldCellsPerMetre), Mathf.RoundToInt(p.y * WeldCellsPerMetre), Mathf.RoundToInt(p.z * WeldCellsPerMetre));
    }
}
