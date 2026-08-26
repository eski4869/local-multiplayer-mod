using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// The claim the whole netcode exists to make: two machines given the same
    /// inputs end in the same state, whatever the link did to the packets on the
    /// way.
    ///
    /// Everything else - prediction, corrections, the buffer, the bounds - is
    /// machinery for keeping this true while hiding the delay. If it holds, the
    /// remaining faults are about how it feels. If it does not, nothing about how it
    /// feels is worth discussing.
    /// </summary>
    [TestClass]
    public class NetplayConvergenceTests
    {
        [TestMethod]
        public void APerfectLinkStaysInStep()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 1, jitter: 0, lossPercent: 0, seed: 1
            );

            Assert.IsTrue(outcome.Converged, outcome.Detail);
        }

        [TestMethod]
        public void ASlowLinkStaysInStep()
        {
            // Seven frames each way - the latency measured on the real connection,
            // and the number that used to be mistaken for permanent frame advantage.
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 7, jitter: 0, lossPercent: 0, seed: 2
            );

            Assert.IsTrue(outcome.Converged, outcome.Detail);
        }

        [TestMethod]
        public void AJitteryLinkStaysInStep()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 6, jitter: 4, lossPercent: 0, seed: 3
            );

            Assert.IsTrue(outcome.Converged, outcome.Detail);
        }

        [TestMethod]
        public void ALossyLinkStaysInStep()
        {
            // A tenth of the packets never arrive. Every packet carries sixteen
            // frames of history precisely so that this is survivable.
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 5, jitter: 3, lossPercent: 10, seed: 4
            );

            Assert.IsTrue(outcome.Converged, outcome.Detail);
        }

        [TestMethod]
        public void ItStaysInStepAcrossManySeeds()
        {
            // One seed passing is one sequence of guesses being lucky. The faults
            // this is guarding against were all conditional on which prediction
            // happened to be wrong and when.
            for (int seed = 0; seed < 25; seed++)
            {
                Harness.Outcome outcome = Harness.Play(
                    frames: 400, latency: 5, jitter: 3, lossPercent: 5, seed: seed
                );

                Assert.IsTrue(
                    outcome.Converged,
                    "seed " + seed + ": " + outcome.Detail
                );
            }
        }

        [TestMethod]
        public void CorrectionsActuallyHappen()
        {
            // Otherwise every test above passes by never exercising the thing they
            // exist to check. Zero corrections across six hundred frames of a
            // delayed link was the real reading that hid a broken search for
            // months.
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 7, jitter: 2, lossPercent: 0, seed: 5
            );

            Assert.IsTrue(
                outcome.A.Corrections > 0,
                "no correction ever ran: " + outcome.Detail
            );
        }

        [TestMethod]
        public void CorrectionsStayCheap()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 7, jitter: 2, lossPercent: 0, seed: 6
            );

            // The prediction window is what bounds this. A correction replays every
            // frame back to the wrong guess inside one real frame, so an average
            // creeping upwards is the spiral where one bad moment never recovers.
            double average = outcome.A.Corrections == 0
                ? 0
                : (double)outcome.A.FramesReplayed / outcome.A.Corrections;

            Assert.IsTrue(
                average <= RollbackPlan.MaxPredictionFrames + RollbackPlan.InputDelayFrames,
                "corrections averaged " + average + " frames"
            );
        }

        [TestMethod]
        public void NobodyIsToldTheyDesynchronised()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 7, jitter: 3, lossPercent: 5, seed: 7
            );

            // These messages were appearing hundreds of times in a single session.
            // Each one is a correction that was needed and could not be made, which
            // is a divergence nothing later undoes.
            CollectionAssert.AreEqual(
                new string[0],
                outcome.A.Reports.ToArray(),
                string.Join(" / ", outcome.A.Reports.ToArray())
            );
        }

        [TestMethod]
        public void NeitherMachineFreezes()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 600, latency: 7, jitter: 3, lossPercent: 0, seed: 8
            );

            // The game locked up completely once, and the log said so plainly -
            // the frame counter stopped moving. A machine that has stopped
            // advancing is the worst outcome available, worse than any
            // desynchronisation, and it is checkable in one line.
            Assert.IsTrue(
                outcome.A.Frame > 700 && outcome.B.Frame > 700,
                outcome.Detail
            );
        }
    }
}
