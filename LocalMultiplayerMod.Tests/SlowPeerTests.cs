using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// One machine that cannot hold sixty frames a second, which is the situation
    /// this was actually being played in.
    ///
    /// Not a slow link - a slow machine. The distinction matters: a link delays
    /// what the two say to each other and they still cover the same ground at the
    /// same rate, while a machine that misses frames covers less ground per second
    /// than its peer and the distance between them grows without limit unless
    /// something closes it.
    /// </summary>
    [TestClass]
    public class SlowPeerTests
    {
        /// <summary>Two frames in ten missed - fifty frames a second against sixty.</summary>
        private const int SlowByAFifth = 2;

        [TestMethod]
        public void TheGapToASlowMachineStaysBounded()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 11, bSkipsPerTen: SlowByAFifth
            );

            // Ten frames a second of divergence, unbounded, is what the real logs
            // showed: a hundred and ninety seven frames apart, better than three
            // seconds, the other king driven entirely by guesswork for all of it.
            // Nothing there was desynchronised; there was simply nobody home.
            long apart = outcome.WidestGap;

            Assert.IsTrue(
                apart < 30,
                "the machines drifted " + apart + " frames apart: " + outcome.Detail
            );
        }

        [TestMethod]
        public void ASlowMachineStaysInStepWithWhatItDoesSimulate()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 12, bSkipsPerTen: SlowByAFifth
            );

            // Falling behind in time is one thing; computing a different world is
            // another, and only the second is a fault.
            Assert.IsTrue(outcome.Converged, outcome.Detail);
        }

        [TestMethod]
        public void TheFasterMachineWaitsRatherThanRunningAway()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 13, bSkipsPerTen: SlowByAFifth
            );

            // The prediction window is a bound. Giving up on it after a few frames
            // turned it into a preference, and the host then ran away for the whole
            // session while reporting, every single frame, that it had decided to
            // wait.
            Assert.IsTrue(
                outcome.A.Stalls > 0,
                "the faster machine never waited: " + outcome.Detail
            );
        }

        [TestMethod]
        public void TheSlowerMachineCoversGroundItHasMissed()
        {
            // A hitch rather than steady slowness: half a second where B stops
            // outright. Steady slowness never opens a gap once the faster machine
            // waits properly - which is why this needs its own case, and why the
            // first attempt at this test failed for the right reason.
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 14, bSkipsPerTen: 0, bHitchAt: 300, bHitchFrames: 30
            );

            // Waiting keeps a gap from growing but never closes one. This is the
            // other half: extra frames simulated without drawing, over input that
            // has already arrived, so they are cheap and no correction has to pay
            // for them twice.
            Assert.IsTrue(
                outcome.B.CatchUps > 0,
                "the slower machine never caught up: " + outcome.Detail
            );
        }

        [TestMethod]
        public void WaitingAloneBoundsTheGap()
        {
            // Catching up switched off, so this measures the waiting by itself.
            // The two together are redundant in the common case, which is a good
            // property and a bad test: with either one working the gap stays shut,
            // so neither is ever shown to carry weight.
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 16, bSkipsPerTen: SlowByAFifth, bHitchAt: 0, bHitchFrames: 0,
                catchUp: false
            );

            long apart = outcome.WidestGap;

            // The prediction window is what bounds this, and only while it is
            // actually held to. Given up on after a few frames it bounds nothing,
            // and a machine merely faster than its peer runs away for the whole
            // session.
            Assert.IsTrue(
                apart < 30,
                "waiting did not hold the gap: " + apart + " frames, " + outcome.Detail
            );
        }

        [TestMethod]
        public void GivingUpOnTheWaitLetsTheFasterMachineRunAway()
        {
            // The version that shipped: wait, but only for nine frames, then proceed
            // regardless. It was put in to stop a crashed peer freezing the game,
            // and it did that by turning the bound into a preference.
            //
            // The failure needs a gap that opens before the waiting engages - a
            // hitch at the start, a level still loading - because nine frames of
            // waiting can close a small gap and escape. Past that width it cannot,
            // and then the state absorbs: having given up, the machine never waits
            // again, so the gap never closes, so it never stops having given up.
            // The real log shows exactly that shape - stall_pred=60 against
            // stalled=0, deciding to wait sixty times a second and never once
            // waiting.
            //
            // Catching up is off here so the waiting is measured alone. With both
            // working the gap stays shut either way, which is a good property and
            // hides which one is holding it.
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 17, bSkipsPerTen: SlowByAFifth, bHitchAt: 60, bHitchFrames: 90,
                catchUp: false, giveUpWaitingAfter: 9
            );

            long apart = outcome.WidestGap;

            // Far apart, and it kept growing for as long as the session ran. The
            // real log reached a hundred and ninety seven frames while reporting,
            // every frame, that it had decided to wait.
            Assert.IsTrue(
                apart > 100,
                "expected the runaway this reproduces, got " + apart + " frames apart"
            );
        }

        [TestMethod]
        public void NeitherMachineIsHeldStillForever()
        {
            Harness.Outcome outcome = Harness.Play(
                frames: 900, latency: 5, jitter: 2, lossPercent: 0,
                seed: 15, bSkipsPerTen: SlowByAFifth
            );

            // Waiting without limit is correct only while the peer is alive and
            // sending. Both must still be advancing.
            Assert.IsTrue(outcome.A.Frame > 400, outcome.Detail);
            Assert.IsTrue(outcome.B.Frame > 400, outcome.Detail);
        }
    }
}
