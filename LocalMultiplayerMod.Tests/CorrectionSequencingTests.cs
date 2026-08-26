using System.Collections.Generic;
using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// The order a correction does things in, watched step by step.
    ///
    /// The convergence harness exercises what normally happens. These cover the
    /// refusals, which normally do not happen at all - the bounds are set so the
    /// branches are unreachable in ordinary play, and unreachable code is exactly
    /// where a mistake sits undisturbed until the one session that reaches it.
    ///
    /// Every one of them shipped broken at least once.
    /// </summary>
    [TestClass]
    public class CorrectionSequencingTests
    {
        /// <summary>
        /// A world that records what was asked of it and in what order, and answers
        /// however the test needs.
        /// </summary>
        private sealed class ScriptedWorld : ICorrectionWorld
        {
            public readonly List<string> Log = new List<string>();

            public long Current = 100;
            public long Confirmed = 90;
            public long Applied = 80;
            public long WrongFrame = 85;
            public bool RestoreAllowed = true;
            public bool RestoreSucceeds = true;

            public long CurrentFrame
            {
                get { return Current; }
            }

            public long RemoteConfirmedThrough
            {
                get { return Confirmed; }
            }

            public long LastAppliedRemote
            {
                get { return Applied; }
                set { Applied = value; }
            }

            public long FirstWrongInputFrame(long from, long through)
            {
                Log.Add("search " + from + ".." + through);
                return WrongFrame >= from && WrongFrame <= through ? WrongFrame : -1;
            }

            public bool CanRestore(long simulationFrame)
            {
                Log.Add("can-restore " + simulationFrame);
                return RestoreAllowed;
            }

            public bool Restore(long simulationFrame)
            {
                Log.Add("restore " + simulationFrame);
                return RestoreSucceeds;
            }

            public void ReplayFrame(long simulationFrame)
            {
                Log.Add("replay " + simulationFrame);
            }

            public void Report(string message)
            {
                Log.Add("report");
            }

            public bool Touched
            {
                get { return Log.Contains("restore " + (WrongFrame + 1)); }
            }
        }

        [TestMethod]
        public void ACorrectionTooExpensiveToMakeTouchesNothing()
        {
            var world = new ScriptedWorld();

            // Far enough back that the replay would cost more than one real frame
            // can afford.
            world.Current = 500;
            world.Confirmed = 400;
            world.Applied = 300;
            world.WrongFrame = 350;

            Correction.Result result = Correction.Run(world);

            Assert.AreEqual(Correction.Outcome.TooExpensive, result.Outcome);

            // Giving up after the rewind left the players dragged backwards through
            // their own movement with nothing to carry them forward again. Give up
            // before touching anything, or not at all.
            Assert.IsFalse(
                world.Touched,
                "the world was rewound and then abandoned: " +
                    string.Join(" / ", world.Log.ToArray())
            );
        }

        [TestMethod]
        public void ACorrectionThatCannotReachBackTouchesNothing()
        {
            var world = new ScriptedWorld();
            world.RestoreAllowed = false;

            Correction.Result result = Correction.Run(world);

            Assert.AreEqual(Correction.Outcome.TooLate, result.Outcome);
            Assert.IsFalse(world.Touched);
        }

        [TestMethod]
        public void TheSearchNeverRunsPastTheFrameActuallySimulated()
        {
            var world = new ScriptedWorld();

            // Confirmed input runs ahead of the simulation whenever this machine is
            // behind the peer: those packets describe frames it has not reached,
            // which have no guess to be wrong about.
            world.Current = 50;
            world.Confirmed = 90;
            world.Applied = 40;
            world.WrongFrame = 45;

            Correction.Run(world);

            // Searching past the present pushed the marker beyond it, and every
            // misprediction at or before the current frame then fell below the
            // range searched and was never examined again. The machine that lags
            // becomes the one that stops correcting, and it is the only one that
            // needs to.
            CollectionAssert.Contains(world.Log, "search 41..50");
        }

        [TestMethod]
        public void TheFrameTheGameIsAboutToRunIsNotReplayed()
        {
            var world = new ScriptedWorld();
            world.Current = 100;
            world.Confirmed = 95;
            world.Applied = 90;
            world.WrongFrame = 92;

            Correction.Run(world);

            // The game simulates the current frame itself as soon as this returns.
            // Replaying it here too left the world one frame ahead of its own
            // counter, permanently and with nothing able to notice.
            CollectionAssert.DoesNotContain(world.Log, "replay 100");
            CollectionAssert.Contains(world.Log, "replay 99");
        }

        [TestMethod]
        public void TheReplayStartsAfterTheStateThatWasRestored()
        {
            var world = new ScriptedWorld();
            world.Current = 100;
            world.Confirmed = 95;
            world.Applied = 90;
            world.WrongFrame = 92;

            Correction.Run(world);

            // Input 92 is consumed by frame 94, so 93 is the state to return to and
            // 94 is the first frame to redo. Restoring and replaying the same frame
            // simulates it twice.
            CollectionAssert.Contains(world.Log, "restore 93");
            CollectionAssert.Contains(world.Log, "replay 94");
            CollectionAssert.DoesNotContain(world.Log, "replay 93");
        }

        [TestMethod]
        public void AFailedRestoreDoesNotReplayOnTopOfIt()
        {
            var world = new ScriptedWorld();
            world.RestoreSucceeds = false;

            Correction.Result result = Correction.Run(world);

            Assert.AreEqual(Correction.Outcome.RestoreFailed, result.Outcome);
            CollectionAssert.DoesNotContain(world.Log, "replay 87");
        }

        [TestMethod]
        public void GuessesThatHeldAdvanceTheSearchWithoutTouchingTheWorld()
        {
            var world = new ScriptedWorld();
            world.WrongFrame = -1;

            Correction.Result result = Correction.Run(world);

            Assert.AreEqual(Correction.Outcome.NoMisprediction, result.Outcome);

            // The common case by far - a full charge is thirty-six frames of one
            // button - so it must cost a comparison and nothing else.
            Assert.AreEqual(90, world.Applied);
            Assert.AreEqual(1, world.Log.Count);
        }
    }
}
