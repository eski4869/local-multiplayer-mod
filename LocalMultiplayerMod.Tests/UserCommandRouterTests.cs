using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    [TestClass]
    public sealed class UserCommandRouterTests
    {
        private static readonly UserCommandRouter DefaultRouter =
            new UserCommandRouter("*", "[a-m]*", "[n-z]*");
        private static readonly UserCommandRouter FourPlayerRouter =
            new UserCommandRouter(
                "*",
                "[a-m]*",
                "[n-z]*",
                "[a-f]*",
                "[g-m]*",
                "[n-s]*",
                "[t-z]*"
            );

        [TestMethod]
        public void SingleModeWithoutUserTargetsPlayer1()
        {
            Assert.AreEqual(PlayerTargets.Player1, DefaultRouter.Resolve(false, null));
        }

        [TestMethod]
        public void SingleModeAppliesItsOwnAllowList()
        {
            var router = new UserCommandRouter("alice,bob", "*", "*");

            Assert.AreEqual(PlayerTargets.Player1, router.Resolve(false, "Alice"));
            Assert.AreEqual(PlayerTargets.None, router.Resolve(false, "carol"));
        }

        [TestMethod]
        public void MultiplayerModeWithoutUserIsIgnored()
        {
            Assert.AreEqual(PlayerTargets.None, DefaultRouter.Resolve(true, null));
            Assert.AreEqual(PlayerTargets.None, DefaultRouter.Resolve(true, "  "));
        }

        [TestMethod]
        [DataRow("alice", 1)]
        [DataRow("m_user", 1)]
        [DataRow("nancy", 2)]
        [DataRow("z_user", 2)]
        [DataRow("ALICE", 1)]
        public void MultiplayerModeRoutesInitialRanges(string user, int expected)
        {
            Assert.AreEqual((PlayerTargets)expected, DefaultRouter.Resolve(true, user));
        }

        [TestMethod]
        [DataRow("alice", 1)]
        [DataRow("george", 2)]
        [DataRow("nancy", 4)]
        [DataRow("tom", 8)]
        [DataRow("Z_USER", 8)]
        public void FourPlayerModeRoutesFourInitialRanges(string user, int expected)
        {
            Assert.AreEqual((PlayerTargets)expected, FourPlayerRouter.Resolve(4, user));
        }

        [TestMethod]
        public void FourPlayerModeWithoutUserIsIgnored()
        {
            Assert.AreEqual(PlayerTargets.None, FourPlayerRouter.Resolve(4, null));
            Assert.AreEqual(PlayerTargets.None, FourPlayerRouter.Resolve(4, "  "));
        }

        [TestMethod]
        public void FourPlayerModePrefersExactMatch()
        {
            var router = new UserCommandRouter(
                "*",
                "*",
                "*",
                "eski*",
                "other",
                "eski4869",
                "eski*"
            );

            Assert.AreEqual(
                PlayerTargets.Player3,
                router.Resolve(4, "eski4869")
            );
        }

        [TestMethod]
        public void OverlappingPatternsReturnFirstPlayer()
        {
            var router = new UserCommandRouter(
                "*",
                "*",
                "*",
                "eski*",
                "other",
                "team*",
                "eski*"
            );

            Assert.AreEqual(
                PlayerTargets.Player1,
                router.Resolve(4, "eski4869")
            );
        }

        [TestMethod]
        public void ExactMatchTakesPriorityOverAnEarlierWildcard()
        {
            var router = new UserCommandRouter("*", "eski*", "other");

            Assert.AreEqual(PlayerTargets.Player1, router.Resolve(true, "eski4869"));

            router = new UserCommandRouter("*", "eski*", "eski4869");
            Assert.AreEqual(
                PlayerTargets.Player2,
                router.Resolve(true, "eski4869")
            );
        }

        [TestMethod]
        public void DuplicateExactMatchesReturnFirstPlayer()
        {
            var router = new UserCommandRouter("*", "alice", "alice");

            Assert.AreEqual(
                PlayerTargets.Player1,
                router.Resolve(true, "alice")
            );
        }

        [TestMethod]
        public void AssignmentMovesExactUserAndPreservesPatterns()
        {
            string[] updated;
            Assert.IsTrue(UserAllowListEditor.TryAssign(
                new[] { "[a-m]*,alice", "[n-z]*" },
                2,
                "Alice",
                out updated
            ));

            CollectionAssert.AreEqual(
                new[] { "[a-m]*", "[n-z]*,Alice" },
                updated
            );

            var router = new UserCommandRouter("*", updated[0], updated[1]);
            Assert.AreEqual(
                PlayerTargets.Player2,
                router.Resolve(true, "alice")
            );
        }

        [TestMethod]
        public void AssignmentRejectsPatternSyntaxAndInvalidPlayer()
        {
            string[] updated;
            Assert.IsFalse(UserAllowListEditor.TryAssign(
                new[] { "*", "*" },
                3,
                "alice",
                out updated
            ));
            Assert.IsFalse(UserAllowListEditor.TryAssign(
                new[] { "*", "*" },
                1,
                "team_*",
                out updated
            ));
        }

        [TestMethod]
        public void CommaSeparatedExactAndPrefixPatternsAreSupported()
        {
            var router = new UserCommandRouter("*", "alice,team_*", "bob");

            Assert.AreEqual(PlayerTargets.Player1, router.Resolve(true, "team_red"));
            Assert.AreEqual(PlayerTargets.Player2, router.Resolve(true, "bob"));
            Assert.AreEqual(PlayerTargets.None, router.Resolve(true, "carol"));
        }

        [TestMethod]
        [DataRow("a*b")]
        [DataRow("[z-a]*")]
        [DataRow("[a-m]")]
        [DataRow("[a-m]**")]
        public void InvalidPatternsAreRejected(string pattern)
        {
            try
            {
                new UserCommandRouter("*", pattern, "*");
                Assert.Fail("Expected FormatException for: " + pattern);
            }
            catch (FormatException)
            {
            }
        }
    }
}
