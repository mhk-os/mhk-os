using System;
using MHK.Online;
using NUnit.Framework;

namespace MHK.Tests
{
    /// <summary>DeleteAccount (26), ChangeUsername (29), RunHiddenInfo (135) and the profiles' playtimes against the bytes the Go codec writes (internal/proto achievements_test.go, profiles_test.go) and round trips.</summary>
    public class AccountProtocolTests
    {
        // TestAchievementBytes: not hidden, moderator "M", reason "r", deleted by the owner.
        const string HiddenHex = "0000000000000000014d01720100";
        // TestPlaytimesBytes: the summary of account 1 "ab" (DE, moderator, created at 2 ms, never active, online, score 3,
        // rank 4) in a page with its playtime 3600 s, and in a ranking as the page and the own row (playtime 5 s).
        const string SummaryHex = "0100000000000000" + "026162" + "024445" + "01" + "0200000000000000" + "0000000000000000" + "01" + "03000000" + "04000000";
        const string PageHex = "01000000" + "01" + SummaryHex + "01" + "100e000000000000";
        const string RankingHex = "01000000" + "01" + SummaryHex + "01" + SummaryHex + "02" + "100e000000000000" + "0500000000000000";

        [Test]
        public void PlaytimesMatchGo()
        {
            var page = PlayerPage.Read(Reader(PageHex));
            Assert.AreEqual("ab", page.players[0].username);
            Assert.AreEqual(3600, page.players[0].playtime);
            Assert.AreEqual(PageHex, Hex(w => page.Write(w, false)));
            var ranking = PlayerPage.ReadRanked(Reader(RankingHex));
            Assert.AreEqual(3600, ranking.players[0].playtime);
            Assert.AreEqual(5, ranking.mine.playtime);
            Assert.AreEqual(RankingHex, Hex(w => ranking.Write(w, true)));
        }

        [Test]
        public void PlaytimesAreOptional()
        {
            // An older server's bodies end before the playtimes: unknown (-1).
            Assert.AreEqual(-1, PlayerPage.Read(Reader("01000000" + "01" + SummaryHex)).players[0].playtime);
            var ranked = PlayerPage.ReadRanked(Reader("01000000" + "01" + SummaryHex + "01" + SummaryHex));
            Assert.AreEqual(-1, ranked.players[0].playtime);
            Assert.AreEqual(-1, ranked.mine.playtime);
            var profile = Hex(new PlayerProfile { player = new PlayerSummary { accountId = 9, username = "x", playtime = 754 } }.Write);
            Assert.AreEqual(754, PlayerProfile.Read(Reader(profile)).player.playtime);
            Assert.AreEqual(-1, PlayerProfile.Read(Reader(profile.Substring(0, profile.Length - 18))).player.playtime);
            // A playtimes list of another length than the summaries is malformed.
            Assert.Throws<WireFormatException>(() => PlayerPage.Read(Reader("01000000" + "01" + SummaryHex + "00")));
            Assert.Throws<WireFormatException>(() => PlayerPage.ReadRanked(Reader(RankingHex.Substring(0, RankingHex.Length - 34) + "01" + "0500000000000000")));
        }

        [Test]
        public void PlaytimeText()
        {
            var texts = new MHK.UI.PlayerCardTexts();
            Assert.AreEqual("-", texts.Playtime(0));
            Assert.AreEqual("34 min", texts.Playtime(34 * 60 + 59));
            Assert.AreEqual("12 h 34 min", texts.Playtime(45_240));
            Assert.AreEqual("100 h 0 min", texts.Playtime(360_000));
        }

        [Test]
        public void HiddenInfoMatchesGo()
        {
            var info = new RunHiddenInfo { moderator = "M", reason = "r", deletedByOwner = true };
            Assert.AreEqual(HiddenHex, Hex(info.Write));
            var back = RunHiddenInfo.Read(Reader(HiddenHex));
            Assert.IsFalse(back.Hidden);
            Assert.AreEqual("M", back.moderator);
            Assert.AreEqual("r", back.reason);
            Assert.IsTrue(back.deletedByOwner);
            Assert.IsFalse(back.accountDisabled);
        }

        [Test]
        public void HiddenInfoRoundTrip()
        {
            var at = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
            var info = new RunHiddenInfo { hiddenAt = at, reason = "Cheating", accountDisabled = true };
            var back = RunHiddenInfo.Read(Reader(Hex(info.Write)));
            Assert.IsTrue(back.Hidden);
            Assert.AreEqual(at, back.hiddenAt);
            Assert.AreEqual("", back.moderator);
            Assert.IsTrue(back.accountDisabled);
        }

        [Test]
        public void DeleteAccountBytes()
        {
            var request = new DeleteAccountRequest { password = "pw", deleteRuns = true };
            Assert.AreEqual("02707701", Hex(request.Write));
            var back = DeleteAccountRequest.Read(Reader("02707700"));
            Assert.AreEqual("pw", back.password);
            Assert.IsFalse(back.deleteRuns);
        }

        [Test]
        public void ChangeUsernameBytes()
        {
            Assert.AreEqual(29, MessageType.ChangeUsername);
            Assert.AreEqual(16, PushType.AccountUpdated);
            Assert.AreEqual("03416263", Hex(new ChangeUsernameRequest { username = "Abc" }.Write));
            Assert.AreEqual("Hans.Huhn", ChangeUsernameRequest.Read(Reader(Hex(new ChangeUsernameRequest { username = "Hans.Huhn" }.Write))).username);
            Assert.Throws<ArgumentException>(() => Hex(new ChangeUsernameRequest { username = new string('x', CredentialsRequest.MaxUsername + 1) }.Write));
        }

        [Test]
        public void ClientRulesMatchServer()
        {
            foreach (var name in new[] { "ab", "_abc", "abc.", "has space", "Admin", "mhk-os", "Anonymous", "anon-1234", "ANON-x", new string('x', 21) })
                Assert.IsNotNull(AccountRules.CheckUsername(name), name);
            foreach (var name in new[] { "Hans_Huhn", "anon", "anonx-1", "A.b-c" })
                Assert.IsNull(AccountRules.CheckUsername(name), name);
        }

        [Test]
        public void LimitsAreChecked()
        {
            Assert.Throws<ArgumentException>(() => Hex(new DeleteAccountRequest { password = new string('p', CredentialsRequest.MaxPassword + 1) }.Write));
            // A 21-byte moderator name is over the username limit.
            Assert.Throws<WireFormatException>(() => RunHiddenInfo.Read(Reader("000000000000000015")));
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
