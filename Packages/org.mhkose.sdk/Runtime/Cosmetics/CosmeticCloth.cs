using MHK.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace MHK.Cosmetics
{
    /// <summary>
    /// A cape of cloth on a body cosmetic. Its top edge runs along x through this transform's origin and stays pinned
    /// there, the rest is a grid of particles moved by Verlet steps under gravity, the wind of the driver's speed, a
    /// breeze and a flutter. Distance constraints hold it together. Dressed on a driver it keeps behind the driver's back
    /// and lies on the kart (<see cref="Obstacles"/>), so it falls onto the engine. Without obstacles no particle swings
    /// further towards the driver than its rest shape allows. The detail of the grid, the number of constraint passes
    /// and the step rate follow <see cref="GraphicsQuality.Cloth"/>, read when the cape is built. A new cape starts raised
    /// and falls into place. A cape that no camera draws is not simulated and starts again from its rest shape once it
    /// is drawn.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [DefaultExecutionOrder(100)]
    public sealed class CosmeticCloth : MonoBehaviour
    {
        [SerializeField, Tooltip("Width of the top edge (m, model space).")]
        float topWidth = 0.3f;
        [SerializeField, Tooltip("Width of the bottom edge (m, model space).")]
        float bottomWidth = 0.42f;
        [SerializeField, Tooltip("Length from the top edge to the bottom edge (m, model space).")]
        float length = 0.45f;
        [SerializeField, Range(0f, 80f), Tooltip("Degrees the cape stands back from hanging straight down in its rest shape.")]
        float restAngle = 40f;
        [SerializeField, Tooltip("How far (m, model space) the side edges curve forward around the shoulders in the rest shape.")]
        float wrap = 0.06f;
        [SerializeField, Range(0f, 1f), Tooltip("How strongly each step pulls the cloth back to its rest shape (0 = limp).")]
        float shapeStiffness = 0.004f;
        [SerializeField, Range(0f, 180f), Tooltip("Degrees the cape stands back from hanging straight down when it appears, so it falls into place.")]
        float dropAngle = 120f;
        [SerializeField, Tooltip("Air drag (1/s) that calms the cloth.")]
        float drag = 3f;
        [SerializeField, Tooltip("How strongly the wind blows the cape back (1/s).")]
        float wind = 2.5f;
        [SerializeField, Tooltip("Strongest wind (m/s), from the driver's speed and the breeze.")]
        float maxWind = 20f;
        [SerializeField, Tooltip("Wind (m/s) from behind the driver that the cape always feels, so it stirs while standing.")]
        float breeze = 1.2f;
        [SerializeField, Range(0f, 1f), Tooltip("Share of the driver's movement the cloth lags behind, the rest carries it along.")]
        float inertia = 0.25f;
        [SerializeField, Tooltip("Flutter in the wind (m/s² per m/s of wind).")]
        float flutter = 0.6f;
        [SerializeField, Tooltip("How far (m, model space) the cloth may swing forward of its rest shape towards the driver " +
                                 "when it has no obstacles, which keeps it out of the driver's back and the seat.")]
        float slack = 0.03f;

        // Columns and rows of the grid, constraint passes per step and steps per second, per ClothDetail.
        static readonly (int columns, int rows, int passes, float rate)[] Details = { (4, 6, 2, 30f), (5, 8, 3, 60f), (6, 10, 4, 60f) };
        const int MaxStepsPerFrame = 3;
        // A jump further than this in one frame (respawn) starts the cloth again from its rest shape.
        const float TeleportDistance = 4f;
        const float FlutterHz = 2.3f, FlutterRowHz = 1.7f;
        const float Damping = 0.995f;
        const float MinLength = 1e-5f;
        // Share of the way along the surface a particle lying on the kart moves back to where its step began, per
        // constraint pass, so the cape stays where it fell instead of sliding off.
        const float Friction = 0.8f;

        int columns, rows, passes;
        float stepTime, accumulated;
        Vector3[] positions, previous, rest;
        int[] constraintA, constraintB;
        float[] constraintLength;
        Vector3[] vertices, normals;
        Mesh mesh;
        MeshRenderer meshRenderer;
        Matrix4x4 lastLocalToWorld;
        bool simulating, started;

        int Count => columns * rows;

        /// <summary>What the cape stays out of, the driver's back and the kart. Null keeps it behind its rest shape.</summary>
        public ClothObstacles Obstacles { get; set; }

        /// <summary>
        /// Builds the cloth mesh in its rest shape. The cape does it on Awake, and the editor's icon renderer calls it on
        /// a preview instance. The grid detail is <see cref="GraphicsQuality.Cloth"/>'s at this moment.
        /// </summary>
        public void Build()
        {
            var detail = Details[Mathf.Clamp((int)GraphicsQuality.Cloth, 0, Details.Length - 1)];
            columns = detail.columns;
            rows = detail.rows;
            passes = detail.passes;
            stepTime = 1f / detail.rate;
            BuildRest();
            BuildConstraints();
            BuildMesh();
            simulating = false;
        }

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            Build();
        }

        void OnDestroy()
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh);
            else DestroyImmediate(mesh);
        }

        void BuildRest()
        {
            rest = new Vector3[Count];
            for (var k = 0; k < Count; k++) rest[k] = Shape(k, restAngle);
            positions = new Vector3[Count];
            previous = new Vector3[Count];
        }

        // Particle k of the cape standing back tilt degrees from hanging straight down.
        Vector3 Shape(int k, float tilt)
        {
            var along = k / columns / (rows - 1f);
            var across = k % columns / (columns - 1f) * 2f - 1f;
            var width = Mathf.Lerp(topWidth, bottomWidth, along);
            var down = along * length;
            // The edges curve forward around the shoulders, less so towards the bottom.
            var curve = wrap * across * across * (1f - 0.5f * along);
            var angle = tilt * Mathf.Deg2Rad;
            return new Vector3(across * 0.5f * width, -down * Mathf.Cos(angle), curve - down * Mathf.Sin(angle));
        }

        // Rows and columns, both diagonals against shearing and every second particle down against folding.
        void BuildConstraints()
        {
            var a = new System.Collections.Generic.List<int>();
            var b = new System.Collections.Generic.List<int>();
            void Add(int i0, int j0, int i1, int j1)
            {
                if (i1 < 0 || i1 >= columns || j1 >= rows) return;
                a.Add(j0 * columns + i0);
                b.Add(j1 * columns + i1);
            }
            for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                Add(i, j, i + 1, j);
                Add(i, j, i, j + 1);
                Add(i, j, i + 1, j + 1);
                Add(i, j, i - 1, j + 1);
                Add(i, j, i, j + 2);
            }
            constraintA = a.ToArray();
            constraintB = b.ToArray();
            constraintLength = new float[constraintA.Length];
            for (var c = 0; c < constraintA.Length; c++) constraintLength[c] = Vector3.Distance(rest[constraintA[c]], rest[constraintB[c]]);
        }

        // Two sides with their own vertices, so both are lit from their own side.
        void BuildMesh()
        {
            var n = Count;
            vertices = new Vector3[2 * n];
            normals = new Vector3[2 * n];
            var uv = new Vector2[2 * n];
            for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                var k = j * columns + i;
                uv[k] = uv[n + k] = new Vector2(i / (columns - 1f), 1f - j / (rows - 1f));
                vertices[k] = vertices[n + k] = rest[k];
            }
            var triangles = new int[(columns - 1) * (rows - 1) * 12];
            var t = 0;
            for (var j = 0; j < rows - 1; j++)
            for (var i = 0; i < columns - 1; i++)
            {
                int tl = j * columns + i, tr = tl + 1, bl = tl + columns, br = bl + 1;
                // Front faces away from the back (-z), the back side the other way.
                triangles[t++] = tl; triangles[t++] = tr; triangles[t++] = br;
                triangles[t++] = tl; triangles[t++] = br; triangles[t++] = bl;
                triangles[t++] = n + tl; triangles[t++] = n + br; triangles[t++] = n + tr;
                triangles[t++] = n + tl; triangles[t++] = n + bl; triangles[t++] = n + br;
            }
            if (mesh == null)
            {
                mesh = new Mesh { name = "Cape" };
                mesh.MarkDynamic();
            }
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            UpdateNormals();
            mesh.normals = normals;
            GetComponent<MeshFilter>().sharedMesh = mesh;
        }

        void LateUpdate()
        {
            if (!meshRenderer.isVisible)
            {
                simulating = false;
                return;
            }
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            var now = transform.localToWorldMatrix;
            if (!simulating || (now.GetColumn(3) - lastLocalToWorld.GetColumn(3)).sqrMagnitude > TeleportDistance * TeleportDistance) Restart(now);
            var velocity = ((Vector3)(now.GetColumn(3) - lastLocalToWorld.GetColumn(3))) / dt;
            Carry(now);
            lastLocalToWorld = now;
            accumulated = Mathf.Min(accumulated + dt, MaxStepsPerFrame * stepTime);
            var blow = Vector3.ClampMagnitude(-velocity - (Vector3)now.GetColumn(2).normalized * breeze, maxWind);
            while (accumulated >= stepTime)
            {
                accumulated -= stepTime;
                Step(now, blow, stepTime);
            }
            WriteMesh(now);
        }

        // The first start is raised, so the cape falls into place. Later ones (drawn again, respawned) start at rest.
        void Restart(Matrix4x4 now)
        {
            for (var k = 0; k < Count; k++) positions[k] = previous[k] = now.MultiplyPoint3x4(started ? rest[k] : Shape(k, dropAngle));
            started = true;
            lastLocalToWorld = now;
            accumulated = 0f;
            simulating = true;
            // Room for the cape blown back and up, so its bounds never need recalculating (built, they fit the rest shape).
            var reach = Mathf.Max(topWidth, bottomWidth) + length;
            mesh.bounds = new Bounds(new Vector3(0f, 0f, -length * 0.5f), new Vector3(2f * reach, 2f * reach, 2f * reach));
        }

        // Moves the cloth along with the driver except for its inertia share, keeping its velocity.
        void Carry(Matrix4x4 now)
        {
            var carry = now * lastLocalToWorld.inverse;
            var share = 1f - inertia;
            for (var k = 0; k < Count; k++)
            {
                positions[k] = Vector3.LerpUnclamped(positions[k], carry.MultiplyPoint3x4(positions[k]), share);
                previous[k] = Vector3.LerpUnclamped(previous[k], carry.MultiplyPoint3x4(previous[k]), share);
            }
        }

        void Step(Matrix4x4 now, Vector3 blow, float h)
        {
            var calm = 1f - Mathf.Exp(-drag * h);
            var gravity = Physics.gravity;
            var right = (Vector3)now.GetColumn(0).normalized;
            var up = (Vector3)now.GetColumn(1).normalized;
            var gust = blow.magnitude * flutter;
            var time = Time.time;
            for (var k = columns; k < Count; k++)
            {
                var row = k / columns;
                var along = row / (rows - 1f);
                var shake = right * Mathf.Sin(time * FlutterHz * Mathf.PI * 2f + k * 0.9f) + up * Mathf.Sin(time * FlutterRowHz * Mathf.PI * 2f + row * 1.3f);
                var accel = gravity + blow * wind + shake * (gust * along);
                var v = (positions[k] - previous[k]) * Damping;
                previous[k] = positions[k];
                positions[k] += v - v * calm + accel * (h * h);
                positions[k] = Vector3.LerpUnclamped(positions[k], now.MultiplyPoint3x4(rest[k]), shapeStiffness);
            }
            for (var k = 0; k < columns; k++) positions[k] = now.MultiplyPoint3x4(rest[k]);
            var scale = ((Vector3)now.GetColumn(0)).magnitude;
            var frame = Obstacles != null ? Obstacles.Frame : null;
            if (frame == null)
            {
                for (var p = 0; p < passes; p++) Solve(scale);
                KeepOffBody(now);
                return;
            }
            var toFrame = frame.worldToLocalMatrix;
            var fromFrame = frame.localToWorldMatrix;
            for (var p = 0; p < passes; p++)
            {
                Solve(scale);
                Collide(toFrame, fromFrame);
            }
        }

        void Solve(float scale)
        {
            for (var c = 0; c < constraintA.Length; c++)
            {
                int a = constraintA[c], b = constraintB[c];
                var delta = positions[b] - positions[a];
                var distance = delta.magnitude;
                if (distance < MinLength) continue;
                var pinnedA = a < columns;
                var pinnedB = b < columns;
                if (pinnedA && pinnedB) continue;
                var correction = delta * ((distance - constraintLength[c] * scale) / distance);
                if (pinnedA) positions[b] -= correction;
                else if (pinnedB) positions[a] += correction;
                else
                {
                    positions[a] += correction * 0.5f;
                    positions[b] -= correction * 0.5f;
                }
            }
        }

        // Moves the cloth out of the driver's back and up onto the kart, in the kart's frame. A particle the kart holds
        // up keeps most of its place along the surface, as cloth does with friction.
        void Collide(Matrix4x4 toFrame, Matrix4x4 fromFrame)
        {
            for (var k = columns; k < Count; k++)
            {
                var local = toFrame.MultiplyPoint3x4(positions[k]);
                var before = local;
                if (Obstacles.Collide(ref local))
                {
                    var start = toFrame.MultiplyPoint3x4(previous[k]);
                    local.x = Mathf.Lerp(local.x, start.x, Friction);
                    local.z = Mathf.Lerp(local.z, start.z, Friction);
                }
                if (local != before) positions[k] = fromFrame.MultiplyPoint3x4(local);
            }
        }

        // A cape without obstacles keeps out of the driver's back and the seat, which are in front of its rest shape (+z),
        // so no particle swings further forward than its rest place and the slack.
        void KeepOffBody(Matrix4x4 now)
        {
            var toLocal = now.inverse;
            for (var k = columns; k < Count; k++)
            {
                var local = toLocal.MultiplyPoint3x4(positions[k]);
                var front = rest[k].z + slack;
                if (local.z <= front) continue;
                local.z = front;
                positions[k] = now.MultiplyPoint3x4(local);
            }
        }

        void WriteMesh(Matrix4x4 now)
        {
            var toLocal = now.inverse;
            var n = Count;
            for (var k = 0; k < n; k++) vertices[k] = vertices[n + k] = toLocal.MultiplyPoint3x4(positions[k]);
            UpdateNormals();
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            mesh.SetVertices(vertices, 0, vertices.Length, flags);
            mesh.SetNormals(normals, 0, normals.Length, flags);
        }

        // Normals from the neighbours along the row and down the column. The front side faces -z at rest.
        void UpdateNormals()
        {
            var n = Count;
            for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                var k = j * columns + i;
                var across = vertices[j * columns + Mathf.Min(i + 1, columns - 1)] - vertices[j * columns + Mathf.Max(i - 1, 0)];
                var down = vertices[Mathf.Min(j + 1, rows - 1) * columns + i] - vertices[Mathf.Max(j - 1, 0) * columns + i];
                var normal = Vector3.Cross(across, down);
                normal = normal.sqrMagnitude > MinLength * MinLength ? normal.normalized : Vector3.back;
                normals[k] = normal;
                normals[n + k] = -normal;
            }
        }
    }
}
