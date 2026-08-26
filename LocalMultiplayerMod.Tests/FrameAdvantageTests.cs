using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Deciding which machine waits, and for how long.
    ///
    /// The measurement each side can take is the gap and the travel time added
    /// together, with no way to separate them from one side. These pin the property
    /// that makes the pair usable: the travel time cancels, so the answer is the
    /// gap and nothing else - the same answer on a fast link and a slow one.
    /// </summary>
    [TestClass]
    public class FrameAdvantageTests
    {
        /// <summary>
        /// What each side measures: where the peer's frame number appeared to be,
        /// minus its own frame. The peer's number left <paramref name="latency"/>
        /// frames ago, so it reads that much lower than it truly is.
        /// </summary>
        private static float Measured(int ownLead, int latency)
        {
            return -ownLead - latency;
        }

        [TestMethod]
        public void TheTravelTimeCancelsOut()
        {
            // This machine is five frames in front, over a seven frame link.
            float local = Measured(5, 7);     // -12
            float remote = Measured(-5, 7);   //  -2

            // Five, not twelve. The raw measurement said twelve, which is the gap
            // and the link added together, and stalling on that is what froze a
            // connection that was keeping perfect pace.
            Assert.AreEqual(5, RollbackPlan.FramesToWait(local, remote));
        }

        [TestMethod]
        public void TheSameGapGivesTheSameAnswerOnAnyLink()
        {
            // A gap of five is a gap of five whether the link is one frame or
            // twenty. If this ever stops holding, the wait has started responding
            // to the connection instead of to the difference in pace.
            Assert.AreEqual(
                RollbackPlan.FramesToWait(Measured(5, 1), Measured(-5, 1)),
                RollbackPlan.FramesToWait(Measured(5, 20), Measured(-5, 20))
            );
        }

        [TestMethod]
        public void TheMachineBehindNeverWaits()
        {
            // Eight frames behind on a seven frame link. Waiting here widens the
            // very gap it is supposed to close - and it is what the game did while
            // frozen, holding itself back to let a peer catch up that was already
            // eight frames in front.
            float local = Measured(-8, 7);
            float remote = Measured(8, 7);

            Assert.AreEqual(0, RollbackPlan.FramesToWait(local, remote));
        }

        [TestMethod]
        public void LevelMachinesNeverWait()
        {
            Assert.AreEqual(0, RollbackPlan.FramesToWait(Measured(0, 7), Measured(0, 7)));
        }

        [TestMethod]
        public void ASmallGapIsNotWorthAStutter()
        {
            // Two frames apart. The prediction covers that at no cost; stopping the
            // game for it costs a stutter that is plainly visible.
            Assert.AreEqual(0, RollbackPlan.FramesToWait(Measured(2, 7), Measured(-2, 7)));
        }

        [TestMethod]
        public void AWaitIsBoundedShortEnoughNotToReadAsACrash()
        {
            // The cap is on the response, not on how long a peer may be slow. Two
            // seconds was allowed here once and the result was indistinguishable
            // from a hang.
            Assert.IsTrue(RollbackPlan.MaxStallFrames <= 12);
        }
    }
}
