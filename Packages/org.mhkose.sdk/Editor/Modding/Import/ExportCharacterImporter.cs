using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MHK.Karts;
using MHK.Modding;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Imports export characters and karts (characters/&lt;id&gt;/character.json, karts/&lt;id&gt;/kart.json) as driver
    /// or kart prefabs plus definitions, one <see cref="ModelLibrary"/> per folder. A driver prefab's origin is its seat.
    /// </summary>
    public static class ExportCharacterImporter
    {
        /// <summary>Export animation keys that are not <see cref="CharacterAnimRole"/> names.</summary>
        static readonly Dictionary<string, CharacterAnimRole> RoleAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "hit", CharacterAnimRole.ShotDown },
            { "shotdown", CharacterAnimRole.ShotDown },
            { "crash", CharacterAnimRole.CollideFront },
            { "squashed", CharacterAnimRole.ShotDown },
            { "winner", CharacterAnimRole.Win },
            { "loser", CharacterAnimRole.Lose },
            { "reverse", CharacterAnimRole.Backward },
            { "reverseLeft", CharacterAnimRole.BackwardTurnLeft },
            { "reverseRight", CharacterAnimRole.BackwardTurnRight },
            { "fall", CharacterAnimRole.Drop },
            { "falseStart", CharacterAnimRole.PowerFail },
            { "boostStart", CharacterAnimRole.PowerStart },
            { "collide", CharacterAnimRole.CollideFront },
            { "backwardCollide", CharacterAnimRole.CollideBack },
            { "spin", CharacterAnimRole.Shake },
            { "stand", CharacterAnimRole.EngineOnly },
            { "spawn", CharacterAnimRole.Respawn },
        };

        /// <summary>Roles whose clip may cycle, the original's post behaviour decides how.</summary>
        static readonly HashSet<CharacterAnimRole> CyclingRoles = new()
        {
            CharacterAnimRole.Idle, CharacterAnimRole.Drive, CharacterAnimRole.Win, CharacterAnimRole.Lose, CharacterAnimRole.EngineOnly,
        };

        /// <summary>One character or kart import: its JSON, names, target folder and the loaded model under a fresh root.</summary>
        sealed class ModelImport
        {
            public JObject json;
            public string id, shortName, folder;
            public GameObject root;
            public GlbModel model;
        }

        static ModelImport Begin(string dir, string jsonFile, string kindFolder, ExportImportSettings s)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, jsonFile)));
            var id = ExportJson.Str(json["id"], s.pack.packId + "." + Path.GetFileName(dir));
            var shortName = ExportJson.SafeShortName(id);
            var folder = $"{ImportAssets.PackFolder(s.pack)}/{kindFolder}/{shortName}";
            AssetPaths.EnsureFolder(folder);
            var (root, model) = BuildModel(dir, json, folder, shortName, s);
            return new ModelImport { json = json, id = id, shortName = shortName, folder = folder, root = root, model = model };
        }

        static (GameObject root, GlbModel model) BuildModel(string dir, JObject json, string folder, string prefix, ExportImportSettings s)
        {
            var file = ExportJson.Str(json["model"]);
            var src = file != null ? Path.Combine(dir, file) : null;
            if (src == null || !File.Exists(src)) throw new FileNotFoundException("model not found", src ?? "(no \"model\")");
            var library = ModelLibraryWriter.Open($"{folder}/{prefix}_models.asset");
            var m = GlbModelImporter.Import(src, new GlbImportOptions
            {
                scale = s.scale,
                textureFolder = folder + "/Textures",
                materialFolder = folder + "/Materials",
                library = library,
                namePrefix = prefix,
                lighting = GlbLightingStyle.Object,
                exportRoot = s.exportFolder,
            });
            library.Save();
            var root = new GameObject(prefix);
            m.root.name = "Model";
            m.root.transform.SetParent(root.transform, false);
            HideNodes(m.root.transform, json);
            return (root, m);
        }

        // "hideNodes" are never shown in the remake and go, "initiallyHiddenNodes" are deactivated. A node holding a bone of
        // a skinned mesh outside it is only deactivated, so that mesh keeps its rig.
        static void HideNodes(Transform model, JObject json)
        {
            var remove = json["hideNodes"] is JArray;
            foreach (var n in json["hideNodes"] as JArray ?? ExportJson.Items(json["initiallyHiddenNodes"]))
            {
                var t = ImportHierarchy.FindDeep(model, (string)n);
                if (t == null || t == model) continue;
                if (remove && !HoldsForeignBone(model, t)) Object.DestroyImmediate(t.gameObject);
                else t.gameObject.SetActive(false);
            }
        }

        static bool HoldsForeignBone(Transform model, Transform node) =>
            model.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => !r.transform.IsChildOf(node))
                .SelectMany(r => r.bones.Append(r.rootBone))
                .Any(b => b != null && b.IsChildOf(node));

        static void PrepareAnimator(GameObject model)
        {
            var animator = ImportHierarchy.GetOrAddComponent<Animator>(model);
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
        }

        static List<CharacterAnimBinding> BuildAnimations(GlbModel m, JObject map, JObject originals)
        {
            var list = new List<CharacterAnimBinding>();
            if (map == null) return list;
            var entries = map.Properties()
                .Select(kv => (key: kv.Name, clip: ExportJson.Str(kv.Value), exact: Enum.TryParse<CharacterAnimRole>(kv.Name, true, out var r), role: r))
                .OrderBy(e => e.exact ? 0 : 1).ToList();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.clip)) continue;
                var role = e.role;
                if (!e.exact && !RoleAliases.TryGetValue(e.key, out role)) continue;
                if (list.Exists(b => b.role == role)) continue;
                var clip = m.clips.FirstOrDefault(kv => string.Equals(kv.Key, e.clip, StringComparison.OrdinalIgnoreCase)).Value;
                if (clip == null)
                {
                    Debug.LogWarning($"[Import] {m.root.name}: animation '{e.clip}' ({e.key}) not in the model.");
                    continue;
                }
                list.Add(Binding(role, clip, originals?[e.clip] as JObject));
            }
            return list;
        }

        static CharacterAnimBinding Binding(CharacterAnimRole role, AnimationClip clip, JObject original)
        {
            var binding = new CharacterAnimBinding { role = role, clip = clip };
            if (original == null) return binding;
            var speed = ExportJson.F(original["speed"]);
            if (speed > 0f) binding.speed = speed;
            if (ExportJson.Str(original["post"]) is { } post && CyclingRoles.Contains(role))
                binding.playback = post switch
                {
                    "oscillate" => CharacterAnimPlayback.PingPong,
                    "repeat" or "offsetRepeat" => CharacterAnimPlayback.Loop,
                    _ => CharacterAnimPlayback.Once,
                };
            return binding;
        }

        /// <summary>Imports the character in <paramref name="dir"/> as a driver prefab and <see cref="CharacterDefinition"/>.</summary>
        public static void ImportCharacter(string dir, ExportImportSettings s)
        {
            var im = Begin(dir, "character.json", "Characters", s);
            try
            {
                PlaceSeatAtOrigin(im, s.scale);
                PrepareAnimator(im.model.root);
                var prefab = PrefabUtility.SaveAsPrefabAsset(im.root, $"{im.folder}/{im.shortName}.prefab");
                var portrait = ImportAssets.ImportEntryOrPattern(dir, ExportJson.Str(im.json["portrait"]), s.portraitPattern, s, im.id,
                    $"{im.folder}/{AssetPaths.SafeFileName(im.id)}_portrait");
                var voices = im.json["voices"] is JObject voiceMap ? ImportVoices(dir, im, voiceMap) : null;
                var def = AssetPaths.LoadOrCreate<CharacterDefinition>($"{im.folder}/{im.shortName}.asset");
                ImportAssets.SetIdentity(def, im.id, ExportJson.DisplayName(im.json, im.shortName), s.pack.packId);
                def.driverPrefab = prefab;
                def.modelScale = ExportJson.F(im.json["modelScale"], s.characterScale);
                var kart = ResolveDefaultKart(s, im.json, im.id);
                if (kart != null) def.defaultKart = kart;
                def.animations = BuildAnimations(im.model, im.json["animations"] as JObject, im.json["originalAnimations"] as JObject);
                if (portrait != null) def.portrait = portrait;
                if (voices != null) def.voices = voices;
                EditorUtility.SetDirty(def);
                ImportAssets.AddToPack(s.pack, def);
                Debug.Log($"[Import] Character {im.id}: {def.animations.Count} animation roles of {im.model.clips.Count} clips, kart {(def.defaultKart != null ? def.defaultKart.Id : "none")}");
            }
            finally
            {
                Object.DestroyImmediate(im.root);
            }
        }

        static void PlaceSeatAtOrigin(ModelImport im, float scale)
        {
            var model = im.model.root.transform;
            var seatNode = ImportHierarchy.FindDeep(model, ExportJson.Str(im.json["seatNode"]));
            if (seatNode != null) model.localPosition = -im.root.transform.InverseTransformPoint(seatNode.position);
            else if (im.json["seatOffset"] is JArray seat) model.localPosition = -ExportJson.Pos(seat, scale);
        }

        static KartDefinition ResolveDefaultKart(ExportImportSettings s, JObject json, string id)
        {
            var kartRef = ExportJson.Str(json["kartModel"]);
            var ownKart = kartRef != null ? s.pack.GetAll<KartDefinition>().FirstOrDefault(k => k.Id == kartRef) : null;
            return ownKart != null ? ownKart : s.KartFor(id);
        }

        static List<CharacterVoiceBinding> ImportVoices(string dir, ModelImport im, JObject voices)
        {
            var result = new List<CharacterVoiceBinding>();
            foreach (var v in voices.Properties())
            {
                if (!Enum.TryParse<CharacterVoice>(v.Name, true, out var voice)) continue;
                var files = v.Value is JArray arr ? arr.Select(x => (string)x).ToList() : new List<string> { ExportJson.Str(v.Value) };
                var clips = files.Where(f => f != null)
                    .Select((f, i) => ImportAssets.ImportAudio(Path.GetFullPath(Path.Combine(dir, f)),
                        $"{im.folder}/Audio/{im.shortName}_{voice.ToString().ToLowerInvariant()}_{i + 1}", false))
                    .Where(c => c != null).ToArray();
                if (clips.Length > 0) result.Add(new CharacterVoiceBinding { voice = voice, clips = clips });
            }
            return result;
        }

        /// <summary>Imports the kart in <paramref name="dir"/> as a kart prefab and <see cref="KartDefinition"/>.</summary>
        public static void ImportKart(string dir, ExportImportSettings s)
        {
            var im = Begin(dir, "kart.json", "Karts", s);
            try
            {
                var km = im.root.AddComponent<KartModel>();
                AssignWheels(km, im.model.root.transform, im.json, s.scale);
                km.seat = ResolveSeat(im, s.scale);
                if (im.json["animations"] is JObject) PrepareAnimator(im.model.root);
                else ImportHierarchy.StripAnimation(im.model.root);
                var prefab = PrefabUtility.SaveAsPrefabAsset(im.root, $"{im.folder}/{im.shortName}.prefab");
                var def = AssetPaths.LoadOrCreate<KartDefinition>($"{im.folder}/{im.shortName}.asset");
                ImportAssets.SetIdentity(def, im.id, ExportJson.DisplayName(im.json, im.shortName), s.pack.packId);
                def.kartPrefab = prefab;
                def.animations = BuildAnimations(im.model, im.json["animations"] as JObject, im.json["originalAnimations"] as JObject);
                EditorUtility.SetDirty(def);
                ImportAssets.AddToPack(s.pack, def);
                Debug.Log($"[Import] Kart {im.id}: wheels {km.frontWheels.Length}+{km.rearWheels.Length}, seat {km.seat.name}, {def.animations.Count} animations");
            }
            finally
            {
                Object.DestroyImmediate(im.root);
            }
        }

        static void AssignWheels(KartModel km, Transform model, JObject json, float scale)
        {
            var wheels = ExportJson.Items(json["wheelNodes"]).Select(t => (string)t).ToList();
            var steer = ExportJson.Items(json["steeringNodes"]).Select(t => (string)t).ToList();
            km.frontWheels = wheels.Where((n, i) => IsFrontWheel(n, i, steer)).Select(n => ImportHierarchy.FindDeep(model, n)).Where(t => t != null).ToArray();
            km.rearWheels = wheels.Where((n, i) => !IsFrontWheel(n, i, steer)).Select(n => ImportHierarchy.FindDeep(model, n)).Where(t => t != null).ToArray();
            km.wheelRadius = ExportJson.F(json["wheelRadius"], km.wheelRadius / scale) * scale;
            if (ExportJson.Str(json["steeringWheelNode"]) is { } sw && ImportHierarchy.FindDeep(model, sw) is { } swt) km.steeringPivots = new[] { swt };
        }

        static bool IsFrontWheel(string name, int index, List<string> steer)
        {
            if (steer.Contains(name)) return true;
            var l = name.ToLowerInvariant();
            if (l.Contains("front") || l.EndsWith("fl") || l.EndsWith("fr")) return true;
            if (l.Contains("rear") || l.Contains("back") || l.EndsWith("bl") || l.EndsWith("br") || l.EndsWith("rl") || l.EndsWith("rr")) return false;
            return index < 2; // convention: FL, FR, RL, RR
        }

        static Transform ResolveSeat(ModelImport im, float scale)
        {
            var seatName = ExportJson.Str(im.json["seatNode"]) ?? ExportJson.Str(im.json["characterMountNode"]);
            var seat = ImportHierarchy.FindDeep(im.model.root.transform, seatName);
            if (seat == null && im.json["seatOffset"] is JArray offset)
            {
                seat = ImportHierarchy.CreateChild(im.root.transform, "Seat");
                seat.localPosition = ExportJson.Pos(offset, scale);
            }
            return seat != null ? seat : im.root.transform;
        }
    }
}
