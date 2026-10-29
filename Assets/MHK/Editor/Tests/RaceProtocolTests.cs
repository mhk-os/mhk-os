using System;
using System.Collections.Generic;
using MHK.Online;
using NUnit.Framework;
using UnityEngine;

namespace MHK.Tests
{
    /// <summary>
    /// The race messages of protocol 2 against the server: the byte vectors the Go codec pins in internal/proto
    /// (udp_test.go TestRaceMessageBytes, lobby_test.go TestRaceSlotBytes): AI slots, the AI host and the slot field of
    /// the notifies the AI host sends for AI karts.
    /// </summary>
    public class RaceProtocolTests
    {
        const string AiSlotHex = "00" + "0000000000000000" + "00" + "00" + "0163" + "016b" + "00" + "01";
        const string HumanSlotHex = "01" + "0900000000000000" + "0175" + "024445" + "0163" + "016b" + "02" + "00";

        [Test]
        public void ProtocolVersionIsTwo() => Assert.AreEqual(2, Protocol.Version);

        [Test]
        public void RaceSlotsMatchGo()
        {
            var human = new RaceSlot { slot = 3, accountId = 2, username = "u", country = "SK", characterId = "c", kartId = "k", platform = 5 };
            Assert.AreEqual("03" + "0200000000000000" + "0175" + "02534b" + "0163" + "016b" + "05" + "00", Hex(human.Write));
            var ai = RaceSlot.Read(Reader(AiSlotHex));
            Assert.IsTrue(ai.ai);
            Assert.AreEqual(0, ai.accountId);
            Assert.AreEqual(AiSlotHex, Hex(ai.Write));
        }

        [Test]
        public void RaceSetupMatchesGo()
        {
            var key = new byte[RaceProtocol.UdpKeySize];
            for (var i = 0; i < key.Length; i++) key[i] = 7;
            var setup = new RaceSetup
            {
                raceId = 1, lobbyId = 2, trackId = "t", mode = 0, engineClass = 2, laps = 3, items = true, anticheat = true, aiHost = 1, seed = 5,
                yourSlot = 1, udpPort = 7350, udpToken = 0x42, udpKey = key, setRace = 1, setSize = 4,
            };
            setup.grid.Add(new RaceSlot { slot = 0, characterId = "c", kartId = "k", ai = true });
            setup.grid.Add(new RaceSlot { slot = 1, accountId = 9, username = "u", country = "DE", characterId = "c", kartId = "k", platform = 2 });
            var hex = "0100000000000000" + "0200000000000000" + "0174" + "00" + "02" + "03" + "01" + "00" + "01" + "01" + "0500000000000000" + "02"
                      + AiSlotHex + HumanSlotHex + "01" + "b61c" + "4200000000000000" + "20" + Repeat("07", 32) + "01" + "04";
            Assert.AreEqual(hex, Hex(setup.Write));
            var back = RaceSetup.Read(Reader(hex));
            Assert.AreEqual(1, back.aiHost);
            Assert.IsTrue(back.grid[0].ai);
            Assert.AreEqual(hex, Hex(back.Write));
        }

        [Test]
        public void PushesMatchGo()
        {
            var granted = ItemGranted.Read(Reader("0100000000000000" + "0200" + "0170" + "03" + "04000000" + "05"));
            Assert.AreEqual(5, granted.slot);
            Assert.AreEqual("p", granted.powerupId);
            Assert.AreEqual(4, ItemDenied.Read(Reader("0100000000000000" + "0200" + "03" + "04")).slot);
            var host = AiHost.Read(Reader("0100000000000000" + "07"));
            Assert.AreEqual(7, host.slot);
            Assert.AreEqual(RaceProtocol.AiHost, host.Type);
        }

        [Test]
        public void NotifiesMatchGo()
        {
            Assert.AreEqual("0100000000000000" + "0200" + "03000000" + "0000803f" + "00000000" + "00000000" + "ff",
                Hex(new ItemPickup { raceId = 1, boxId = 2, tick = 3, pos = new Vector3(1, 0, 0) }.Write));
            Assert.AreEqual("0100000000000000" + "02000001" + "03000000" + Repeat("00", 12) + Repeat("00", 12) + "0000803f" + "01" + "ff" + "00",
                Hex(new ItemUse { raceId = 1, useId = ItemUse.UseId(0, 2), tick = 3, rot = new Quaternion(0, 0, 0, 1), backwards = true, slot = 0 }.Write));
            Assert.AreEqual("0100000000000000" + "02" + "01000003" + "0165" + "04000000" + "00",
                Hex(new HitTaken { raceId = 1, attackerSlot = 2, useId = ItemUse.UseId(2, 1), effectId = "e", tick = 4, slot = 0 }.Write));
            var finished = new Finished { raceId = 1, finishMs = 60000, tick = 3600, slot = 1 };
            finished.lapMs.Add(20000);
            Assert.AreEqual("0100000000000000" + "60ea0000" + "01" + "204e0000" + "100e0000" + "01", Hex(finished.Write));
            Assert.AreEqual("0100000000000000" + "ff" + "09000000" + "02", Hex(new EliminatedSelf { raceId = 1, tick = 9, slot = 2 }.Write));
        }

        [Test]
        public void AiProgressMatchesGo()
        {
            var entries = new List<AiProgress>
            {
                new() { slot = 0, lap = 2, raceDistance = 1234.5f, health = 100 },
                new() { slot = 3, lap = 1, raceDistance = 0.5f, health = 0 },
            };
            var w = new WireWriter();
            w.U8((byte)entries.Count);
            foreach (var e in entries) e.Write(w);
            Assert.AreEqual("02" + "00" + "02" + "00509a44" + "64" + "03" + "01" + "0000003f" + "00", ToHex(w.ToArray()));
            // One AiProgress datagram carries every AI kart of a full grid.
            var capacity = (RaceProtocol.UdpMaxDatagram - RaceProtocol.UdpHeaderSize - RaceProtocol.UdpMacSize - 1) / AiProgress.Size;
            Assert.GreaterOrEqual(capacity, RaceProtocol.MaxRacers - 1);
        }

        static string Repeat(string s, int n) => string.Concat(System.Linq.Enumerable.Repeat(s, n));

        static string Hex(Action<WireWriter> write)
        {
            var w = new WireWriter();
            write(w);
            return ToHex(w.ToArray());
        }

        static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        static WireReader Reader(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return new WireReader(bytes);
        }
    }
}
