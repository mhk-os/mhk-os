using System;
using System.Collections.Generic;
using MHK.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MHK.Race
{
    /// <summary>What a loose track prop is made of, which sets its mass (<see cref="PropMass"/>).</summary>
    public enum PropMaterial
    {
        /// <summary>From the object's, its meshes' or its materials' names; wood when none matches.</summary>
        Auto,
        /// <summary>Plastic and cardboard: cones, bollards, barrier frames, cartons.</summary>
        Light,
        /// <summary>Hay and straw bales.</summary>
        Straw,
        /// <summary>Crates, wooden barrels, planks, furniture.</summary>
        Wood,
        /// <summary>Hollow metal: mailboxes, bins, metal barrels, tools.</summary>
        SheetMetal,
        /// <summary>Solid or filled metal: hydrants, metal crates.</summary>
        Iron,
        /// <summary>Rocks and boulders.</summary>
        Stone,
        /// <summary>Keeps the rigidbody's own mass.</summary>
        Authored,
    }

    /// <summary>Densities and limits of <see cref="PropMass"/> (GameConfig.propMass).</summary>
    [Serializable]
    public class PropMassSettings
    {
        [Tooltip("Light props (cones, bollards, barrier frames, cartons): kg per m³ of the bounding box of their colliders. " +
                 "The densities are relative to the 20 kg reference kart, not real-world ones.")]
        [Min(0f)] public float light = 1.5f;
        [Tooltip("Hay and straw bales: kg per m³ of collider bounding box.")]
        [Min(0f)] public float straw = 8f;
        [Tooltip("Crates, wooden barrels, planks, furniture, and props whose material is not recognised: kg per m³ of " +
                 "collider bounding box.")]
        [Min(0f)] public float wood = 14f;
        [Tooltip("Hollow metal (mailboxes, bins, metal barrels, tools): kg per m³ of collider bounding box.")]
        [Min(0f)] public float sheetMetal = 14f;
        [Tooltip("Solid or filled metal (hydrants, metal crates): kg per m³ of collider bounding box.")]
        [Min(0f)] public float iron = 40f;
        [Tooltip("Rocks and boulders (about half their bounding box): kg per m³ of collider bounding box.")]
        [Min(0f)] public float stone = 16f;
        [Tooltip("Lightest prop, as a share of the reference kart's mass (GameConfig.defaultPhysics).")]
        [Min(0f)] public float minKartShare = 0.05f;
        [Tooltip("Heaviest prop, as a share of the reference kart's mass. Below about 1.8 a kart can still push it slowly " +
                 "(its rear tyres grip harder than the prop's ground friction holds). A head-on hit at speed leaves the kart " +
                 "with 1 / (1 + share) of its speed.")]
        [Min(0f)] public float maxKartShare = 1.5f;

        /// <summary>Density (kg per m³ of collider bounding box) of <paramref name="material"/>.</summary>
        public float Density(PropMaterial material) => material switch
        {
            PropMaterial.Light => light,
            PropMaterial.Straw => straw,
            PropMaterial.SheetMetal => sheetMetal,
            PropMaterial.Iron => iron,
            PropMaterial.Stone => stone,
            _ => wood,
        };
    }

    /// <summary>
    /// Gives the loose props (non-kinematic rigidbodies) of a loaded track scene masses from their size and material:
    /// the bounding volume of their colliders × the material's density (<see cref="PropMassSettings"/>), clamped to
    /// shares of the reference kart's mass. The material comes from a <see cref="PhysicsProp"/> when there is one, else
    /// from the names of the object, its meshes and its materials (the imported tracks' German and English prop names).
    /// Replaces whatever mass the scene holds, so released tracks get it too.
    /// </summary>
    public static class PropMass
    {
        // Lower-case name parts, first match wins: specific before generic ("metalkiste" is iron, "holzfass" wood).
        static readonly (string part, PropMaterial material)[] NameParts =
        {
            ("metalkiste", PropMaterial.Iron), ("hydrant", PropMaterial.Iron),
            ("holz", PropMaterial.Wood), ("wood", PropMaterial.Wood),
            ("metal", PropMaterial.SheetMetal), ("oil", PropMaterial.SheetMetal), ("briefkasten", PropMaterial.SheetMetal),
            ("mailbox", PropMaterial.SheetMetal), ("tonne", PropMaterial.SheetMetal), ("hammer", PropMaterial.SheetMetal),
            ("fels", PropMaterial.Stone), ("stein", PropMaterial.Stone), ("stone", PropMaterial.Stone),
            ("boulder", PropMaterial.Stone), ("brocken", PropMaterial.Stone),
            ("heu", PropMaterial.Straw), ("stroh", PropMaterial.Straw), ("straw", PropMaterial.Straw), ("hay", PropMaterial.Straw),
            ("karton", PropMaterial.Light), ("carton", PropMaterial.Light), ("cardboard", PropMaterial.Light),
            ("huetchen", PropMaterial.Light), ("cone", PropMaterial.Light), ("begrenzung", PropMaterial.Light),
            ("absperrung", PropMaterial.Light), ("bollard", PropMaterial.Light), ("barrier", PropMaterial.Light),
            ("kiste", PropMaterial.Wood), ("crate", PropMaterial.Wood), ("box", PropMaterial.Wood), ("barrel", PropMaterial.Wood),
            ("fass", PropMaterial.Wood), ("brett", PropMaterial.Wood), ("plank", PropMaterial.Wood),
            ("klavier", PropMaterial.Wood), ("piano", PropMaterial.Wood),
        };

        static readonly List<Rigidbody> Bodies = new();
        static readonly List<Collider> Colliders = new();
        static readonly List<MeshFilter> Meshes = new();
        static readonly List<Renderer> Renderers = new();
        static readonly List<Material> Materials = new();

        /// <summary>Sets the mass of every loose prop in <paramref name="scene"/>.</summary>
        public static void Apply(Scene scene, GameConfig cfg)
        {
            if (!scene.IsValid() || cfg.defaultPhysics == null) return;
            var s = cfg.propMass;
            var kart = cfg.defaultPhysics.mass;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, Bodies);
                foreach (var body in Bodies)
                {
                    if (body.isKinematic || body.TryGetComponent<TrackHazard>(out _)) continue;
                    var material = MaterialOf(body);
                    if (material == PropMaterial.Authored) continue;
                    var volume = BoundingVolume(body);
                    if (volume > 0f) body.mass = Mathf.Clamp(volume * s.Density(material), s.minKartShare * kart, s.maxKartShare * kart);
                }
            }
            Bodies.Clear();
            Colliders.Clear();
            Meshes.Clear();
            Renderers.Clear();
            Materials.Clear();
        }

        static PropMaterial MaterialOf(Rigidbody body)
        {
            if (body.TryGetComponent<PhysicsProp>(out var prop) && prop.material != PropMaterial.Auto) return prop.material;
            if (FromName(body.name, out var material)) return material;
            body.GetComponentsInChildren(true, Meshes);
            foreach (var f in Meshes)
                if (f.sharedMesh != null && FromName(f.sharedMesh.name, out material)) return material;
            body.GetComponentsInChildren(true, Renderers);
            foreach (var r in Renderers)
            {
                r.GetSharedMaterials(Materials);
                foreach (var m in Materials)
                    if (m != null && FromName(m.name, out material)) return material;
            }
            return PropMaterial.Wood;
        }

        static bool FromName(string name, out PropMaterial material)
        {
            foreach (var (part, m) in NameParts)
            {
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                material = m;
                return true;
            }
            material = PropMaterial.Auto;
            return false;
        }

        // Summed bounding boxes (local shape × scale) of the solid colliders that move with the body.
        static float BoundingVolume(Rigidbody body)
        {
            var volume = 0f;
            body.GetComponentsInChildren(true, Colliders);
            foreach (var c in Colliders)
                if (!c.isTrigger && c.GetComponentInParent<Rigidbody>(true) == body) volume += BoundingVolume(c);
            return volume;
        }

        static float BoundingVolume(Collider c)
        {
            Vector3 size;
            switch (c)
            {
                case BoxCollider box:
                    size = box.size;
                    break;
                case SphereCollider sphere:
                    size = Vector3.one * (2f * sphere.radius);
                    break;
                case CapsuleCollider capsule:
                    size = Vector3.one * (2f * capsule.radius);
                    size[capsule.direction] = Mathf.Max(capsule.height, size[capsule.direction]);
                    break;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    size = mesh.sharedMesh.bounds.size;
                    break;
                default:
                    return 0f;
            }
            var scale = c.transform.lossyScale;
            return Mathf.Abs(size.x * scale.x * size.y * scale.y * size.z * scale.z);
        }
    }
}
