using System.IO;

namespace MHK.Ghosts
{
    /// <summary>Header block of a ghost file. Fields in a fixed order, newer format versions only append.</summary>
    static class GhostHeaderCodec
    {
        const int MaxLaps = 10000;

        public static void Write(BinaryWriter w, GhostHeader h)
        {
            w.Write(h.id ?? "");
            w.Write(h.gameBuild ?? "");
            w.Write(h.contentVersion);
            w.Write(h.playerName ?? "");
            w.Write(h.playerId ?? "");
            w.Write(h.dateUtcTicks);
            w.Write(h.trackId ?? "");
            w.Write(h.trackPackId ?? "");
            w.Write(h.trackPackVersion ?? "");
            w.Write(h.trackHash ?? "");
            w.Write(h.kartId ?? "");
            w.Write(h.characterId ?? "");
            w.Write(h.engineClass);
            w.Write(h.engineClassLabel ?? "");
            w.Write(h.laps);
            w.Write(h.setupHash ?? "");
            w.Write(h.tickSeconds);
            w.Write(h.speedScale);
            w.Write(h.finishTime);
            w.Write(h.lapTimes.Length);
            foreach (var t in h.lapTimes) w.Write(t);
            w.Write(h.frameCount);
        }

        /// <summary>Reads a header written by format <paramref name="version"/> (fields appended later get their defaults).</summary>
        public static GhostHeader Read(BinaryReader r, int version)
        {
            var h = new GhostHeader
            {
                id = r.ReadString(),
                gameBuild = r.ReadString(),
                contentVersion = r.ReadInt32(),
                playerName = r.ReadString(),
                playerId = r.ReadString(),
                dateUtcTicks = r.ReadInt64(),
                trackId = r.ReadString(),
                trackPackId = r.ReadString(),
                trackPackVersion = r.ReadString(),
                trackHash = r.ReadString(),
                kartId = r.ReadString(),
                characterId = r.ReadString(),
                engineClass = r.ReadInt32(),
                engineClassLabel = r.ReadString(),
                laps = r.ReadInt32(),
                setupHash = r.ReadString(),
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
            return h;
        }
    }
}
