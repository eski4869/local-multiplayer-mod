using System.Threading;
using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Covers the one property that separates this from a frame counter: session
    /// time keeps running when the simulation does not.
    ///
    /// The game loop skips its whole update while the pause menu is open. A client
    /// that measured time by counting its own ticks would come back from a ten
    /// second pause six hundred frames behind and have no way to notice - which is
    /// the shape of the "pausing makes the lag worse every time" report against
    /// existing multiplayer mods.
    /// </summary>
    [TestClass]
    public class NetplayClockTests
    {
        [TestMethod]
        public void StartsWithNothingSimulated()
        {
            var clock = new NetplayClock();
            clock.Start();

            Assert.AreEqual(0, clock.SimulatedFrame);
            Assert.IsTrue(clock.IsRunning);
        }

        [TestMethod]
        public void CountsTheFramesTheSimulationRan()
        {
            var clock = new NetplayClock();
            clock.Start();

            for (int i = 0; i < 5; i++)
            {
                clock.NoteSimulatedFrame();
            }

            Assert.AreEqual(5, clock.SimulatedFrame);
        }

        [TestMethod]
        public void SessionTimeAdvancesWithoutTheSimulation()
        {
            var clock = new NetplayClock();
            clock.Start();

            // Stands in for the pause menu: real time passes, no frame is
            // simulated. This is exactly the case a tick counter cannot see.
            Thread.Sleep(120);

            Assert.AreEqual(0, clock.SimulatedFrame);
            Assert.IsGreaterThanOrEqualTo(6, clock.SessionFrame, "session frame did not advance");
            Assert.IsGreaterThanOrEqualTo(6, clock.Gap, "gap did not open");
        }

        [TestMethod]
        public void GapClosesAsFramesAreSimulated()
        {
            var clock = new NetplayClock();
            clock.Start();
            Thread.Sleep(100);

            long behind = clock.Gap;
            for (long i = 0; i < behind; i++)
            {
                clock.NoteSimulatedFrame();
            }

            // Real time has moved on a little during the catch-up, so the gap
            // shrinks rather than reaching exactly zero.
            Assert.IsLessThan(behind, clock.Gap, "catching up did not reduce the gap");
        }

        [TestMethod]
        public void GapIsNeverNegative()
        {
            var clock = new NetplayClock();
            clock.Start();

            for (int i = 0; i < 500; i++)
            {
                clock.NoteSimulatedFrame();
            }

            // Simulating ahead of real time must read as "caught up", never as a
            // negative debt that a caller might act on.
            Assert.AreEqual(0, clock.Gap);
        }

        [TestMethod]
        public void SkippingAbandonsAGapTooLargeToSimulate()
        {
            var clock = new NetplayClock();
            clock.Start();
            Thread.Sleep(80);

            clock.SkipTo(clock.SessionFrame);

            // Ten seconds of pause is six hundred frames; replaying those through
            // every block behaviour inside one frame is not something to attempt.
            Assert.AreEqual(0, clock.Gap);
        }

        [TestMethod]
        public void SkippingBackwardsIsIgnored()
        {
            var clock = new NetplayClock();
            clock.Start();
            for (int i = 0; i < 10; i++)
            {
                clock.NoteSimulatedFrame();
            }

            clock.SkipTo(2);

            // Time must not run backwards, whatever a caller asks for.
            Assert.AreEqual(10, clock.SimulatedFrame);
        }

        [TestMethod]
        public void StartingAgainResetsTheSession()
        {
            var clock = new NetplayClock();
            clock.Start();
            for (int i = 0; i < 10; i++)
            {
                clock.NoteSimulatedFrame();
            }

            clock.Start();

            Assert.AreEqual(0, clock.SimulatedFrame);
        }
    }
}
