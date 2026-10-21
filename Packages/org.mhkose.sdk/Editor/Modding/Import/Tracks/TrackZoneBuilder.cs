using System.Collections.Generic;
using MHK.Race;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MHK.Editor.Modding.Import
{
    /// <summary>
    /// Trigger zones of track.json ("zones": respawn, kill, boost, conveyor, wind and slick boxes) and the "boostPads"
    /// speed pads facing the driving direction.
    /// </summary>
    public static class TrackZoneBuilder
    {
        /// <summary>Adds the zones under <paramref name="zones"/>. Unknown kinds are skipped with a warning.</summary>
        public static void BuildZones(TrackImportContext c, Transform zones)
        {
            var skipped = new Dictionary<string, int>();
            foreach (var z in ExportJson.Items(c.Json["zones"]))
            {
                var kind = ExportJson.Str(z["kind"], "respawn");
                if (ZoneKindFor(kind, z, c.Settings) is { } zoneKind) AddZone(c, zones, z, kind, zoneKind);
                else skipped[kind] = skipped.TryGetValue(kind, out var n) ? n + 1 : 1;
            }
            foreach (var (kind, n) in skipped)
                Debug.LogWarning($"[Import] Track {c.ShortName}: {n} '{kind}' zone(s) skipped (no engine zone of that kind).");
        }

        static TrackZoneKind? ZoneKindFor(string kind, JToken z, ExportImportSettings s) => kind switch
        {
            "kill" => TrackZoneKind.Kill,
            "respawn" => TrackZoneKind.RespawnPoint,
            "boost" => TrackZoneKind.Boost,
            "conveyor" or "wind" when z["pushDir"] != null => TrackZoneKind.Push,
            "slick" when !string.IsNullOrEmpty(s.slickEffectId) => TrackZoneKind.Effect,
            _ => null,
        };

        static void AddZone(TrackImportContext c, Transform zones, JToken z, string kind, TrackZoneKind zoneKind)
        {
            var go = ImportHierarchy.CreateChild(zones, ExportJson.Str(z["name"]) ?? kind).gameObject;
            go.transform.localPosition = ExportJson.Pos(z["pos"], c.Scale);
            var rotation = ExportJson.Rot(z["rot"]);
            var size = z["radius"] != null && z["size"] == null
                ? Vector3.one * (2f * ExportJson.F(z["radius"]))
                : ExportJson.Scale(z["size"]);
            if (zoneKind == TrackZoneKind.Push) AlignBox(ref rotation, ref size, ExportJson.Dir(z["pushDir"]));
            go.transform.localRotation = rotation;
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size * c.Scale;
            var zone = go.AddComponent<TrackZone>();
            zone.kind = zoneKind;
            ApplyZoneValues(c, zone, kind, z);
            if (z["respawn"] is JObject rp)
            {
                var t = ImportHierarchy.CreateChild(go.transform, "RespawnPoint");
                t.SetPositionAndRotation(ExportJson.Pos(rp["pos"], c.Scale), ExportJson.Rot(rp["rot"]));
                zone.respawnPoint = t;
            }
        }

        static void ApplyZoneValues(TrackImportContext c, TrackZone zone, string kind, JToken z)
        {
            switch (kind)
            {
                case "boost":
                    zone.effectId = c.Settings.boostPadEffectId;
                    break;
                case "conveyor":
                    zone.value = ExportJson.F(z["pushSpeed"]) * c.Scale;
                    break;
                case "wind":
                    zone.value = ExportJson.F(z["pushSpeedAtOneMetre"]) * c.Scale * c.Scale;
                    zone.pushFalloff = true;
                    break;
                case "slick":
                    zone.effectId = c.Settings.slickEffectId;
                    break;
            }
        }

        // Turns the box about its own axes so +Z follows dir as closely as it can, permuting size to match.
        static void AlignBox(ref Quaternion rotation, ref Vector3 size, Vector3 dir)
        {
            var local = Quaternion.Inverse(rotation) * dir.normalized;
            var ax = Mathf.Abs(local.x);
            var ay = Mathf.Abs(local.y);
            var az = Mathf.Abs(local.z);
            if (ax >= ay && ax >= az)
            {
                rotation *= Quaternion.Euler(0f, local.x > 0f ? 90f : -90f, 0f);
                size = new Vector3(size.z, size.y, size.x);
            }
            else if (ay > az)
            {
                rotation *= Quaternion.Euler(local.y > 0f ? -90f : 90f, 0f, 0f);
                size = new Vector3(size.x, size.z, size.y);
            }
            else if (local.z < 0f) rotation *= Quaternion.Euler(0f, 180f, 0f);
        }

        /// <summary>Speed pads ("boostPads": pos, rot, "size" full extents or "halfSize"), turned to face along <paramref name="path"/>.</summary>
        public static void BuildBoostPads(TrackImportContext c, Transform zones, TrackPath path)
        {
            foreach (var pad in ExportJson.Items(c.Json["boostPads"]))
            {
                var t = ImportHierarchy.CreateChild(zones, "Speed Pad");
                t.SetLocalPositionAndRotation(ExportJson.Pos(pad["pos"], c.Scale), ExportJson.Rot(pad["rot"]));
                if (path.Count >= 2 && Vector3.Dot(t.forward, path.GetTangent(path.Project(t.position))) < 0f)
                    t.Rotate(0f, 180f, 0f, Space.Self);
                var box = t.gameObject.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = pad["halfSize"] != null ? ExportJson.Scale(pad["halfSize"]) * (2f * c.Scale) : ExportJson.Scale(pad["size"]) * c.Scale;
                var zone = t.gameObject.AddComponent<TrackZone>();
                zone.kind = TrackZoneKind.Boost;
                zone.effectId = c.Settings.boostPadEffectId;
            }
        }
    }
}
