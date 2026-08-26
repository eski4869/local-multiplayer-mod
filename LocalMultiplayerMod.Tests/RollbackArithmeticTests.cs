using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// The index arithmetic a correction depends on.
    ///
    /// A correction is found in one index space and applied in another. An input
    /// recorded at I is consumed by the simulation <c>InputDelayFrames</c> later,
    /// and snapshots are indexed by the simulation frame that produced them - so
    /// the frame to return to is not the frame the disagreement was found at.
    ///
    /// Getting it wrong is silent. Nothing throws and nothing logs; the world just
    /// ends up a fixed distance from where it belongs, every time, which never
    /// shows as a spike and cannot be found by playing. It can be found here.
    /// </summary>
    [TestClass]
    public class RollbackArithmeticTests
    {
        [TestMethod]
        public void ReplayStartsWhereTheWrongInputWasActuallyConsumed()
        {
            RollbackPlan.Plan plan = RollbackPlan.For(100, 110);

            // Input 100 is not read by the simulation on frame 100. It is held back
            // by the input delay and consumed on 102, so 100 and 101 are innocent
            // and must not be replayed.
            Assert.AreEqual(102, plan.FirstSpoiled);
            Assert.AreEqual(101, plan.RestoreTo);
        }

        [TestMethod]
        public void ReplayLandsExactlyWhereTheGameLeftOff()
        {
            const long current = 110;
            RollbackPlan.Plan plan = RollbackPlan.For(100, current);

            // The restored state is the one frame RestoreTo left behind, and each
            // replayed frame advances it by one.
            long landsAfter = plan.RestoreTo + plan.ReplayFrames;

            // current has not been simulated yet - the game runs it as soon as the
            // correction returns - so the world must be left exactly one frame short
            // of it. Landing on current would simulate it twice, and a world one
            // step ahead of its own counter is an error no later frame can notice.
            Assert.AreEqual(current - 1, landsAfter);
        }

        [TestMethod]
        public void TheWholeSpoiledStretchIsReplayedAndNothingMore()
        {
            RollbackPlan.Plan plan = RollbackPlan.For(100, 110);

            // 102 through 109: every frame that read the wrong guess, and no frame
            // that did not.
            Assert.AreEqual(8, plan.ReplayFrames);
        }

        [TestMethod]
        public void AGuessNotYetActedOnIsNotACorrection()
        {
            // The real input for 100 arrived while the simulation was still on 101.
            // Frame 102 has not run and will read the real value when it does.
            // Rewinding here would undo frames that were never wrong.
            RollbackPlan.Plan plan = RollbackPlan.For(100, 101);

            Assert.IsFalse(plan.Needed);
            Assert.AreEqual(0, plan.ReplayFrames);
        }

        [TestMethod]
        public void TheFrameAboutToRunIsNotACorrectionEither()
        {
            // The guess is consumed by exactly the frame the game is about to
            // simulate. There is nothing to undo.
            RollbackPlan.Plan plan = RollbackPlan.For(100, 102);

            Assert.IsFalse(plan.Needed);
        }

        [TestMethod]
        public void TheEarliestSpoiledFrameIsStillRepairable()
        {
            // One frame past the boundary is a real correction, of exactly one
            // frame. An off-by-one here would silently skip the smallest and most
            // common correction of all.
            RollbackPlan.Plan plan = RollbackPlan.For(100, 103);

            Assert.IsTrue(plan.Needed);
            Assert.AreEqual(1, plan.ReplayFrames);
            Assert.AreEqual(101, plan.RestoreTo);
        }

        [TestMethod]
        public void TheBufferReachesEverythingTheCostCeilingAllows()
        {
            // The cost ceiling should be the only thing that refuses a correction.
            // A refusal from the buffer is a permanent divergence rather than a
            // skipped repair, so the buffer must cover the widest correction the
            // ceiling permits, plus the frame it restores before it.
            Assert.IsTrue(
                RollbackPlan.RequiredBufferFrames >
                    RollbackPlan.MaxRollbackFrames,
                "the buffer must not be the thing that refuses a correction"
            );
        }

        [TestMethod]
        public void NoGuessCanOutliveTheBuffer()
        {
            // Nothing is guessed further ahead than the prediction window, so no
            // disagreement is older than that window plus the delay it waits out.
            Assert.IsTrue(
                RollbackPlan.RequiredBufferFrames >
                    RollbackPlan.MaxPredictionFrames + RollbackPlan.InputDelayFrames
            );
        }

        [TestMethod]
        public void ThePredictionWindowKeepsCorrectionsInsideTheCostCeiling()
        {
            // The window is what makes the ceiling unreachable in practice. If a
            // guess could get older than the ceiling allows, corrections would
            // start being skipped - and a skipped correction is a divergence that
            // no later correction can undo.
            Assert.IsTrue(
                RollbackPlan.MaxPredictionFrames + RollbackPlan.InputDelayFrames <
                    RollbackPlan.MaxRollbackFrames
            );
        }
    }
}
