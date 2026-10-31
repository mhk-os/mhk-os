using System.Collections.Generic;
using MHK.Core;
using MHK.Online;
using MHK.Race;
using UnityEngine;

namespace MHK.Powerups
{
    /// <summary>
    /// The item part of an online race's <see cref="RaceLoaded"/>: what this client's track and content make of the
    /// item boxes, the item pool, the character specials and the item rules. The server takes the racers' majority and
    /// draws items from it, so every value here must be the same on every client with the same content.
    /// </summary>
    public static class NetItemContent
    {
        const float WeightStep = 1f / (RaceProtocol.PoolWeights - 1);
        const float TenthsPerSecond = 10f;
        const float MsPerSecond = 1000f;

        /// <summary>
        /// Fills <paramref name="loaded"/>'s boxes (stable order: box id = index, <see cref="StableOrder"/>), pool, specials
        /// per grid slot and rules for <paramref name="race"/>, once its track and karts are loaded.
        /// </summary>
        public static void Fill(RaceLoaded loaded, RaceManager race)
        {
            var content = GameRoot.Instance.Content;
            var config = GameRoot.Instance.Config;

            loaded.boxes.Clear();
            var boxes = new List<ItemBox>();
            StableOrder.Collect(boxes);
            foreach (var b in boxes)
                loaded.boxes.Add(new ItemBoxInfo
                {
                    pos = b.transform.position,
                    respawnTenths = Mathf.RoundToInt(b.RespawnSeconds * TenthsPerSecond),
                    fixedContent = b.fixedPowerupId ?? "",
                });

            loaded.pool.Clear();
            var pool = new List<ItemPoolEntry>();
            ItemOdds.BuildPool(content, race.Session, pool);
            foreach (var p in pool)
            {
                var entry = new PoolEntry { powerupId = p.powerup.Id, charges = p.powerup.charges, battleCharges = p.powerup.battleCharges };
                for (var i = 0; i < entry.weights.Length; i++) entry.weights[i] = p.WeightAt(i * WeightStep);
                loaded.pool.Add(entry);
            }

            // Only lobby content: a cross-pack special resolves only where its pack is installed (the server drops the
            // others too).
            loaded.specials.Clear();
            var online = OnlineRace.Current;
            var packs = OnlineLobby.Current != null ? OnlineLobby.Current.settings.packs : null;
            if (online != null)
                foreach (var k in race.Karts)
                {
                    var slot = online.SlotOf(k);
                    var special = k.Character != null ? k.Character.specialPowerup : null;
                    if (slot < 0 || special == null) continue;
                    if (packs != null && !ContentManifests.IsLobbyContent(content, special, packs)) continue;
                    loaded.specials.Add(new SpecialEntry { slot = slot, powerupId = special.Id });
                }
            loaded.specials.Sort((a, b) => a.slot.CompareTo(b.slot));

            loaded.rules = new ItemRules
            {
                replacesHeld = config.itemBoxReplacesHeld,
                rouletteMs = Mathf.RoundToInt(config.itemRouletteSeconds * MsPerSecond),
            };
        }
    }
}
