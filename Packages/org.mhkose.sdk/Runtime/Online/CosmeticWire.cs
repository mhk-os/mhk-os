using System.Collections.Generic;
using MHK.Cosmetics;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Wire form of a racer's cosmetics (docs/protocol.md, "Cosmetics"). It is a list of at most <see cref="MaxCosmetics"/>
    /// entries, each a content id and a colour as four bytes (r, g, b, a). Cosmetics are purely visual and no lobby
    /// content, so the server passes them on unchecked and a client skips the ids it does not have.
    /// </summary>
    public static class CosmeticWire
    {
        /// <summary>Most cosmetics one racer sends (one per slot, with room for future slots).</summary>
        public const int MaxCosmetics = 8;
        // An empty id (one length byte) and the colour.
        const int MinEntryBytes = 5;

        /// <summary>
        /// Writes the first <see cref="MaxCosmetics"/> of <paramref name="cosmetics"/> whose ids are content ids
        /// (<see cref="ContentManifests.IsContentId"/>, the only ones the server passes on). Null writes an empty list.
        /// </summary>
        public static void Write(WireWriter w, IReadOnlyList<CosmeticChoice> cosmetics, int maxIdBytes)
        {
            var sent = new List<CosmeticChoice>(MaxCosmetics);
            if (cosmetics != null)
                foreach (var c in cosmetics)
                    if (sent.Count < MaxCosmetics && ContentManifests.IsContentId(c.id) && c.id.Length <= maxIdBytes) sent.Add(c);
            w.ListCount(sent.Count, MaxCosmetics);
            foreach (var c in sent)
            {
                w.String(c.id, maxIdBytes);
                w.U8(c.color.r);
                w.U8(c.color.g);
                w.U8(c.color.b);
                w.U8(c.color.a);
            }
        }

        public static List<CosmeticChoice> Read(WireReader r, int maxIdBytes)
        {
            var count = r.ListCount(MaxCosmetics, MinEntryBytes);
            var cosmetics = new List<CosmeticChoice>(count);
            for (var i = 0; i < count; i++)
                cosmetics.Add(new CosmeticChoice(r.String(maxIdBytes), new Color32(r.U8(), r.U8(), r.U8(), r.U8())));
            return cosmetics;
        }
    }
}
