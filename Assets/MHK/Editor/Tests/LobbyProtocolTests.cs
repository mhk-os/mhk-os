using System;
using System.Collections.Generic;
using System.Xml;
using MHK.Core;
using MHK.Editor.Build;
using MHK.Online;
using NUnit.Framework;

namespace MHK.Tests
{
    /// <summary>
    /// The lobby protocol against the server: byte vectors the Go codec (internal/proto, the pinned vectors of
    /// lobby_test.go) writes. Plus content ids, join links and the Android link filter.
    /// </summary>
    public class LobbyProtocolTests
    {
        // internal/proto lobby_test.go vectors (pinHex) written by the Go codec.
        const string SettingsHex = "016e" + "02" + "010170" + "01" + "02" + "03" + "00" + "01" + "0178" + "0000c03f" + "09" + "0a" + "00" + "02" + "00"
                                   + "02" + "03612e62" + "03612e63";
        const string SummaryHex = "0100000000000000" + "01" + "00" + "0200" + "016e" + "01" + "01" + "010170" + "03" + "04" + "05" + "fbffffff"
                                  + "01" + "02" + "10" + "03";
        const string AiRowHex = "01" + "0000000000000000" + "00" + "03" + "04000000" + "05000000" + "06000000" + "01" + "01" + "0163";
        const string HelloHex = "07000000" + "02" + "0467616d65" + "03616263" + "046d686b32" + "00";
        const string ManifestHex = "046d686b32" + "0163" + "07000000" + "02" + "01" + "096d686b322e726f6d65" + "03000000"
                                   + "07" + "0a6d686b322e6172656e61" + "01000000";
        // Vote / StartRace body: trackId "t", variant "core.reverse".
        const string TrackChoiceHex = "0174" + "0c636f72652e72657665727365";

        [Test]
        public void ContentIds()
        {
            Assert.IsTrue(ContentManifests.IsContentId("mhk2.kart.speedy"));
            Assert.IsFalse(ContentManifests.IsContentId("p.a b"));
            Assert.IsFalse(ContentManifests.IsContentId("p.hühn"));
            Assert.IsFalse(ContentManifests.IsContentId(new string('a', 97)));
            Assert.IsTrue(ContentManifests.IsPackId("tincan"));
            Assert.IsFalse(ContentManifests.IsPackId("a.b"));
            Assert.AreEqual("game", ContentManifests.PackOf("game.huhn"));
            Assert.AreEqual("", ContentManifests.PackOf("nodot"));
        }

        [Test]
        public void RoomSettingsMatchGo()
        {
            var s = new RoomSettings
            {
                name = "n", mode = RoomMode.Championship, packs = new List<string> { "p" }, anticheat = true, engineClass = 2, laps = 3,
                items = false, itemOdds = new List<PowerupOdds> { new("x", 1.5f) }, maxAi = 9, maxPlayers = 10, trackPick = 0, racesPerSet = 2,
                cupId = "", trackIds = new List<string> { "a.b", "a.c" },
            };
            Assert.AreEqual(SettingsHex, Hex(s.Write));
            var back = RoomSettings.Read(Reader(SettingsHex));
            Assert.AreEqual(SettingsHex, Hex(back.Write));
            Assert.IsTrue(back.CustomTracks);
            Assert.AreEqual(9, back.maxAi);
        }

        [Test]
        public void SummaryAndResultRowMatchGo()
        {
            var summary = new LobbySummary
            {
                lobbyId = 1, isPublic = false, category = 0, instance = 2, name = "n", mode = RoomMode.BattleSet, anticheat = true,
                packs = new List<string> { "p" }, players = 3, maxPlayers = 4, phase = LobbyPhase.Results, rulesVersion = -5, engineClass = 1,
                setRace = 2, setSize = 16, maxAi = 3,
            };
            Assert.AreEqual(SummaryHex, Hex(summary.Write));
            Assert.AreEqual(3, LobbySummary.Read(Reader(SummaryHex)).maxAi);
            var row = new RaceResultRow
            {
                slot = 1, accountId = 0, username = "", position = 3, finishMs = 4, points = 5, setPoints = 6, outcome = RaceOutcome.Dnf, ai = true,
                characterId = "c",
            };
            Assert.AreEqual(AiRowHex, Hex(row.Write));
            var back = RaceResultRow.Read(Reader(AiRowHex));
            Assert.IsTrue(back.ai);
            Assert.AreEqual("c", back.characterId);
        }

        [Test]
        public void ContentMessagesMatchGo()
        {
            var hello = new ContentHelloRequest { rulesVersion = 7 };
            hello.packs.Add(new PackRef { packId = "game", commit = "abc" });
            hello.packs.Add(new PackRef { packId = "mhk2", commit = "" });
            Assert.AreEqual(HelloHex, Hex(hello.Write));
            CollectionAssert.AreEqual(new[] { "mhk2" }, ContentHelloAnswer.Read(Reader("01" + "046d686b32")).missing);
            var manifest = new PackManifest { packId = "mhk2", commit = "c", rulesVersion = 7 };
            manifest.items.Add(new ManifestItem(ContentKind.RaceTrack, "mhk2.rome", 3));
            manifest.items.Add(new ManifestItem(ContentKind.Arena, "mhk2.arena", 1));
            Assert.AreEqual(ManifestHex, Hex(manifest.Write));
        }

