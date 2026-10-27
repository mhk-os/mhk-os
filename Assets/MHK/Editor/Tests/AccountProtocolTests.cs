using System;
using MHK.Online;
using NUnit.Framework;

namespace MHK.Tests
{
    /// <summary>DeleteAccount (26) and RunHiddenInfo (135) against the bytes the Go codec writes (internal/proto achievements_test.go) and round trips.</summary>
    public class AccountProtocolTests
    {
        // TestAchievementBytes: not hidden, moderator "M", reason "r", deleted by the owner.
        const string HiddenHex = "0000000000000000014d01720100";

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
