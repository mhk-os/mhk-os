using System;
using MHK.Online;
using NUnit.Framework;

namespace MHK.Tests
{
    /// <summary>ListBoards (128), its boards and the run rows of protocol 5 against the bytes the Go codec writes (internal/proto timetrial_test.go) and round trips.</summary>
    public class TimeTrialProtocolTests
    {
        // Track "t", any engine class, rules version 1, any variant.
        const string ListBoardsHex = "0174" + "ff" + "01000000" + "012a";
        // Board 1 of "t" version 2 "core.rain", engine class 1, 3 laps, rules version 1, 4 players, best 5 ms, no upload.
        const string BoardHex = "0100000000000000" + "0174" + "02000000" + "09636f72652e7261696e" + "01" + "03" + "01000000" + "04000000" + "05000000"
                                + "0000000000000000";
        // Run 1 of account 2 "u": 3 ms, best lap 4 ms, kart "k" version 5, character "c", 6 bytes, board 7 ("t", class 1,
        // version 8, default variant, 3 laps, rules version 1).
        const string RunHex = "0100000000000000" + "00000000" + "0200000000000000" + "0175" + "00" + "03000000" + "04000000" + "00" + "016b" + "05000000"
                              + "0163" + "00" + "0000000000000000" + "0000000000000000" + "00000000" + "00" + "06000000" + "0700000000000000" + "0174"
                              + "01" + "08000000" + "00" + "03" + "01000000";

        [Test]
        public void ListBoardsMatchesGo()
        {
            var request = new ListBoardsRequest { trackId = "t", rulesVersion = 1 };
            Assert.AreEqual(ListBoardsHex, Hex(request.Write));
            var back = ListBoardsRequest.Read(Reader(ListBoardsHex));
            Assert.AreEqual(ListBoardsRequest.AnyEngineClass, back.engineClass);
            Assert.AreEqual(BoardInfo.AnyVariant, back.variant);
        }

        [Test]
        public void BoardMatchesGo()
        {
            var board = new BoardInfo { boardId = 1, trackId = "t", trackVersion = 2, variant = "core.rain", engineClass = 1, laps = 3, rulesVersion = 1, players = 4, bestMs = 5 };
            Assert.AreEqual(BoardHex, Hex(board.Write));
            var back = BoardInfo.Read(Reader(BoardHex));
            Assert.AreEqual(2, back.trackVersion);
            Assert.AreEqual("core.rain", back.variant);
            Assert.AreEqual(default(DateTime), back.lastUpload);
        }

        [Test]
        public void RunMatchesGo()
        {
            var run = new RunEntry
            {
                runId = 1, accountId = 2, username = "u", finishMs = 3, bestLapMs = 4, kartId = "k", kartVersion = 5, characterId = "c", fileSize = 6,
                boardId = 7, trackId = "t", engineClass = 1, trackVersion = 8, laps = 3, rulesVersion = 1,
            };
            Assert.AreEqual(RunHex, Hex(run.Write));
            var back = RunEntry.Read(Reader(RunHex));
            Assert.AreEqual(5, back.kartVersion);
            Assert.AreEqual(8, back.trackVersion);
            Assert.AreEqual("", back.variant);
            Assert.AreEqual(RunHex, Hex(back.Write));
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
