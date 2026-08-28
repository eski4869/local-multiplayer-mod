using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// The session summary is arithmetic over a histogram, which is exactly the
    /// kind of thing that can be wrong without ever looking wrong: every number
    /// it prints is plausible whatever the bug. The point of the line is to pick
    /// an input delay off it, so a miss rate that is off by one bucket picks the
    /// wrong delay and nothing about the output says so.
    /// </summary>
    [TestClass]
    public class NetplaySessionReportTests
    {
        private static string Token(string line, string key)
        {
            int at = line.IndexOf(" " + key + "=");
            Assert.IsTrue(at >= 0, key + " missing from: " + line);

            int from = at + key.Length + 2;
            int to = line.IndexOf(' ', from);
            return to < 0 ? line.Substring(from) : line.Substring(from, to - from);
        }

        private static string Summarise(params long[] lags)
        {
            var report = new NetplaySessionReport();
            report.Start(false);

            for (int i = 0; i < lags.Length; i++)
            {
                report.NoteFrame(lags[i]);
            }

            return report.Finish();
        }

        [TestMethod]
        public void AGuessRateIsTheShareOfFramesTheDelayWouldNotHaveCovered()
        {
            // Five frames, lags 0 1 2 5. A delay of 1 covers the first two.
            string line = Summarise(0, 0, 1, 2, 5);

            Assert.AreEqual("0.0", Token(line, "guess_d5"));
            Assert.AreEqual("20.0", Token(line, "guess_d2"));
            Assert.AreEqual("40.0", Token(line, "guess_d1"));
            Assert.AreEqual("60.0", Token(line, "guess_d0"));
        }

        [TestMethod]
        public void ADelayCoveringEveryFrameLeavesNothingToGuessAcross()
        {
            // The claim the whole line exists to support: if every frame's lag is
            // within the delay, that delay removes rollback entirely.
            string line = Summarise(0, 1, 2, 3, 3, 2, 1);

            Assert.AreEqual("0.0", Token(line, "guess_d3"));
            Assert.AreEqual("3", Token(line, "lag_max"));
        }

        [TestMethod]
        public void PercentilesReportFramesNotShares()
        {
            // Ninety-nine frames at lag 1 and one at lag 12: the mean is barely
            // moved and the tail is the whole story, which is why the tail is what
            // gets reported.
            var lags = new long[100];
            for (int i = 0; i < 99; i++)
            {
                lags[i] = 1;
            }

            lags[99] = 12;

            string line = Summarise(lags);

            Assert.AreEqual("1", Token(line, "lag_p50"));
            Assert.AreEqual("1", Token(line, "lag_p95"));
            Assert.AreEqual("12", Token(line, "lag_max"));
            Assert.AreEqual("1.0", Token(line, "guess_d1"));
        }

        [TestMethod]
        public void LagBeyondTheRangeStillCountsAsAGuess()
        {
            // The last bucket is a catch-all. A lag past it must not silently
            // become a covered frame - that would make a bad connection read as a
            // good one, which is the one direction this must never fail in.
            string line = Summarise(0, 100000);

            Assert.AreEqual("50.0", Token(line, "guess_d6"));
        }

        [TestMethod]
        public void ASessionThatSimulatedNothingHasNothingToSay()
        {
            var report = new NetplaySessionReport();
            report.Start(false);

            Assert.IsNull(report.Finish());
        }

        [TestMethod]
        public void ASessionCannotReportItselfTwice()
        {
            var report = new NetplaySessionReport();
            report.Start(true);
            report.NoteFrame(0);

            Assert.IsNotNull(report.Finish());

            // A peer going silent calls Leave, and so does the player leaving after
            // it. The second must produce nothing.
            Assert.IsNull(report.Finish());
        }

        [TestMethod]
        public void WrongIsAShareOfTheGuessesAndNotOfEveryFrame()
        {
            // The distinction the line exists to make. Four frames guessed at the
            // shipped delay of two, one of them wrong: that is a quarter of the
            // guesses, not a quarter of the session. Reporting it against every
            // frame is what made a fifty per cent guess rate read as a fifty per
            // cent rollback rate when the real figure was a tenth of a per cent.
            var report = new NetplaySessionReport();
            report.Start(false);

            for (int i = 0; i < 4; i++)
            {
                report.NoteFrame(5);
            }

            report.NoteRollback(3);

            string line = report.Finish();

            Assert.AreEqual("100.0", Token(line, "guessed"));
            Assert.AreEqual("25.00", Token(line, "wrong"));
        }

        [TestMethod]
        public void RollbacksAndBattleAreCarried()
        {
            var report = new NetplaySessionReport();
            report.Start(true);
            report.NoteFrame(4);
            report.NoteRollback(7);
            report.NoteRollback(3);

            string line = report.Finish();

            Assert.AreEqual("1", Token(line, "battle"));
            Assert.AreEqual("2", Token(line, "rollbacks"));
            Assert.AreEqual("10", Token(line, "resimulated"));
        }
    }
}
