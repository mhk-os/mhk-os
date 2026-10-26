using System;
using System.Collections.Generic;
using UnityEngine;

namespace MHK.Online
{
    /// <summary>
    /// Message numbers and limits of the race messages (docs/protocol.md, "Race messages (TCP)" and "Realtime UDP").
    /// Push 523 RaceResults belongs to the lobby messages.
    /// </summary>
    public static class RaceProtocol
    {
        // Pushes (server to client).
        public const ushort RaceSetup = 512, RaceGo = 513, ItemGranted = 514, ItemBoxTaken = 515, ItemDenied = 516,
            ItemUsed = 517, ItemUseRejected = 518, ProjectileCorrect = 519, HitApplied = 520, HitForce = 521,
            PlayerFinished = 522, RaceResults = 523, PlayerLeftRace = 524, Eliminated = 525, BridgeTriggered = 526;

        // Notifies (client to server, frame kind 3).
        public const ushort RaceLoaded = 528, ItemPickup = 529, ItemUse = 530, HitTaken = 531, HitSeen = 532,
            Finished = 533, CheatReport = 534, UnknownContent = 535, EliminatedSelf = 536, BridgeTrigger = 537,
            ProjectileGone = 538;

        // UDP datagram types.
        public const byte UdpPing = 1, UdpPong = 2, UdpState = 3, UdpWorld = 4;

        public const int MaxRacers = 10, MaxItemBoxes = 512, MaxPool = 128, MaxEffects = 1024, MaxLaps = 99,
            MaxContentId = 96, MaxUsername = 20, MaxCountry = 2, MaxHash = 32, MaxMessage = 256, MaxOdds = 64,
            UdpKeySize = 32, PoolWeights = 11;

        /// <summary>Largest datagram either side sends or accepts.</summary>
        public const int UdpMaxDatagram = 1200;
        /// <summary>type u8, token u64, seq u32.</summary>
        public const int UdpHeaderSize = 13;
        public const int UdpMacSize = 8;

        /// <summary>Race ticks per second: the fixed physics rate, counted from GO.</summary>
        public const int TickRate = 60;

        /// <summary>A slot field's "none" (u8 255 on the wire, -1 here).</summary>
        public const int NoSlot = -1;
    }

    /// <summary>A server-to-client race push (TCP): every one names its race.</summary>
    public interface IRacePush
    {
        /// <summary>The push type (<see cref="RaceProtocol"/>).</summary>
        ushort Type { get; }
        long RaceId { get; }
    }

    /// <summary>A client-to-server race notify (TCP), sent with <see cref="OnlineRace.Notify"/>.</summary>
    public interface IRaceNotify
    {
        /// <summary>The notify type (<see cref="RaceProtocol"/>).</summary>
        ushort Type { get; }
        void Write(WireWriter w);
    }

    /// <summary>Field encodings shared by the race messages.</summary>
    public static class RaceWire
    {
        public static void Vec3(WireWriter w, Vector3 v)
        {
            w.F32(v.x);
            w.F32(v.y);
            w.F32(v.z);
        }

        public static Vector3 Vec3(WireReader r) => new(r.F32(), r.F32(), r.F32());

        public static void Quat(WireWriter w, Quaternion q)
        {
            w.F32(q.x);
            w.F32(q.y);
            w.F32(q.z);
            w.F32(q.w);
        }

        public static Quaternion Quat(WireReader r) => new(r.F32(), r.F32(), r.F32(), r.F32());

        /// <summary>A slot (0-254) or <see cref="RaceProtocol.NoSlot"/> as u8 (255 = none).</summary>
        public static void Slot(WireWriter w, int slot) => w.U8(slot < 0 || slot > 254 ? (byte)255 : (byte)slot);

        public static int Slot(WireReader r)
        {
            var v = r.U8();
            return v == 255 ? RaceProtocol.NoSlot : v;
        }

        public static void U8(WireWriter w, int v) => w.U8((byte)Mathf.Clamp(v, 0, 255));

        /// <summary>A u32 tick or id kept as int here (values stay far below 2^31).</summary>
        public static void U32(WireWriter w, int v) => w.U32(unchecked((uint)v));

        public static int U32(WireReader r) => unchecked((int)r.U32());

        public static void LapMs(WireWriter w, List<int> laps)
        {
            var count = laps?.Count ?? 0;
            w.ListCount(count, RaceProtocol.MaxLaps);
            for (var i = 0; i < count; i++) w.I32(laps[i]);
        }

        public static List<int> LapMs(WireReader r)
        {
            var count = r.ListCount(RaceProtocol.MaxLaps, 4);
            var list = new List<int>(count);
            for (var i = 0; i < count; i++) list.Add(r.I32());
            return list;
        }

