using System;
using System.Collections.Generic;
using MHK.Achievements;
using MHK.Editor.Modding;
using MHK.Modding;
using UnityEditor;
using UnityEngine;

namespace MHK.Editor.Achievements
{
    /// <summary>
    /// Creates or updates the game's built-in achievements: assets in an Achievements folder next to the always-on game
    /// pack (Resources/BuiltinContent) and listed in its content. The achievements of the original games require their
    /// packs (requiredPacks), so they need no parts; mods add parts to them.
    /// </summary>
    public static class BuiltinAchievements
    {
        const string IdPrefix = "core.achievement.";
        const string SourceGame = "core";
        const string FolderName = "Achievements";
        static readonly string[] OfficialGames = { "mhk2", "mhk3", "mhke", "mhkt" };

        sealed class Spec
        {
            public string name, title, description, icon;
            public int order;
            public bool secret;
            public AchievementGoalKind kind;
            public string[] stats = Array.Empty<string>();
            public long target = 1;
            public string set = "";
            public AchievementCollect collect;
            public string[] packs = Array.Empty<string>();
            public string unit = "";
            public float scale = 1f;
        }

        static Spec Counter(string name, string title, string description, string icon, int order, string stat, long target) => new()
        {
            name = name, title = title, description = description, icon = icon, order = order, kind = AchievementGoalKind.Counter,
            stats = new[] { stat }, target = target,
        };

        static Spec Distance(string name, string title, string description, string icon, int order, long km)
        {
            var spec = Counter(name, title, description, icon, order, AchievementStats.DistanceMetres, km * 1000);
            spec.unit = "km";
            spec.scale = 0.001f;
            return spec;
        }

        static Spec Collection(string name, string title, string description, string icon, int order, string set, AchievementCollect collect,
            string unit, params string[] packs) => new()
        {
            name = name, title = title, description = description, icon = icon, order = order, kind = AchievementGoalKind.Collection,
            set = set, collect = collect, target = 0, unit = unit, packs = packs,
        };

        static Spec[] Specs => new[]
        {
            Counter("first_win", "First Victory", "Win a race.", "emoji_events", 10, AchievementStats.RacesWon, 1),
            Counter("wins_25", "Serial Winner", "Win 25 races.", "star", 11, AchievementStats.RacesWon, 25),
            Counter("top3_10", "On the Podium", "Finish 10 races in the top 3.", "counter_3", 20, AchievementStats.RacesTop3, 10),
            Counter("top3_50", "Podium Regular", "Finish 50 races in the top 3.", "military_tech", 21, AchievementStats.RacesTop3, 50),
            Counter("top3_100", "Podium Legend", "Finish 100 races in the top 3.", "crown", 22, AchievementStats.RacesTop3, 100),
            Counter("races_100", "Seasoned Racer", "Finish 100 races.", "sports_motorsports", 25, AchievementStats.RacesFinished, 100),
            Counter("perfect_championship", "Flawless Championship", "Win every race of a championship.", "sports_score", 30,
                AchievementStats.PerfectChampionships, 1),
            Collection("all_championships", "Champion of Champions",
                "Win every race of every championship of Moorhuhn Kart 2, 3, Extra and Thunder.", "stadium", 31,
                AchievementStats.PerfectCups, AchievementCollect.Cup, "championships", OfficialGames),
            Collection("all_tracks", "Grand Tour", "Finish a race on every track of Moorhuhn Kart 2, 3, Extra and Thunder.", "map", 40,
                AchievementStats.TracksFinished, AchievementCollect.RaceTrack, "tracks", OfficialGames),
            Counter("under_2min", "Speed Demon", "Finish a race in under 2 minutes.", "timer", 50, AchievementStats.RacesUnder2Min, 1),
            Distance("distance_10", "Road Trip", "Drive 10 km.", "speed", 60, 10),
            Distance("distance_100", "Long Haul", "Drive 100 km.", "landscape", 61, 100),
            Distance("distance_1000", "Around the World (Almost)", "Drive 1000 km.", "public", 62, 1000),
            new Spec
            {
                name = "crowd_control", title = "Crowd Control", description = "Hit 5 or more karts with a single Shrinker or Froster shot.",
                icon = "bolt", order = 70, kind = AchievementGoalKind.Maximum,
                stats = new[] { AchievementStats.PowerupHits("mhk3.shrinker"), AchievementStats.PowerupHits("mhk3.froster") }, target = 5,
                packs = new[] { "mhk3" },
            },
            new Spec
            {
                name = "hammer_double", title = "Two for One", description = "Hit 2 karts with a single swing of the Wooden Hammer.",
                icon = "repeat", order = 71, kind = AchievementGoalKind.Maximum, stats = new[] { AchievementStats.PowerupHits("mhke.wooden_hammer") },
                target = 2, packs = new[] { "mhke" },
            },
            new Spec
            {
                name = "own_trap", title = "Oops!", description = "Run into a hazard you planted yourself.", icon = "favorite", order = 72,
                secret = true, kind = AchievementGoalKind.Counter, stats = new[] { AchievementStats.OwnTraps }, target = 1,
            },
            Counter("battle_first", "Arena Debut", "Win a battle.", "shield", 80, AchievementStats.BattlesWon, 1),
            Counter("battles_10", "Last One Standing", "Win 10 battles.", "swords", 81, AchievementStats.BattlesWon, 10),
            Collection("all_arenas", "Arena Tourist", "Fight a battle in every arena of Moorhuhn Kart Thunder.", "flag", 82,
                AchievementStats.TracksFinished, AchievementCollect.BattleArena, "arenas", "mhkt"),
            Counter("online_win", "Online Champion", "Win an online race against other players.", "leaderboard", 90,
                AchievementStats.OnlineRacesWon, 1),
            Counter("upload_ghost", "Ghost Writer", "Upload a time-trial ghost.", "upload", 91, AchievementStats.TimeTrialsUploaded, 1),
        };

