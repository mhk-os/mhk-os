using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MHK.Editor.Modding.Import
{
    /// <summary>Settings of <see cref="GlbModelImporter.Import"/>.</summary>
    public sealed class GlbImportOptions
    {
        /// <summary>Uniform scale of the returned root (its localScale).</summary>
        public float scale = 1f;
        /// <summary>"Assets/…" folder for the extracted PNG and JPEG textures. Null: in-memory textures.</summary>
        public string textureFolder;
        /// <summary>"Assets/…" folder for the MHK/Lit materials, reused and updated on re-import. Null: in-memory materials.</summary>
        public string materialFolder;
        /// <summary>Library that stores the meshes and clips as sub-assets. Null: they stay in memory.</summary>
        public ModelLibraryWriter library;
        /// <summary>Prefix of the mesh and clip sub-asset names (default: the file name without extension).</summary>
        public string namePrefix;
        /// <summary>Export root that material extras.animatedTexture.exportFrames are relative to. Null: first frame only.</summary>
        public string exportRoot;
        /// <summary>Lighting of the created materials (world geometry, dynamic object, sky dome).</summary>
        public GlbLightingStyle lighting = GlbLightingStyle.World;
    }

    /// <summary>Result of <see cref="GlbModelImporter.Import"/>.</summary>
    public sealed class GlbModel
    {
        /// <summary>New scene object named after the file, children = the glTF scene nodes with their original names.</summary>
        public GameObject root;
        /// <summary>The glTF JSON.</summary>
        public JObject json;
        /// <summary>Animation clips by glTF animation name.</summary>
        public Dictionary<string, AnimationClip> clips = new();
        /// <summary>glTF material index to material (-1: primitives without one). Vertex-tinted uses map to the plain variant when both exist.</summary>
        public Dictionary<int, Material> materials = new();
    }

    /// <summary>
    /// Editor-only glTF 2.0 importer (.glb, .gltf). Builds the node hierarchy, meshes (one per glTF mesh, one submesh
    /// per triangle primitive), skins, generic animation clips and MHK/Lit materials, converting coordinates exactly
    /// like glTFast (<see cref="GlbSpace"/>), so positions in converter JSON files match.
    /// </summary>
    public static class GlbModelImporter
    {
        /// <summary>Imports <paramref name="glbPath"/>. Throws with a clear message when the file is invalid (the partial hierarchy is destroyed).</summary>
        public static GlbModel Import(string glbPath, GlbImportOptions o)
        {
            var import = new GlbImport(GlbReader.Load(glbPath), o ?? new GlbImportOptions());
            try
            {
                return import.Run();
            }
            catch
            {
                if (import.root != null) Object.DestroyImmediate(import.root);
                throw;
            }
        }
    }

    /// <summary>State of one <see cref="GlbModelImporter.Import"/> run: the document, the created nodes and the model.</summary>
    sealed class GlbImport
    {
        public readonly GlbReader doc;
        public readonly JObject json;
        public readonly GlbImportOptions options;
        public readonly string prefix;
        public readonly GlbMaterials materials;
        public readonly GlbModel model = new();
        public Transform[] nodes;
        public GameObject root;

        readonly string file;
        readonly HashSet<string> warned = new();
        readonly GlbMeshBuilder meshes;

        public GlbImport(GlbReader doc, GlbImportOptions options)
        {
            this.doc = doc;
            json = doc.json;
            this.options = options;
            file = Path.GetFileName(doc.path);
            prefix = string.IsNullOrEmpty(options.namePrefix) ? Path.GetFileNameWithoutExtension(doc.path) : options.namePrefix;
            materials = new GlbMaterials(doc, options, prefix);
            meshes = new GlbMeshBuilder(this);
        }

        public void WarnOnce(string key, string message)
        {
            if (warned.Add(key)) Debug.LogWarning($"[GlbImport] {file}: {message}");
        }

        public T Store<T>(T obj) where T : Object => options.library != null ? options.library.Store(obj) : obj;

        public GlbModel Run()
        {
            model.json = json;
            root = new GameObject(Path.GetFileNameWithoutExtension(doc.path));
            model.root = root;
            var nodeList = ExportJson.Items(json["nodes"]);
            nodes = new Transform[nodeList.Count];
            foreach (var n in SceneRoots(nodeList)) CreateNode(nodeList, n, root.transform);
            for (var i = 0; i < nodeList.Count; i++)
                if (nodes[i] != null && nodeList[i]["mesh"] != null) AttachMesh((JObject)nodeList[i], nodes[i]);
            if (json["animations"] is JArray anims)
                for (var i = 0; i < anims.Count; i++) GlbAnimationBuilder.Import(this, (JObject)anims[i], i);
            root.transform.localScale = Vector3.one * options.scale;
            foreach (var kv in materials.byIndex) model.materials[kv.Key] = kv.Value;
            options.library?.Save();
            foreach (var m in materials.byIndex.Values.Distinct())
                if (EditorUtility.IsPersistent(m)) AssetDatabase.SaveAssetIfDirty(m);
            return model;
        }

        IEnumerable<int> SceneRoots(JArray nodeList)
        {
            var scenes = json["scenes"] as JArray;
            var s = GlbReader.I(json["scene"], 0);
            if (scenes != null && s >= 0 && s < scenes.Count) return ExportJson.Items(scenes[s]["nodes"]).Select(t => (int)t);
            var children = new HashSet<int>(nodeList.SelectMany(n => ExportJson.Items(n["children"]).Select(t => (int)t)));
            return Enumerable.Range(0, nodeList.Count).Where(i => !children.Contains(i));
        }

        void CreateNode(JArray nodeList, int index, Transform parent)
        {
            if (index < 0 || index >= nodeList.Count) throw new InvalidDataException($"{doc.path}: node {index} does not exist");
            if (nodes[index] != null) throw new InvalidDataException($"{doc.path}: node {index} is used twice in the hierarchy");
            var n = (JObject)nodeList[index];
            var t = new GameObject(GlbReader.Str(n["name"]) ?? $"node{index}").transform;
            t.SetParent(parent, false);
            nodes[index] = t;
            if (n["matrix"] is JArray { Count: GltfConstants.Mat4Components } m)
            {
                GlbSpace.Decompose(GlbSpace.Matrix(m.Select(v => (float)v).ToArray()), out var pos, out var rot, out var scl);
                t.SetLocalPositionAndRotation(pos, rot);
                t.localScale = scl;
            }
            else
            {
                t.SetLocalPositionAndRotation(GlbSpace.Vec(n["translation"]), GlbSpace.Rot(n["rotation"]));
                t.localScale = GlbSpace.Scale(n["scale"]);
            }
            if (n["children"] is JArray children)
                foreach (var c in children) CreateNode(nodeList, (int)c, t);
        }

        void AttachMesh(JObject node, Transform t)
        {
            var meshIndex = (int)node["mesh"];
            var skinIndex = GlbReader.I(node["skin"]);
            var data = meshes.Get(meshIndex, skinIndex);
            if (data == null) return;
            Renderer renderer;
            if (skinIndex >= 0 && data.hasJoints) renderer = GlbSkin.CreateRenderer(this, t.gameObject, data.mesh, skinIndex);
            else
            {
                if (skinIndex >= 0) WarnOnce("noJoints" + meshIndex, $"mesh {meshIndex} has a skin but no JOINTS_0/WEIGHTS_0; imported unskinned");
                t.gameObject.AddComponent<MeshFilter>().sharedMesh = data.mesh;
                renderer = t.gameObject.AddComponent<MeshRenderer>();
            }
            renderer.sharedMaterials = data.materials;
            if (options.lighting != GlbLightingStyle.Sky) return;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }
}