        public static void Odds(WireWriter w, List<RaceOdds> odds)
        {
            var count = odds?.Count ?? 0;
            w.ListCount(count, RaceProtocol.MaxOdds);
            for (var i = 0; i < count; i++)
            {
                w.String(odds[i].powerupId, RaceProtocol.MaxContentId);
                w.F32(odds[i].percent);
            }
        }

        public static List<RaceOdds> Odds(WireReader r)
        {
            var count = r.ListCount(RaceProtocol.MaxOdds, 5);
            var list = new List<RaceOdds>(count);
            for (var i = 0; i < count; i++) list.Add(new RaceOdds { powerupId = r.String(RaceProtocol.MaxContentId), percent = r.F32() });
            return list;
        }
    }

    /// <summary>One item odd of a race (powerup id, percent).</summary>
    public struct RaceOdds
    {
        public string powerupId;
        public float percent;
    }

    /// <summary>One racer of <see cref="RaceSetup.grid"/>.</summary>
    public sealed class RaceSlot
    {
        public int slot;
        public long accountId;
        public string username = "";
        public string country = "";
        public string characterId = "";
        public string kartId = "";
        /// <summary>The racer's device: 0 unknown, 1 Windows, 2 macOS, 3 Linux, 4 Android, 5 iOS.</summary>
        public int platform;

        public void Write(WireWriter w)
        {
            RaceWire.U8(w, slot);
            w.I64(accountId);
            w.String(username, RaceProtocol.MaxUsername);
            w.String(country, RaceProtocol.MaxCountry);
            w.String(characterId, RaceProtocol.MaxContentId);
            w.String(kartId, RaceProtocol.MaxContentId);
            RaceWire.U8(w, platform);
        }

        public static RaceSlot Read(WireReader r) => new()
        {
            slot = r.U8(), accountId = r.I64(), username = r.String(RaceProtocol.MaxUsername), country = r.String(RaceProtocol.MaxCountry),
            characterId = r.String(RaceProtocol.MaxContentId), kartId = r.String(RaceProtocol.MaxContentId), platform = r.U8(),
        };
    }

    /// <summary>Push 512: a race starts loading (personalised: <see cref="yourSlot"/>, the UDP seat).</summary>
    public sealed class RaceSetup : IRacePush
    {
        public long raceId;
        public long lobbyId;
        public string trackId = "";
        /// <summary>0 race, 1 battle.</summary>
        public int mode;
        public int engineClass;
        /// <summary>0 = the track's default.</summary>
        public int laps;
        public bool items;
        public List<RaceOdds> itemOdds = new();
        public bool anticheat;
        /// <summary>Exactly one human: race against <see cref="aiCount"/> local AI, nothing is arbitrated.</summary>
        public bool solo;
        public int aiCount;
        public ulong seed;
        /// <summary>Start order: grid position = list index.</summary>
        public List<RaceSlot> grid = new();
        public int yourSlot;
        public int udpPort;
        public ulong udpToken;
        public byte[] udpKey = Array.Empty<byte>();
        public int setRace;
        public int setSize;

        public ushort Type => RaceProtocol.RaceSetup;
        public long RaceId => raceId;
        public bool Battle => mode == 1;

        /// <summary>The grid entry of <paramref name="slotNumber"/>, null when none.</summary>
        public RaceSlot Slot(int slotNumber)
        {
            foreach (var s in grid)
                if (s.slot == slotNumber) return s;
            return null;
        }

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.I64(lobbyId);
            w.String(trackId, RaceProtocol.MaxContentId);
            RaceWire.U8(w, mode);
            RaceWire.U8(w, engineClass);
            RaceWire.U8(w, laps);
            w.Bool(items);
            RaceWire.Odds(w, itemOdds);
            w.Bool(anticheat);
            w.Bool(solo);
            RaceWire.U8(w, aiCount);
            w.U64(seed);
            w.ListCount(grid.Count, RaceProtocol.MaxRacers);
            foreach (var s in grid) s.Write(w);
            RaceWire.U8(w, yourSlot);
            w.U16((ushort)udpPort);
            w.U64(udpToken);
            w.Bytes(udpKey, RaceProtocol.UdpKeySize);
            RaceWire.U8(w, setRace);
            RaceWire.U8(w, setSize);
        }

