using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// The delay stopped being a constant, which means the arithmetic that used
    /// to be true by inspection is now true only for the values the menu can
    /// actually produce. That is worth checking, because widening the range is a
    /// one-character change and the failure it would cause - corrections quietly
    /// refused for being too expensive - looks like nothing at all.
    /// </summary>
    [TestClass]
    public class NetplaySettingsTests
    {
        [TestCleanup]
        public void Restore()
        {
            RollbackPlan.ResetInputDelayFrames();
            NetplaySettings.AutomaticDelay = true;
            NetplaySettings.ManualDelayFrames =
                RollbackPlan.DefaultInputDelayFrames;
        }

        [TestMethod]
        public void EverySelectableDelayKeepsCorrectionsInsideTheCeiling()
        {
            for (int delay = RollbackPlan.MinInputDelayFrames;
                delay <= RollbackPlan.MaxInputDelayFrames;
                delay++)
            {
                RollbackPlan.SetInputDelayFrames(delay);

                // A guess that could get older than the ceiling allows would start
                // having its correction skipped, and a skipped correction is a
                // divergence no later correction can undo.
                Assert.IsTrue(
                    RollbackPlan.MaxPredictionFrames +
                        RollbackPlan.InputDelayFrames <
                        RollbackPlan.MaxRollbackFrames,
                    "delay " + delay + " puts a correction past the cost ceiling"
                );

                Assert.IsTrue(
                    RollbackPlan.RequiredBufferFrames >
                        RollbackPlan.MaxPredictionFrames +
                            RollbackPlan.InputDelayFrames,
                    "delay " + delay + " outlives the buffer"
                );
            }
        }

        [TestMethod]
        public void ADelayOffTheWireIsBroughtIntoRangeRatherThanRefused()
        {
            // It arrives as a byte from the other machine. Refusing to start a
            // session over a number that could have been clamped would be the
            // worse failure.
            RollbackPlan.SetInputDelayFrames(200);
            Assert.AreEqual(
                RollbackPlan.MaxInputDelayFrames,
                RollbackPlan.InputDelayFrames
            );

            RollbackPlan.SetInputDelayFrames(-5);
            Assert.AreEqual(
                RollbackPlan.MinInputDelayFrames,
                RollbackPlan.InputDelayFrames
            );
        }

        [TestMethod]
        public void ManualIgnoresWhateverWasMeasured()
        {
            NetplaySettings.AutomaticDelay = false;
            NetplaySettings.ManualDelayFrames = 5;

            Assert.AreEqual(5, NetplaySettings.Resolve(400.0));
            Assert.AreEqual(5, NetplaySettings.Resolve(0.0));
        }

        [TestMethod]
        public void AutoTurnsOneWayTravelIntoFrames()
        {
            NetplaySettings.AutomaticDelay = true;

            // 68ms round trip is 34ms one way, which is two frames at the 17ms
            // this game really runs at - not the 16.67 it asks for.
            Assert.AreEqual(2, NetplaySettings.Resolve(68.0));

            // Rounded up, never to nearest: a delay one frame short of the travel
            // leaves the rollback doing the work the delay was chosen to avoid.
            Assert.AreEqual(3, NetplaySettings.Resolve(69.0));
        }

        [TestMethod]
        public void AutoNeverAnswersZeroAndNeverAnswersPastTheRange()
        {
            NetplaySettings.AutomaticDelay = true;

            // Zero is a legitimate thing to ask for and not a thing to arrive at:
            // it means every frame is a guess, which should be chosen knowingly.
            Assert.AreEqual(1, NetplaySettings.Resolve(0.5));

            Assert.AreEqual(
                RollbackPlan.MaxInputDelayFrames,
                NetplaySettings.Resolve(5000.0)
            );
        }

        [TestMethod]
        public void AutoWithNothingMeasuredFallsBackRatherThanGuessing()
        {
            NetplaySettings.AutomaticDelay = true;

            // No round trip exists until somebody joins. A session that somehow
            // starts without one takes the value the mod shipped with.
            Assert.AreEqual(
                RollbackPlan.DefaultInputDelayFrames,
                NetplaySettings.Resolve(0.0)
            );
        }
    }
}
