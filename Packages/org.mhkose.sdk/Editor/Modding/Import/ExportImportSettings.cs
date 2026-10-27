using System;
using System.Collections.Generic;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>A track whose models and coordinates get an extra scale (narrow tracks enlarged for the remake's karts).</summary>
    [Serializable]
    public class TrackScaleOverride
    {
        [Tooltip("Track id as in its track.json.")]
        public string trackId;
        [Tooltip("Multiplier on ExportImportSettings.scale for this track.")]
        public float scale = 1f;
    }

    /// <summary>A hand-tuned pose of a track's Lighting/Sun that replaces the one the import derives from track.json.</summary>
    [Serializable]
    public class TrackSunOverride
    {
        [Tooltip("Track id as in its track.json.")]
        public string trackId;
        [Tooltip("Local position of Lighting/Sun.")]
        public Vector3 position;
        [Tooltip("Local rotation of Lighting/Sun (the light shines along its forward).")]
        public Quaternion rotation = Quaternion.identity;
    }

    /// <summary>A fixed item spawn type ("fixed:&lt;type&gt;" in track.json itemSpawns) and what its box gives.</summary>
    [Serializable]
    public class FixedItemMapping
    {
        [Tooltip("The &lt;type&gt; of \"fixed:&lt;type&gt;\".")]
        public string type;
        [Tooltip("ItemBox.fixedPowerupId: a powerup id, a status effect id (applied on contact) or @special.")]
        public string contentId;
        [Tooltip("Item box prefab for this type (empty = the default item box).")]
        public GameObject prefab;
    }

    /// <summary>The default kart of one imported character.</summary>
    [Serializable]
    public class CharacterKartMapping
    {
        [Tooltip("Character id as in its character.json.")]
        public string characterId;
        [Tooltip("Kart the character drives when the player does not pick one (any loaded kart, e.g. a core kart).")]
        public KartDefinition kart;
    }

    /// <summary>
    /// Per-pack settings of <b>MHK ▸ Modding ▸ Import Export Folder…</b>: source folder, target pack, scale, and track and
    /// character import options. Select it and run the menu again (or its context menu) to re-import.
    /// </summary>
    [CreateAssetMenu(menuName = "MHK/Modding/Export Import Settings", fileName = "ImportSettings")]
    public class ExportImportSettings : ScriptableObject
    {
        [Tooltip("Folder with the converter output (absolute path).")]
        public string exportFolder;
        [Tooltip("Pack that receives the definitions. The assets are written into its folder (Tracks/, Characters/, Karts/).")]
        public ContentPack pack;

        [Header("What")]
        [Tooltip("Import tracks/<id>/track.json.")]
        public bool tracks = true;
        [Tooltip("Import characters/<id>/character.json.")]
        public bool characters = true;
        [Tooltip("Import karts/<id>/kart.json.")]
        public bool karts = true;
        [Tooltip("Import only the track / character / kart with this id (empty = all).")]
        public string onlyId;
        [Tooltip("After the import, merge the textures, audio and materials it added into identical ones of the pack and " +
                 "optimize the model libraries it wrote (animation keys, mesh Read/Write). The rest of the pack stays as it is.")]
        public bool optimizeAfterImport = true;

        [Header("Scale")]
        [Tooltip("Uniform scale of every model and track coordinate (1 = the export's metres, the export contract sizes " +
                 "karts like MHK2's).")]
        public float scale = 1f;
        [Tooltip("Extra scale per track.")]
        public List<TrackScaleOverride> trackScales = new();

        [Header("Tracks")]
        [Tooltip("Item box for \"random\" item spawns.")]
        public GameObject itemBoxPrefab;
        [Tooltip("Boxes for \"fixed:<type>\" item spawns.")]
        public List<FixedItemMapping> fixedItems = new();
        [Tooltip("Laps when track.json has none.")]
        [Min(1)] public int defaultLaps = 3;
        [Tooltip("Width (m) of racing-line nodes without one.")]
        public float defaultPathWidth = 10f;
        [Tooltip("Racing-line and route nodes wider than this × the median width of their 3 neighbours on each side (and " +
                 "wider than 30 m) get that median (TrackPathTools.ClampWidthSpikes): converted AI lanes measured across " +
                 "both branches where a route leaves the line. 0 = off.")]
        [Min(0f)] public float widthSpikeFactor = Tracks.TrackPathTools.DefaultWidthSpikeFactor;
        [Tooltip("Lighting settings asset of the track scenes.")]
        public LightingSettings lightingSettings;
        [Tooltip("Physics material of drivable collision.")]
        public PhysicsMaterial groundMaterial;
        [Tooltip("Physics material of wall collision.")]
        public PhysicsMaterial wallMaterial;
        [Tooltip("Linear-space multiplier on the export's ambient colour (the originals lit in gamma space with baked " +
                 "world light, real-time lighting needs more ambient).")]
        public float ambientScale = 3f;
        [Tooltip("Multiplier on sun and fill light intensities.")]
        public float lightScale = 1.3f;
        [Tooltip("Extra multiplier on fill lights (they cast no shadows).")]
        public float fillScale = 1f;
        [Tooltip("Sun shadow strength (the built-in tracks use 0.5).")]
        [Range(0f, 1f)] public float sunShadowStrength = 0.5f;
        [Tooltip("Height (m) under the lowest visual geometry where karts are respawned, when track.json has no respawnHeight.")]
        public float killDepth = 15f;
        [Tooltip("Speed pads (track.json boostPads): status effect id applied.")]
        public string boostPadEffectId;
        [Tooltip("Slick zones (track.json zones kind \"slick\": oil): status effect id the karts have while inside (a short " +
                 "low-grip effect). Empty = slick zones are skipped.")]
        public string slickEffectId;
        [Tooltip("Crushers / hazards (props with \"crusher\" or \"hazard\"): status effect id applied to the hit kart " +
                 "(e.g. the built-in mhk3.hit.crushed). Ids are resolved at race time, so a mod can use core effects by id.")]
        public string crushEffectId;
        [Tooltip("Squash volumes (props with \"squash\": TINcan surface 14, closing doors): status effect id applied to the " +
                 "kart inside. Empty = crushEffectId.")]
        public string squashEffectId;
        [Tooltip("Every crusher / squash volume is a touch hazard (a trigger: any overlap applies the effect), ignoring the " +
                 "export's \"stomp\" hint. The originals' ODE crush geoms were triggers (TINcan surfaces 14 / 16).")]
        public bool crushersAsTriggers;
        [Tooltip("Moving walls (animated colliding props without a hazard) also crush karts they come down on (a stomp " +
                 "hazard with crushEffectId): closing gates and lowered containers squash a kart under them instead of " +
                 "pressing it into the floor.")]
        public bool movingWallsCrush;
        [Tooltip("Karts in the air with no ground within this distance (m, before the track scale) below them fall: the " +
                 "drop effect (freeze, fall animation, respawn) as on drop geometry (TrackRuntime.fallDepth, TINcan: a kart " +
                 "whose ground ray misses falls). 0 = off: karts land on whatever is below.")]
        [Min(0f)] public float fallDepth;
        [Tooltip("Alternative routes (track.json altPaths) are drawn on the race minimap (TrackPath.showOnMinimap).")]
        public bool routesOnMinimap = true;
        [Tooltip("Sun poses per track that replace the one derived from track.json (hand-tuned lighting survives re-imports).")]
        public List<TrackSunOverride> sunOverrides = new();
        [Tooltip("Ambient sounds (track \"sounds\", prop \"sound\"): volume when the entry has none.")]
        [Range(0f, 1f)] public float ambienceVolume = 0.6f;
        [Tooltip("Ambient sounds: full volume within this distance (m) when the entry has none.")]
        public float ambienceMinDistance = 2f;
        [Tooltip("Ambient sounds: silent beyond this distance (m) when the entry has none.")]
        public float ambienceMaxDistance = 25f;
        [Tooltip("Kart physics overrides written to every imported track.")]
        public TrackPhysicsSettings trackPhysics = new();
        [Tooltip("Collision triangles with a longer edge (m) are split (PhysX handles triangles over 500 m badly). 0 = off.")]
        public float maxCollisionEdge = 100f;
        [Tooltip("Split large visual meshes into chunks after the import (MHK ▸ Optimize ▸ Chunk Large Meshes).")]
        public bool chunkLargeMeshes = true;
        [Tooltip("A racing line crossing the finish line farther (m) from the start checkpoint's centre is reported.")]
        [Min(0f)] public float finishLineReportDistance = 3f;
        [Tooltip("Alternative routes bypassing less of the lap than this share are skipped.")]
        [Range(0f, 1f)] public float minRouteBypass = 0.01f;
        [Tooltip("Alternative routes bypassing more of the lap than this share are skipped.")]
        [Range(0f, 1f)] public float maxRouteBypass = 0.5f;
        [Tooltip("Rigidbody sleep threshold of dynamic props.")]
        [Min(0f)] public float dynamicPropSleepThreshold = 0.1f;

        [Header("Track fallbacks (no export value)")]
        [Tooltip("Flat ambient colour.")]
        public Color defaultAmbient = new(0.4f, 0.4f, 0.4f);
        [Tooltip("Direction the sun shines along, in scene space.")]
        public Vector3 defaultSunDirection = new(0.3f, -0.8f, 0.5f);
        [Tooltip("Fog density at scale 1.")]
        public float defaultFogDensity = 0.002f;
        [Tooltip("Linear fog start (m, before the track scale).")]
        public float defaultFogStart = 50f;
        [Tooltip("Linear fog end (m, before the track scale).")]
        public float defaultFogEnd = 600f;
        [Tooltip("Race camera far clip (m, before the track scale).")]
        public float defaultFarClip = 1000f;

        [Header("Characters")]
        [Tooltip("Default kart of imported characters without an entry in characterKarts (a core kart works: the mod " +
                 "bundle carries a copy that is resolved to the game's kart by id when the mod loads).")]
        public KartDefinition defaultKart;
        [Tooltip("Default kart per character.")]
        public List<CharacterKartMapping> characterKarts = new();
        [Tooltip("CharacterDefinition.modelScale of imported characters (character.json \"modelScale\" wins).")]
        public float characterScale = 1f;
        [Tooltip("Portrait picture of characters without a \"portrait\" entry, relative to the export folder. {0} = the " +
                 "character's id without its pack prefix (e.g. ui/portraits/{0}.png). Empty or missing file = none (render one " +
                 "with MHK ▸ Content ▸ Render Missing Previews, Portraits & Icons).")]
        public string portraitPattern;
        [Tooltip("Preview picture of tracks without a \"preview\" entry, relative to the export folder. {0} = the track's " +
                 "id without its pack prefix. Empty = none (capture one in game).")]
        public string previewPattern;

        /// <summary>Scale of the track <paramref name="trackId"/>.</summary>
        public float TrackScale(string trackId)
        {
            foreach (var t in trackScales)
                if (t != null && t.trackId == trackId) return scale * t.scale;
            return scale;
        }

        /// <summary>The default kart of <paramref name="characterId"/>.</summary>
        public KartDefinition KartFor(string characterId)
        {
            foreach (var m in characterKarts)
                if (m != null && m.characterId == characterId && m.kart != null) return m.kart;
            return defaultKart;
        }

        /// <summary>Fills the empty asset references with the SDK defaults (and the game's item box in the game project) and saves the settings when that changed them.</summary>
        public void ApplyDefaultAssets()
        {
            if (!(Fill(ref itemBoxPrefab, SdkPaths.ItemBoxPrefab) | Fill(ref lightingSettings, SdkPaths.TrackLighting) |
                  Fill(ref groundMaterial, SdkPaths.TrackGroundMaterial) | Fill(ref wallMaterial, SdkPaths.TrackWallMaterial)))
                return;
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssetIfDirty(this);
        }

        static bool Fill<T>(ref T field, string path) where T : UnityEngine.Object
        {
            if (field != null) return false;
            field = AssetDatabase.LoadAssetAtPath<T>(path);
            return field != null;
        }

        [ContextMenu("Import")]
        void ImportNow() => ExportFolderImporter.Import(this);
    }
}