        public static RaceSetup Read(WireReader r)
        {
            var m = new RaceSetup
            {
                raceId = r.I64(), lobbyId = r.I64(), trackId = r.String(RaceProtocol.MaxContentId), mode = r.U8(), engineClass = r.U8(),
                laps = r.U8(), items = r.Bool(), itemOdds = RaceWire.Odds(r), anticheat = r.Bool(), solo = r.Bool(), aiCount = r.U8(),
                seed = r.U64(),
            };
            var count = r.ListCount(RaceProtocol.MaxRacers, 14);
            for (var i = 0; i < count; i++) m.grid.Add(RaceSlot.Read(r));
            m.yourSlot = r.U8();
            m.udpPort = r.U16();
            m.udpToken = r.U64();
            m.udpKey = r.Bytes(RaceProtocol.UdpKeySize);
            m.setRace = r.U8();
            m.setSize = r.U8();
            return m;
        }
    }

    /// <summary>Push 513: everyone loaded; GO is at <see cref="go"/> on the server clock (the countdown shows the 3 s before).</summary>
    public sealed class RaceGo : IRacePush
    {
        public long raceId;
        /// <summary>Unix milliseconds, server clock.</summary>
        public long goMs;

        public ushort Type => RaceProtocol.RaceGo;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.I64(goMs);
        }

        public static RaceGo Read(WireReader r) => new() { raceId = r.I64(), goMs = r.Time().ToUnixTimeMilliseconds() };
    }

    /// <summary>Push 514 (only to the picker): the box gave this powerup.</summary>
    public sealed class ItemGranted : IRacePush
    {
        public long raceId;
        public int boxId;
        public string powerupId = "";
        public int charges;
        public int tick;

        public ushort Type => RaceProtocol.ItemGranted;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.U16((ushort)boxId);
            w.String(powerupId, RaceProtocol.MaxContentId);
            RaceWire.U8(w, charges);
            RaceWire.U32(w, tick);
        }

        public static ItemGranted Read(WireReader r) => new()
        {
            raceId = r.I64(), boxId = r.U16(), powerupId = r.String(RaceProtocol.MaxContentId), charges = r.U8(), tick = RaceWire.U32(r),
        };
    }

    /// <summary>Push 515 (to everyone else): a racer took a box.</summary>
    public sealed class ItemBoxTaken : IRacePush
    {
        public long raceId;
        public int slot;
        public int boxId;
        public int tick;

        public ushort Type => RaceProtocol.ItemBoxTaken;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, slot);
            w.U16((ushort)boxId);
            RaceWire.U32(w, tick);
        }

        public static ItemBoxTaken Read(WireReader r) => new() { raceId = r.I64(), slot = RaceWire.Slot(r), boxId = r.U16(), tick = RaceWire.U32(r) };
    }

    /// <summary>Push 516: an <see cref="ItemPickup"/> was refused.</summary>
    public sealed class ItemDenied : IRacePush
    {
        public const int NotAvailable = 1, TooFar = 2, CannotReceive = 3;

        public long raceId;
        public int boxId;
        public int reason;

        public ushort Type => RaceProtocol.ItemDenied;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.U16((ushort)boxId);
            RaceWire.U8(w, reason);
        }

        public static ItemDenied Read(WireReader r) => new() { raceId = r.I64(), boxId = r.U16(), reason = r.U8() };
    }

    /// <summary>Push 517 (to everyone else): a racer used an item.</summary>
    public sealed class ItemUsed : IRacePush
    {
        public long raceId;
        public int slot;
        public int useId;
        public string powerupId = "";
        public int tick;
        public Vector3 pos;
        public Quaternion rot = Quaternion.identity;
        public bool backwards;
        public int targetSlot = RaceProtocol.NoSlot;

        public ushort Type => RaceProtocol.ItemUsed;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, slot);
            RaceWire.U32(w, useId);
            w.String(powerupId, RaceProtocol.MaxContentId);
            RaceWire.U32(w, tick);
            RaceWire.Vec3(w, pos);
            RaceWire.Quat(w, rot);
            w.Bool(backwards);
            RaceWire.Slot(w, targetSlot);
        }

        public static ItemUsed Read(WireReader r) => new()
        {
            raceId = r.I64(), slot = RaceWire.Slot(r), useId = RaceWire.U32(r), powerupId = r.String(RaceProtocol.MaxContentId),
            tick = RaceWire.U32(r), pos = RaceWire.Vec3(r), rot = RaceWire.Quat(r), backwards = r.Bool(), targetSlot = RaceWire.Slot(r),
        };
    }

    /// <summary>Push 518: an <see cref="ItemUse"/> was refused.</summary>
    public sealed class ItemUseRejected : IRacePush
    {
        public const int NotHeld = 1, Cooldown = 2;

        public long raceId;
        public int useId;
        public int reason;

        public ushort Type => RaceProtocol.ItemUseRejected;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.U32(w, useId);
            RaceWire.U8(w, reason);
        }

        public static ItemUseRejected Read(WireReader r) => new() { raceId = r.I64(), useId = RaceWire.U32(r), reason = r.U8() };
    }

    /// <summary>Push 519: the median state of a projectile, to blend towards.</summary>
    public sealed class ProjectileCorrect : IRacePush
    {
        public long raceId;
        public int useId;
        public int tick;
        public Vector3 pos;
        public Vector3 vel;

        public ushort Type => RaceProtocol.ProjectileCorrect;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.U32(w, useId);
            RaceWire.U32(w, tick);
            RaceWire.Vec3(w, pos);
            RaceWire.Vec3(w, vel);
        }

        public static ProjectileCorrect Read(WireReader r) => new()
        {
            raceId = r.I64(), useId = RaceWire.U32(r), tick = RaceWire.U32(r), pos = RaceWire.Vec3(r), vel = RaceWire.Vec3(r),
        };
    }

    /// <summary>Push 520 (to everyone but the victim): a hit the victim confirmed.</summary>
    public sealed class HitApplied : IRacePush
    {
        public long raceId;
        public int victimSlot;
        public int attackerSlot = RaceProtocol.NoSlot;
        public int useId;
        public string effectId = "";
        public int tick;

        public ushort Type => RaceProtocol.HitApplied;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, victimSlot);
            RaceWire.Slot(w, attackerSlot);
            RaceWire.U32(w, useId);
            w.String(effectId, RaceProtocol.MaxContentId);
            RaceWire.U32(w, tick);
        }

        public static HitApplied Read(WireReader r) => new()
        {
            raceId = r.I64(), victimSlot = RaceWire.Slot(r), attackerSlot = RaceWire.Slot(r), useId = RaceWire.U32(r),
            effectId = r.String(RaceProtocol.MaxContentId), tick = RaceWire.U32(r),
        };
    }

    /// <summary>Push 521 (to the victim): observers saw this hit; apply it and confirm with <see cref="HitTaken"/>.</summary>
    public sealed class HitForce : IRacePush
    {
        public long raceId;
        public int attackerSlot;
        public int useId;
        public string effectId = "";

        public ushort Type => RaceProtocol.HitForce;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, attackerSlot);
            RaceWire.U32(w, useId);
            w.String(effectId, RaceProtocol.MaxContentId);
        }

        public static HitForce Read(WireReader r) => new()
        {
            raceId = r.I64(), attackerSlot = RaceWire.Slot(r), useId = RaceWire.U32(r), effectId = r.String(RaceProtocol.MaxContentId),
        };
    }

    /// <summary>Push 522: a racer finished (the server's accepted time).</summary>
    public sealed class PlayerFinished : IRacePush
    {
        public long raceId;
        public int slot;
        public int finishMs;
        public List<int> lapMs = new();

        public ushort Type => RaceProtocol.PlayerFinished;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, slot);
            w.I32(finishMs);
            RaceWire.LapMs(w, lapMs);
        }

        public static PlayerFinished Read(WireReader r) => new() { raceId = r.I64(), slot = RaceWire.Slot(r), finishMs = r.I32(), lapMs = RaceWire.LapMs(r) };
    }

    /// <summary>Push 524: a racer left the race (<see cref="outcome"/> as RaceResultRow.outcome).</summary>
    public sealed class PlayerLeftRace : IRacePush
    {
        public const int Left = 2, KickedCheating = 3, KickedModerator = 4, Desync = 5;

        public long raceId;
        public int slot;
        public int outcome;
        public string message = "";

        public ushort Type => RaceProtocol.PlayerLeftRace;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, slot);
            RaceWire.U8(w, outcome);
            w.String(message, RaceProtocol.MaxMessage);
        }

        public static PlayerLeftRace Read(WireReader r) => new() { raceId = r.I64(), slot = RaceWire.Slot(r), outcome = r.U8(), message = r.String(RaceProtocol.MaxMessage) };
    }

    /// <summary>Push 525 (battle): a racer is out.</summary>
    public sealed class Eliminated : IRacePush
    {
        public long raceId;
        public int slot;
        public int attackerSlot = RaceProtocol.NoSlot;
        public int tick;

        public ushort Type => RaceProtocol.Eliminated;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, slot);
            RaceWire.Slot(w, attackerSlot);
            RaceWire.U32(w, tick);
        }

        public static Eliminated Read(WireReader r) => new() { raceId = r.I64(), slot = RaceWire.Slot(r), attackerSlot = RaceWire.Slot(r), tick = RaceWire.U32(r) };
    }

    /// <summary>Push 526: the winning trigger of a collapsing bridge.</summary>
    public sealed class BridgeTriggered : IRacePush
    {
        public long raceId;
        public int bridgeId;
        public int tick;

        public ushort Type => RaceProtocol.BridgeTriggered;
        public long RaceId => raceId;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.U16((ushort)bridgeId);
            RaceWire.U32(w, tick);
        }

        public static BridgeTriggered Read(WireReader r) => new() { raceId = r.I64(), bridgeId = r.U16(), tick = RaceWire.U32(r) };
    }

    /// <summary>One item box of the loaded track; its box id is its index in <see cref="RaceLoaded.boxes"/>.</summary>
    public sealed class ItemBoxInfo
    {
        public Vector3 pos;
        /// <summary>Respawn time in 0.1 s.</summary>
        public int respawnTenths;
        /// <summary>"" random, a powerup id, "@special", or a status effect id (not arbitrated).</summary>
        public string fixedContent = "";

        public void Write(WireWriter w)
        {
            RaceWire.Vec3(w, pos);
            w.U16((ushort)Mathf.Clamp(respawnTenths, 0, ushort.MaxValue));
            w.String(fixedContent, RaceProtocol.MaxContentId);
        }

        public static ItemBoxInfo Read(WireReader r) => new() { pos = RaceWire.Vec3(r), respawnTenths = r.U16(), fixedContent = r.String(RaceProtocol.MaxContentId) };
    }

    /// <summary>One powerup of the item pool with its weights at position fractions 0, 0.1, ... 1 (0 = leader).</summary>
    public sealed class PoolEntry
    {
        public string powerupId = "";
        public int charges;
        public int battleCharges;
        public readonly float[] weights = new float[RaceProtocol.PoolWeights];

        public void Write(WireWriter w)
        {
            w.String(powerupId, RaceProtocol.MaxContentId);
            RaceWire.U8(w, charges);
            RaceWire.U8(w, battleCharges);
            for (var i = 0; i < weights.Length; i++) w.F32(weights[i]);
        }

        public static PoolEntry Read(WireReader r)
        {
            var e = new PoolEntry { powerupId = r.String(RaceProtocol.MaxContentId), charges = r.U8(), battleCharges = r.U8() };
            for (var i = 0; i < e.weights.Length; i++) e.weights[i] = r.F32();
            return e;
        }
    }

    /// <summary>A slot's special powerup (<see cref="RaceLoaded.specials"/>).</summary>
    public struct SpecialEntry
    {
        public int slot;
        public string powerupId;
    }

    /// <summary>Item rules of this game version.</summary>
    public struct ItemRules
    {
        /// <summary>A new item replaces the held one (else a racer holding one cannot receive).</summary>
        public bool replacesHeld;
        public int rouletteMs;
    }

    /// <summary>Notify 528: the race's content as this client loaded it; the server takes the racers' majority.</summary>
    public sealed class RaceLoaded : IRaceNotify
    {
        public long raceId;
        public string trackHash = "";
        /// <summary>Laps of this race (the track's default resolved).</summary>
        public int laps;
        public List<ItemBoxInfo> boxes = new();
        public List<PoolEntry> pool = new();
        public List<SpecialEntry> specials = new();
        public ItemRules rules;
        /// <summary>Sorted status effect ids: the index table of <see cref="KartState.effects"/>.</summary>
        public List<string> effects = new();

        public ushort Type => RaceProtocol.RaceLoaded;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.String(trackHash, RaceProtocol.MaxHash);
            RaceWire.U8(w, laps);
            w.ListCount(boxes.Count, RaceProtocol.MaxItemBoxes);
            foreach (var b in boxes) b.Write(w);
            w.ListCount(pool.Count, RaceProtocol.MaxPool);
            foreach (var p in pool) p.Write(w);
            w.ListCount(specials.Count, RaceProtocol.MaxRacers);
            foreach (var s in specials)
            {
                RaceWire.U8(w, s.slot);
                w.String(s.powerupId, RaceProtocol.MaxContentId);
            }
            w.Bool(rules.replacesHeld);
            w.U16((ushort)Mathf.Clamp(rules.rouletteMs, 0, ushort.MaxValue));
            w.ListCount(effects.Count, RaceProtocol.MaxEffects);
            foreach (var e in effects) w.String(e, RaceProtocol.MaxContentId);
        }

        public static RaceLoaded Read(WireReader r)
        {
            var m = new RaceLoaded { raceId = r.I64(), trackHash = r.String(RaceProtocol.MaxHash), laps = r.U8() };
            var n = r.ListCount(RaceProtocol.MaxItemBoxes, 15);
            for (var i = 0; i < n; i++) m.boxes.Add(ItemBoxInfo.Read(r));
            n = r.ListCount(RaceProtocol.MaxPool, 3 + 4 * RaceProtocol.PoolWeights);
            for (var i = 0; i < n; i++) m.pool.Add(PoolEntry.Read(r));
            n = r.ListCount(RaceProtocol.MaxRacers, 2);
            for (var i = 0; i < n; i++) m.specials.Add(new SpecialEntry { slot = r.U8(), powerupId = r.String(RaceProtocol.MaxContentId) });
            m.rules = new ItemRules { replacesHeld = r.Bool(), rouletteMs = r.U16() };
            n = r.ListCount(RaceProtocol.MaxEffects, 1);
            for (var i = 0; i < n; i++) m.effects.Add(r.String(RaceProtocol.MaxContentId));
            return m;
        }
    }

    /// <summary>Notify 529: the local kart touched an arbitrated item box.</summary>
    public sealed class ItemPickup : IRaceNotify
    {
        public long raceId;
        public int boxId;
        public int tick;
        public Vector3 pos;

        public ushort Type => RaceProtocol.ItemPickup;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.U16((ushort)boxId);
            RaceWire.U32(w, tick);
            RaceWire.Vec3(w, pos);
        }

        public static ItemPickup Read(WireReader r) => new() { raceId = r.I64(), boxId = r.U16(), tick = RaceWire.U32(r), pos = RaceWire.Vec3(r) };
    }

    /// <summary>Notify 530: the local racer used its item (<see cref="useId"/> = (slot + 1) &lt;&lt; 24 | n).</summary>
    public sealed class ItemUse : IRaceNotify
    {
        public long raceId;
        public int useId;
        public int tick;
        public Vector3 pos;
        public Quaternion rot = Quaternion.identity;
        public bool backwards;
        public int targetSlot = RaceProtocol.NoSlot;

        public ushort Type => RaceProtocol.ItemUse;

        public static int UseId(int slot, int n) => (slot + 1) << 24 | (n & 0xFFFFFF);

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.U32(w, useId);
            RaceWire.U32(w, tick);
            RaceWire.Vec3(w, pos);
            RaceWire.Quat(w, rot);
            w.Bool(backwards);
            RaceWire.Slot(w, targetSlot);
        }

        public static ItemUse Read(WireReader r) => new()
        {
            raceId = r.I64(), useId = RaceWire.U32(r), tick = RaceWire.U32(r), pos = RaceWire.Vec3(r), rot = RaceWire.Quat(r),
            backwards = r.Bool(), targetSlot = RaceWire.Slot(r),
        };
    }

    /// <summary>Notify 531: the local kart took a hit (useId 0 = track hazard or zone).</summary>
    public sealed class HitTaken : IRaceNotify
    {
        public long raceId;
        public int attackerSlot = RaceProtocol.NoSlot;
        public int useId;
        public string effectId = "";
        public int tick;

        public ushort Type => RaceProtocol.HitTaken;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, attackerSlot);
            RaceWire.U32(w, useId);
            w.String(effectId, RaceProtocol.MaxContentId);
            RaceWire.U32(w, tick);
        }

        public static HitTaken Read(WireReader r) => new()
        {
            raceId = r.I64(), attackerSlot = RaceWire.Slot(r), useId = RaceWire.U32(r), effectId = r.String(RaceProtocol.MaxContentId), tick = RaceWire.U32(r),
        };
    }

    /// <summary>Notify 532: this client saw a projectile hit another kart (not applied locally).</summary>
    public sealed class HitSeen : IRaceNotify
    {
        public long raceId;
        public int victimSlot;
        public int attackerSlot = RaceProtocol.NoSlot;
        public int useId;
        public string effectId = "";
        public int tick;

        public ushort Type => RaceProtocol.HitSeen;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, victimSlot);
            RaceWire.Slot(w, attackerSlot);
            RaceWire.U32(w, useId);
            w.String(effectId, RaceProtocol.MaxContentId);
            RaceWire.U32(w, tick);
        }

        public static HitSeen Read(WireReader r) => new()
        {
            raceId = r.I64(), victimSlot = RaceWire.Slot(r), attackerSlot = RaceWire.Slot(r), useId = RaceWire.U32(r),
            effectId = r.String(RaceProtocol.MaxContentId), tick = RaceWire.U32(r),
        };
    }

    /// <summary>Notify 533: the local racer crossed the finish.</summary>
    public sealed class Finished : IRaceNotify
    {
        public long raceId;
        public int finishMs;
        public List<int> lapMs = new();
        public int tick;
        /// <summary>Solo races: the place among all karts incl. AI; 0 otherwise.</summary>
        public int position;

        public ushort Type => RaceProtocol.Finished;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.I32(finishMs);
            RaceWire.LapMs(w, lapMs);
            RaceWire.U32(w, tick);
            RaceWire.U8(w, position);
        }

        public static Finished Read(WireReader r) => new() { raceId = r.I64(), finishMs = r.I32(), lapMs = RaceWire.LapMs(r), tick = RaceWire.U32(r), position = r.U8() };
    }

    /// <summary>Notify 534: this client's plausibility checks flagged another racer.</summary>
    public sealed class CheatReport : IRaceNotify
    {
        public long raceId;
        public int suspectSlot;
        public FlagReason reason;
        public int tick;
        public string detail = "";

        public ushort Type => RaceProtocol.CheatReport;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, suspectSlot);
            w.U8((byte)reason);
            RaceWire.U32(w, tick);
            w.String(detail, RaceProtocol.MaxMessage);
        }

        public static CheatReport Read(WireReader r) => new()
        {
            raceId = r.I64(), suspectSlot = RaceWire.Slot(r), reason = (FlagReason)r.U8(), tick = RaceWire.U32(r), detail = r.String(RaceProtocol.MaxMessage),
        };
    }

    /// <summary>Notify 535: a racer uses content this client does not have (kind as in the pack manifests).</summary>
    public sealed class UnknownContent : IRaceNotify
    {
        public long raceId;
        public int slot;
        public int kind;
        public string id = "";

        public ushort Type => RaceProtocol.UnknownContent;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, slot);
            RaceWire.U8(w, kind);
            w.String(id, RaceProtocol.MaxContentId);
        }

        public static UnknownContent Read(WireReader r) => new() { raceId = r.I64(), slot = RaceWire.Slot(r), kind = r.U8(), id = r.String(RaceProtocol.MaxContentId) };
    }

    /// <summary>Notify 536 (battle): the local racer is out.</summary>
    public sealed class EliminatedSelf : IRaceNotify
    {
        public long raceId;
        public int attackerSlot = RaceProtocol.NoSlot;
        public int tick;

        public ushort Type => RaceProtocol.EliminatedSelf;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.Slot(w, attackerSlot);
            RaceWire.U32(w, tick);
        }

        public static EliminatedSelf Read(WireReader r) => new() { raceId = r.I64(), attackerSlot = RaceWire.Slot(r), tick = RaceWire.U32(r) };
    }

    /// <summary>Notify 537: the local kart triggered a collapsing bridge.</summary>
    public sealed class BridgeTrigger : IRaceNotify
    {
        public long raceId;
        public int bridgeId;
        public int tick;

        public ushort Type => RaceProtocol.BridgeTrigger;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            w.U16((ushort)bridgeId);
            RaceWire.U32(w, tick);
        }

        public static BridgeTrigger Read(WireReader r) => new() { raceId = r.I64(), bridgeId = r.U16(), tick = RaceWire.U32(r) };
    }

    /// <summary>Notify 538: a projectile ended (owner, or a reporter that saw it end).</summary>
    public sealed class ProjectileGone : IRaceNotify
    {
        public long raceId;
        public int useId;
        public int tick;

        public ushort Type => RaceProtocol.ProjectileGone;

        public void Write(WireWriter w)
        {
            w.I64(raceId);
            RaceWire.U32(w, useId);
            RaceWire.U32(w, tick);
        }

        public static ProjectileGone Read(WireReader r) => new() { raceId = r.I64(), useId = RaceWire.U32(r), tick = RaceWire.U32(r) };
    }

    /// <summary><see cref="KartState.flags"/> bits.</summary>
    [Flags]
    public enum KartStateFlags : byte
    {
        None = 0,
        /// <summary>Teleported or respawned since the previous state: receivers snap instead of interpolating.</summary>
        Cut = 1 << 0,
        ControlsEnabled = 1 << 1,
        Finished = 1 << 2,
        Eliminated = 1 << 3,
        Intangible = 1 << 4,
        Airborne = 1 << 5,
        LookBack = 1 << 6,
    }

    /// <summary>A racer's own kart snapshot (UDP, 30 Hz): sent in State, received per other racer in World.</summary>
    public sealed class KartState
    {
        const float RotScale = 32767f;
        const int MaxEffectsPerState = 255;

        /// <summary>Race tick: physics steps since GO, 0 before GO.</summary>
        public int tick;
        public Vector3 pos;
        public Quaternion rot = Quaternion.identity;
        /// <summary>m/s (cm/s on the wire, clamped to ±327 m/s).</summary>
        public Vector3 vel;
        /// <summary><c>GhostInputKeys</c> bits.</summary>
        public byte keys;
        public KartStateFlags flags;
        /// <summary>Visual steer -1..1.</summary>
        public float steer;
        public int lap;
        /// <summary>Metres along the race (ranking).</summary>
        public float raceDistance;
        /// <summary>Battle health 0-255.</summary>
        public int health;
        /// <summary>Active status effects as indexes into <see cref="RaceLoaded.effects"/>.</summary>
        public readonly List<ushort> effects = new();

        public bool Has(KartStateFlags f) => (flags & f) != 0;

        public void Write(WireWriter w)
        {
            RaceWire.U32(w, tick);
            RaceWire.Vec3(w, Finite(pos));
            var q = rot;
            var norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (!(norm > 1e-6f) || float.IsInfinity(norm)) q = Quaternion.identity;
            else q = new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm);
            w.U16(unchecked((ushort)Quantize(q.x, RotScale)));
            w.U16(unchecked((ushort)Quantize(q.y, RotScale)));
            w.U16(unchecked((ushort)Quantize(q.z, RotScale)));
            w.U16(unchecked((ushort)Quantize(q.w, RotScale)));
            w.U16(unchecked((ushort)Quantize(vel.x, 100f)));
            w.U16(unchecked((ushort)Quantize(vel.y, 100f)));
            w.U16(unchecked((ushort)Quantize(vel.z, 100f)));
            w.U8(keys);
            w.U8((byte)flags);
            w.U8(unchecked((byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(steer * 127f), -127, 127)));
            RaceWire.U8(w, lap);
            w.F32(float.IsNaN(raceDistance) || float.IsInfinity(raceDistance) ? 0f : raceDistance);
            RaceWire.U8(w, health);
            var n = Math.Min(effects.Count, MaxEffectsPerState);
            w.U8((byte)n);
            for (var i = 0; i < n; i++) w.U16(effects[i]);
        }

        /// <summary>Reads into this instance (reusing <see cref="effects"/>).</summary>
        public void Read(WireReader r)
        {
            tick = RaceWire.U32(r);
            pos = RaceWire.Vec3(r);
            var x = (short)r.U16() / RotScale;
            var y = (short)r.U16() / RotScale;
            var z = (short)r.U16() / RotScale;
            var qw = (short)r.U16() / RotScale;
            var norm = Mathf.Sqrt(x * x + y * y + z * z + qw * qw);
            rot = norm > 1e-4f ? new Quaternion(x / norm, y / norm, z / norm, qw / norm) : Quaternion.identity;
            vel = new Vector3((short)r.U16() / 100f, (short)r.U16() / 100f, (short)r.U16() / 100f);
            keys = r.U8();
            flags = (KartStateFlags)r.U8();
            steer = (sbyte)r.U8() / 127f;
            lap = r.U8();
            raceDistance = r.F32();
            health = r.U8();
            var n = r.U8();
            if (r.Remaining < 2 * n) throw new WireFormatException("Truncated effects.");
            effects.Clear();
            for (var i = 0; i < n; i++) effects.Add(r.U16());
        }

        public void CopyFrom(KartState o)
        {
            tick = o.tick;
            pos = o.pos;
            rot = o.rot;
            vel = o.vel;
            keys = o.keys;
            flags = o.flags;
            steer = o.steer;
            lap = o.lap;
            raceDistance = o.raceDistance;
            health = o.health;
            effects.Clear();
            effects.AddRange(o.effects);
        }

        static short Quantize(float v, float scale)
        {
            if (float.IsNaN(v)) return 0;
            return (short)Mathf.Clamp(Mathf.RoundToInt(v * scale), -32767, 32767);
        }

        static Vector3 Finite(Vector3 v) =>
            float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z) ? Vector3.zero : v;
    }

    /// <summary>A client's view of a live projectile, piggybacked on State datagrams (each live projectile every 0.5 s).</summary>
    public struct ProjectileReport
    {
        public const int Size = 4 + 4 + 12 + 12;

        public int useId;
        public int tick;
        public Vector3 pos;
        public Vector3 vel;

        public void Write(WireWriter w)
        {
            RaceWire.U32(w, useId);
            RaceWire.U32(w, tick);
            RaceWire.Vec3(w, pos);
            RaceWire.Vec3(w, vel);
        }

        public static ProjectileReport Read(WireReader r) => new() { useId = RaceWire.U32(r), tick = RaceWire.U32(r), pos = RaceWire.Vec3(r), vel = RaceWire.Vec3(r) };
    }
}