        [Test]
        public void LobbyStateAndResultsMatchGo()
        {
            // The minimal state of lobby_test.go: everything empty or zero but one AI driver and the owner's cup.
            var stateHex = new string('0', 2 * 27) + "00" + new string('0', 2 * 14) + "00" + new string('0', 2 * 8) + "00" + "00" + "00" + "00" + "00"
                           + new string('0', 2 * 8) + "00" + "01" + "0163" + "016b" + "ffffffff" + "0163" + "01" + "0174";
            var state = LobbyState.Read(Reader(stateHex));
            Assert.AreEqual(stateHex, Hex(state.Write));
            Assert.AreEqual(1, state.aiDrivers.Count);
            Assert.AreEqual("c", state.aiDrivers[0].characterId);
            Assert.AreEqual(-1, state.aiDrivers[0].setPoints);
            Assert.IsTrue(state.RunsCup);
            CollectionAssert.AreEqual(new[] { "t" }, state.cupTrackIds);
            // One vote (account 9 for "t" reversed) and the drawn track "t" reversed.
            state.votes.Add(new LobbyVote(9, "t", "core.reverse"));
            state.trackId = "t";
            state.variant = "core.reverse";
            var votedHex = new string('0', 2 * 27) + "00" + new string('0', 2 * 14) + "00" + new string('0', 2 * 8) + "00" + "00"
                           + "01" + "0900000000000000" + TrackChoiceHex + TrackChoiceHex
                           + new string('0', 2 * 8) + "00" + "01" + "0163" + "016b" + "ffffffff" + "0163" + "01" + "0174";
            Assert.AreEqual(votedHex, Hex(state.Write));
            var voted = LobbyState.Read(Reader(votedHex));
            Assert.AreEqual("core.reverse", voted.votes[0].variant);
            Assert.AreEqual("core.reverse", voted.variant);
            var results = new RaceResults { raceId = 8, setDone = true };
            for (var i = 0; i < LobbyLimits.RaceRows; i++)
                results.rows.Add(new RaceResultRow { slot = i, position = i + 1, ai = i > 0, characterId = "c", username = i > 0 ? "" : "Kurt" });
            var resultsHex = Hex(results.Write);
            var back = RaceResults.Read(Reader(resultsHex));
            Assert.AreEqual(resultsHex, Hex(back.Write));
            Assert.AreEqual(LobbyLimits.RaceRows, back.rows.Count);
            Assert.IsTrue(back.setDone);
        }

        [Test]
        public void JoinLinksParse()
        {
            Assert.IsTrue(JoinLinks.TryParse("mhkos://join/K7QX2M", out var code));
            Assert.AreEqual("K7QX2M", code);
            Assert.IsTrue(JoinLinks.TryParse("MHKOS://Join/k7qx2m/", out code));
            Assert.AreEqual("K7QX2M", code);
            Assert.IsFalse(JoinLinks.TryParse("mhkos://join/K7QX2", out _));
            Assert.IsFalse(JoinLinks.TryParse("mhkos://join/K7QX2O", out _), "O is not in the code alphabet");
            Assert.IsFalse(JoinLinks.TryParse("https://join/K7QX2M", out _));
            Assert.AreEqual("K7QX2M", JoinLinks.NormalizeCode("k7q-x2m"));
            Assert.AreEqual("mhkos://join/K7QX2M", JoinLinks.Link("K7QX2M"));
        }

        [Test]
        public void LinkForwardingBetweenListeners()
        {
            const int port = 47391;
            var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var first = JoinLinkForwarder.Listen(port, got.Enqueue);
            Assert.IsNotNull(first);
            Assert.IsNull(JoinLinkForwarder.Listen(port, _ => { }), "a second instance cannot listen on the same port");
            Assert.IsTrue(JoinLinkForwarder.TryForward("mhkos://join/K7QX2M", port));
            Assert.IsFalse(JoinLinkForwarder.TryForward("https://example.com", port), "only room links are taken");
            Assert.IsTrue(got.TryDequeue(out var url));
            Assert.AreEqual("mhkos://join/K7QX2M", url);
            first.Dispose();
            Assert.IsFalse(JoinLinkForwarder.TryForward("mhkos://join/K7QX2M", port), "nobody listens");
        }

        [Test]
        public void AndroidManifestGetsLinkFilter()
        {
            var doc = new XmlDocument();
            doc.LoadXml("<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"><application>" +
                        "<activity android:name=\"com.unity3d.player.UnityPlayerGameActivity\"><intent-filter>" +
                        "<action android:name=\"android.intent.action.MAIN\" /><category android:name=\"android.intent.category.LAUNCHER\" />" +
                        "</intent-filter></activity><activity android:name=\"other\" /></application></manifest>");
            Assert.AreEqual(1, JoinLinksManifest.AddJoinLinkFilter(doc));
            Assert.AreEqual(0, JoinLinksManifest.AddJoinLinkFilter(doc), "applied once");
            var xml = doc.OuterXml;
            StringAssert.Contains("<data android:scheme=\"mhkos\" android:host=\"join\" />", xml);
            StringAssert.Contains("android:launchMode=\"singleTask\"", xml);
            StringAssert.Contains("<category android:name=\"android.intent.category.BROWSABLE\" />", xml);
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
