using System.Collections.Generic;
using System.IO;
using MHK.Cosmetics;
using MHK.Online;
using UnityEngine;

namespace MHK.Ghosts
{
    /// <summary>
    /// Header block of a ghost file (format <see cref="GhostFile.FormatVersion"/>). Fields in a fixed order, then the
    /// cosmetics the driver wore when there are any. Games before cosmetics stop reading before them.
    /// </summary>
    static class GhostHeaderCodec
    {
        const int MaxLaps = 10000;

        public static void Write(BinaryWriter w, GhostHeader h)
        {
            w.Write(h.id ?? "");
            w.Write(h.gameBuild ?? "");
            w.Write(h.rulesVersion);
            w.Write(h.playerName ?? "");
            w.Write(h.playerId ?? "");
            w.Write(h.dateUtcTicks);
            w.Write(h.trackId ?? "");
            w.Write(h.trackPackId ?? "");
            w.Write(h.trackPackVersion ?? "");
            w.Write(h.trackVersion);
            w.Write(h.variant ?? "");
            w.Write(h.kartId ?? "");
            w.Write(h.kartVersion);
            w.Write(h.characterId ?? "");
            w.Write(h.engineClass);
            w.Write(h.engineClassLabel ?? "");
            w.Write(h.laps);
            w.Write(h.tickSeconds);
            w.Write(h.speedScale);
            w.Write(h.finishTime);
            w.Write(h.lapTimes.Length);
            foreach (var t in h.lapTimes) w.Write(t);
            w.Write(h.frameCount);
            WriteCosmetics(w, h.cosmetics);
        }

        public static GhostHeader Read(BinaryReader r)
        {
            var h = new GhostHeader
            {
                id = r.ReadString(),
                gameBuild = r.ReadString(),
                rulesVersion = r.ReadInt32(),
                playerName = r.ReadString(),
                playerId = r.ReadString(),
                dateUtcTicks = r.ReadInt64(),
                trackId = r.ReadString(),
                trackPackId = r.ReadString(),
                trackPackVersion = r.ReadString(),
                trackVersion = r.ReadInt32(),
                variant = r.ReadString(),
                kartId = r.ReadString(),
                kartVersion = r.ReadInt32(),
                characterId = r.ReadString(),
                engineClass = r.ReadInt32(),
                engineClassLabel = r.ReadString(),
                laps = r.ReadInt32(),
                tickSeconds = r.ReadSingle(),
                speedScale = r.ReadSingle(),
                finishTime = r.ReadSingle(),
            };
            var laps = r.ReadInt32();
            if (laps < 0 || laps > MaxLaps) throw new InvalidDataException("bad lap count");
            h.lapTimes = new float[laps];
            for (var i = 0; i < laps; i++) h.lapTimes[i] = r.ReadSingle();
            h.frameCount = r.ReadInt32();
            if (h.tickSeconds <= 0f || h.frameCount < 0) throw new InvalidDataException("bad tick data");
            if (r.BaseStream.Position < r.BaseStream.Length) ReadCosmetics(r, h.cosmetics);
            return h;
        }

        // A count byte, then each id and its colour as four bytes (r, g, b, a). Only content ids are written, and nothing
        // at all without cosmetics, so the file stays what servers and games before cosmetics accept.
        static void WriteCosmetics(BinaryWriter w, List<CosmeticChoice> cosmetics)
        {
            var worn = new List<CosmeticChoice>(CosmeticWire.MaxCosmetics);
            if (cosmetics != null)
                foreach (var c in cosmetics)
                    if (worn.Count < CosmeticWire.MaxCosmetics && ContentManifests.IsContentId(c.id)) worn.Add(c);
            if (worn.Count == 0) return;
            w.Write((byte)worn.Count);
            foreach (var c in worn)
            {
                w.Write(c.id);
                w.Write(c.color.r);
                w.Write(c.color.g);
                w.Write(c.color.b);
                w.Write(c.color.a);
            }
        }

        static void ReadCosmetics(BinaryReader r, List<CosmeticChoice> cosmetics)
        {
            var count = r.ReadByte();
            if (count > CosmeticWire.MaxCosmetics) throw new InvalidDataException("bad cosmetic count");
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadString();
                var color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                if (ContentManifests.IsContentId(id)) cosmetics.Add(new CosmeticChoice(id, color));
            }
        }
    }
}