        /// <summary>Writes every built-in achievement (existing assets are updated in place) and lists it in the game pack.</summary>
        [MenuItem("MHK/Content/Create Built-in Achievements")]
        public static void Create()
        {
            var pack = GamePack();
            if (pack == null)
            {
                Debug.LogError($"[Achievements] No '{PackAssetPaths.GamePackId}' pack in Resources/{ModLoader.BuiltinCatalogResource}.");
                return;
            }
            var folder = AssetPaths.DirectoryOf(pack) + "/" + FolderName;
            var added = 0;
            var specs = Specs;
            foreach (var spec in specs)
            {
                var def = Write(folder, spec);
                if (pack.content.Contains(def)) continue;
                pack.content.Add(def);
                added++;
            }
            EditorUtility.SetDirty(pack);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Achievements] {specs.Length} built-in achievements written to {folder}, {added} added to the '{pack.packId}' pack.");
        }

        static ContentPack GamePack()
        {
            var catalog = Resources.Load<BuiltinContentCatalog>(ModLoader.BuiltinCatalogResource);
            if (catalog == null) return null;
            foreach (var p in catalog.AllPacks())
                if (p.packId == PackAssetPaths.GamePackId) return p;
            return null;
        }

        static AchievementDefinition Write(string folder, Spec spec)
        {
            var def = AssetPaths.LoadOrCreate<AchievementDefinition>($"{folder}/{spec.name}.asset");
            var so = new SerializedObject(def);
            so.FindProperty("id").stringValue = IdPrefix + spec.name;
            so.FindProperty("displayName").stringValue = spec.title;
            so.FindProperty("sourceGame").stringValue = SourceGame;
            so.ApplyModifiedPropertiesWithoutUndo();
            def.description = spec.description;
            def.iconName = spec.icon;
            def.secret = spec.secret;
            def.order = spec.order;
            def.kind = spec.kind;
            def.stats = new List<string>(spec.stats);
            def.target = spec.target;
            def.stat = spec.set;
            def.collect = spec.collect;
            def.requiredPacks = new List<string>(spec.packs);
            def.unit = spec.unit;
            def.displayScale = spec.scale;
            EditorUtility.SetDirty(def);
            return def;
        }
    }
}
