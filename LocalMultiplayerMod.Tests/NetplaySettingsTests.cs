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
        public void TravelWorthNothingDoesNotRaiseTheDelay()
        {
            NetplaySettings.AutomaticDelay = true;

            // Auto used to add a frame of delay per frame of travel. That is the
            // wrong trade here: travel decides how many frames are predicted, and
            // a first real session measured prediction at 99.7% correct - fifty
            // per cent of frames predicted, twelve wrong out of nine thousand.
            // Buying that fraction with input delay on every frame, in a game
            // where the length of a press is the mechanic, is a bad bargain.
            //
            // 68ms round trip is two frames one way at the 17ms this game really
            // runs at. Nothing is added for it.
            Assert.AreEqual(
                RollbackPlan.DefaultInputDelayFrames,
                NetplaySettings.Resolve(68.0)
            );

            Assert.AreEqual(
                RollbackPlan.DefaultInputDelayFrames,
                NetplaySettings.Resolve(0.5)
            );
        }

        [TestMethod]
        public void AutoClimbsOnlyForALinkFarEnoughToBeWorthIt()
        {
            NetplaySettings.AutomaticDelay = true;

            // 204ms round trip is six frames one way. Four of those are free, so
            // two are added to the shipped default.
            Assert.AreEqual(
                RollbackPlan.DefaultInputDelayFrames + 2,
                NetplaySettings.Resolve(204.0)
            );
        }

        [TestMethod]
        public void AutoNeverGoesBelowTheShippedDefaultOrPastTheRange()
        {
            NetplaySettings.AutomaticDelay = true;

            // Never below what the mod ships with. A measurement is not a licence
            // to change how the game feels; going lower is the owner's call.
            Assert.IsTrue(
                NetplaySettings.Resolve(1.0) >=
                    RollbackPlan.DefaultInputDelayFrames
            );

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
