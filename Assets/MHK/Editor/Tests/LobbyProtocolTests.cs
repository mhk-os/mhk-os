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
    /// The lobby protocol against the server: the manifest hash vectors of internal/content's tests and byte vectors the
    /// Go codec (internal/proto, the samples of lobby_test.go) writes. Plus join links and the Android link filter.
    /// </summary>
    public class LobbyProtocolTests
    {
        // internal/proto lobby_test.go samples encoded by the Go codec.
        const string SettingsHex = "0b48c3bc686e65722d4375700202046d686b320674696e63616e01010501020b6d686b322e726f636b65740000c841086d686b322e6f696c00009642010801040d6d686b322e6375702e676f6c64";
        const string HelloHex = "07000000020467616d6503616263026666046d686b3200023030";
        const string ManifestHex = "046d686b3201630700000001680201096d686b322e726f6d65026631070a6d686b322e6172656e6100";
        const string ResultsHex = "080000000000000002000a00000000000000044b75727401187301000c0000001b0000000000010b00000000000000044b75727402187301000c0000001b000000000001";
        const string StateHex = "040000000000000000030200085261636573202332000102046d686b32046d686b33030a0107000000020304064142433233340b48c3bc686e65722d4375700202046d686b320674696e63616e01010501020b6d686b322e726f636b65740000c841086d686b322e6f696c00009642010801040d6d686b322e6375702e676f6c640101000000000000000448616e730244450e6d686b322e636861722e6875686e106d686b322e6b6172742e737065656479010101013000250000007b8ac7039a010000027b8ac7039a0100000204020100000000000000096d686b322e726f6d65020000000000000000096d686b322e726f6d65630000000000000002000a00000000000000044b75727401187301000c0000001b0000000000030d00000000000000044b75727404187301000c0000001b0000000001";

        [Test]
        public void ManifestHashMatchesServerVector()
        {
            var items = new List<ManifestItem>
            {
                new(ContentKind.RaceTrack, "mhk2.Rome", "ab"),
                new(ContentKind.Kart, "mhk2.kart", "cd"),
                new(ContentKind.RaceTrack, "mhk2.alps", "ef"),
            };
            Assert.AreEqual("0cf67de748fbeb1d772512d45c3b24af", ContentManifests.Hash(items));
            items.Reverse();
            Assert.AreEqual("0cf67de748fbeb1d772512d45c3b24af", ContentManifests.Hash(items));
            Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb924", ContentManifests.Hash(new List<ManifestItem>()));
            var upper = ContentManifests.Hash(new List<ManifestItem> { new(ContentKind.RaceTrack, "p.AC", ""), new(ContentKind.RaceTrack, "p.ab", "") });
            var lower = ContentManifests.Hash(new List<ManifestItem> { new(ContentKind.RaceTrack, "p.ab", ""), new(ContentKind.RaceTrack, "p.ac", "") });
            Assert.AreEqual(lower, upper);
        }

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
                name = "Hühner-Cup", mode = RoomMode.Championship, packs = new List<string> { "mhk2", "tincan" }, anticheat = true,
                engineClass = 1, laps = 5, items = true, itemOdds = new List<PowerupOdds> { new("mhk2.rocket", 25), new("mhk2.oil", 75) },
                aiWhenAlone = true, maxPlayers = 8, trackPick = 1, racesPerSet = 4, cupId = "mhk2.cup.gold",
            };
            Assert.AreEqual(SettingsHex, Hex(s.Write));
            var back = RoomSettings.Read(Reader(SettingsHex));
            Assert.AreEqual(SettingsHex, Hex(back.Write));
        }

        [Test]
        public void ContentMessagesMatchGo()
        {
            var hello = new ContentHelloRequest { contentVersion = 7 };
            hello.packs.Add(new PackRef { packId = "game", commit = "abc", manifestHash = "ff" });
            hello.packs.Add(new PackRef { packId = "mhk2", commit = "", manifestHash = "00" });
            Assert.AreEqual(HelloHex, Hex(hello.Write));
            var manifest = new PackManifest { packId = "mhk2", commit = "c", contentVersion = 7, manifestHash = "h" };
            manifest.items.Add(new ManifestItem(ContentKind.RaceTrack, "mhk2.rome", "f1"));
            manifest.items.Add(new ManifestItem(ContentKind.Arena, "mhk2.arena", ""));
            Assert.AreEqual(ManifestHex, Hex(manifest.Write));
        }

        [Test]
        public void LobbyStateAndResultsMatchGo()
        {
            var state = LobbyState.Read(Reader(StateHex));
            Assert.AreEqual(StateHex, Hex(state.Write));
            Assert.AreEqual("ABC234", state.code);
            Assert.IsTrue(state.summary.isPublic);
            Assert.AreEqual(LobbyPhase.Voting, state.Phase);
            Assert.AreEqual("Hans", state.members[0].username);
            Assert.AreEqual(1_761_000_000_123L, state.phaseEnds);
            Assert.AreEqual("", state.VoteOf(2));
            Assert.AreEqual(2, state.lastResults.Count);
            Assert.IsTrue(state.lastResults[1].ai);
            var results = RaceResults.Read(Reader(ResultsHex));
            Assert.AreEqual(ResultsHex, Hex(results.Write));
            Assert.IsTrue(results.setDone);
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
