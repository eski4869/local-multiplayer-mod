using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    [TestClass]
    public sealed class GatherCommandTests
    {
        [TestMethod]
        public void ParsesEveryOrderedPair()
        {
            int accepted = 0;

            for (int mover = 1; mover <= 4; mover++)
            {
                for (int target = 1; target <= 4; target++)
                {
                    string command = "p" + mover + "-p" + target;
                    int parsedMover;
                    int parsedTarget;
                    bool parsed = GatherCommand.TryParse(
                        command,
                        out parsedMover,
                        out parsedTarget
                    );

                    if (mover == target)
                    {
                        Assert.IsFalse(parsed, command + " is a no-op");
                        continue;
                    }

                    Assert.IsTrue(parsed, command);
                    Assert.AreEqual(mover, parsedMover, command);
                    Assert.AreEqual(target, parsedTarget, command);
                    accepted++;
                }
            }

            Assert.AreEqual(12, accepted);
        }

        /// <summary>
        /// The user-assignment command "pN" shares its prefix with this one. They
        /// are told apart by length, so a bare "p2" must not read as a gather.
        /// </summary>
        [TestMethod]
        public void DoesNotClaimTheUserAssignmentCommand()
        {
            AssertRejected("p1");
            AssertRejected("p2");
            AssertRejected("p4");
        }

        [TestMethod]
        public void RejectsPlayerNumbersOutsideTheSupportedRange()
        {
            AssertRejected("p0-p1");
            AssertRejected("p5-p1");
            AssertRejected("p1-p0");
            AssertRejected("p1-p5");
        }

        [TestMethod]
        public void RejectsMalformedSeparatorsAndPrefixes()
        {
            AssertRejected("p1>p2");
            AssertRejected("p1-2");
            AssertRejected("1-p2");
            AssertRejected("p1--p2");
            AssertRejected("p1-p2-p3");
        }

        [TestMethod]
        public void RejectsEmptyInput()
        {
            AssertRejected(null);
            AssertRejected("");
        }

        private static void AssertRejected(string command)
        {
            int mover;
            int target;
            Assert.IsFalse(
                GatherCommand.TryParse(command, out mover, out target),
                command ?? "<null>"
            );
            Assert.AreEqual(0, mover);
            Assert.AreEqual(0, target);
        }
    }
}
