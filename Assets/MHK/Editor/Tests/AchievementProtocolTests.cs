using System;
using System.Collections.Generic;
using MHK.Online;
using NUnit.Framework;

namespace MHK.Tests
{
    /// <summary>The achievement messages against the bytes the Go codec writes (internal/proto achievements_test.go) and round trips.</summary>
    public class AchievementProtocolTests
    {
        // TestAchievementBytes: counters c=1, maxima m=2, sets s={i}, unlocked u at 3 ms.
        const string SyncHex = "0101630100000001016d020000000101730101690101750300000000000000";

        [Test]
        public void SyncRequestMatchesGo()
        {
            var m = new SyncAchievementsRequest();
            m.counters.Add(("c", 1));
            m.maxima.Add(("m", 2));
            m.sets.Add(("s", new List<string> { "i" }));
            m.unlocked.Add(("u", DateTimeOffset.FromUnixTimeMilliseconds(3)));
            Assert.AreEqual(SyncHex, Hex(m.Write));
            var back = SyncAchievementsRequest.Read(Reader(SyncHex));
            Assert.AreEqual(("c", 1u), back.counters[0]);
            Assert.AreEqual(("m", 2u), back.maxima[0]);
            Assert.AreEqual("i", back.sets[0].items[0]);
            Assert.AreEqual(3, back.unlocked[0].at.ToUnixTimeMilliseconds());
        }

        [Test]
        public void TotalsAndSharesRoundTrip()
        {
            var totals = new AchievementTotals();
            totals.counters.Add(("distance.m", 1UL << 40));
            totals.sets.Add(("cups.perfect", new List<string>()));
            totals.unlocked.Add(("a", DateTimeOffset.FromUnixTimeMilliseconds(1000)));
            var t = AchievementTotals.Read(Reader(Hex(totals.Write)));
            Assert.AreEqual(1UL << 40, t.counters[0].value);
            Assert.AreEqual(0, t.maxima.Count);
            Assert.AreEqual("cups.perfect", t.sets[0].stat);
            Assert.AreEqual("a", t.unlocked[0].id);

            var shares = new AchievementShareCounts { players = 1234 };
            shares.shares.Add(("game.first_race", 99));
            var s = AchievementShareCounts.Read(Reader(Hex(shares.Write)));
            Assert.AreEqual(1234u, s.players);
            Assert.AreEqual(("game.first_race", 99u), s.shares[0]);

            var request = new AchievementSharesRequest();
            request.ids.Add("x");
            Assert.AreEqual("010178", Hex(request.Write));
        }

        [Test]
        public void LimitsAreChecked()
        {
            var m = new SyncAchievementsRequest();
            for (var i = 0; i <= SyncAchievementsRequest.MaxStats; i++) m.counters.Add(("c", 1));
            Assert.Throws<ArgumentException>(() => Hex(m.Write));
            // 257 counters announced: over the limit before the list is read.
            Assert.Throws<WireFormatException>(() => SyncAchievementsRequest.Read(Reader("8102")));
        }

        static string Hex(Action<WireWriter> write)
        {
            var w = new WireWriter();
            write(w);
            return BitConverter.ToString(w.ToArray()).Replace("-", "").ToLowerInvariant();
        }

        static WireReader Reader(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return new WireReader(bytes);
        }
    }
}
