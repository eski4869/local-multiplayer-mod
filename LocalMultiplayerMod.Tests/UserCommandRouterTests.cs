using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    [TestClass]
    public sealed class UserCommandRouterTests
    {
        private static UserCommandRouter CreateRouter(
            IList<UserOverridePreference> singleOverrides = null,
            IList<UserOverridePreference> multiplayerOverrides = null,
            IList<UserOverridePreference> fourPlayerOverrides = null
        )
        {
            return new UserCommandRouter(
                new[] { "*" },
                singleOverrides ?? new List<UserOverridePreference>(),
                new[] { "[a-m]*", "[n-z]*" },
                multiplayerOverrides ?? new List<UserOverridePreference>(),
                new[] { "[a-f]*", "[g-m]*", "[n-s]*", "[t-z]*" },
                fourPlayerOverrides ?? new List<UserOverridePreference>()
            );
        }

        [TestMethod]
        public void SingleModeWithoutUserTargetsPlayer1()
        {
            Assert.AreEqual(PlayerTargets.Player1, CreateRouter().Resolve(1, null));
        }

        [TestMethod]
        public void MultiplayerModesWithoutUserAreIgnored()
        {
            UserCommandRouter router = CreateRouter();

            Assert.AreEqual(PlayerTargets.None, router.Resolve(2, null));
            Assert.AreEqual(PlayerTargets.None, router.Resolve(4, "  "));
        }

        [TestMethod]
        [DataRow("alice", 1)]
        [DataRow("m_user", 1)]
        [DataRow("nancy", 2)]
        [DataRow("z_user", 2)]
        [DataRow("ALICE", 1)]
        public void TwoPlayerModeUsesDefaultRoutes(string user, int expected)
        {
            Assert.AreEqual(
                (PlayerTargets)expected,
                CreateRouter().Resolve(2, user)
            );
        }

        [TestMethod]
        [DataRow("alice", 1)]
        [DataRow("george", 2)]
        [DataRow("nancy", 4)]
        [DataRow("tom", 8)]
        [DataRow("Z_USER", 8)]
        public void FourPlayerModeUsesDefaultRoutes(string user, int expected)
        {
            Assert.AreEqual(
                (PlayerTargets)expected,
                CreateRouter().Resolve(4, user)
            );
        }

        [TestMethod]
        public void ExactOverrideTakesPriorityOverDefaultRoute()
        {
            var overrides = new List<UserOverridePreference>
            {
                new UserOverridePreference { Name = "z", Player = 1 }
            };
            UserCommandRouter router = CreateRouter(
                fourPlayerOverrides: overrides
            );

            Assert.AreEqual(PlayerTargets.Player1, router.Resolve(4, "z"));
            Assert.AreEqual(PlayerTargets.Player4, router.Resolve(4, "zelda"));
        }

        [TestMethod]
        public void OverridesAreScopedToTheirMode()
        {
            var twoPlayerOverrides = new List<UserOverridePreference>
            {
                new UserOverridePreference { Name = "z", Player = 1 }
            };
            var fourPlayerOverrides = new List<UserOverridePreference>
            {
                new UserOverridePreference { Name = "z", Player = 3 }
            };
            UserCommandRouter router = CreateRouter(
                multiplayerOverrides: twoPlayerOverrides,
                fourPlayerOverrides: fourPlayerOverrides
            );

            Assert.AreEqual(PlayerTargets.Player1, router.Resolve(2, "z"));
            Assert.AreEqual(PlayerTargets.Player3, router.Resolve(4, "z"));
        }

        [TestMethod]
        public void AssignmentReplacesExistingOverrideWithoutChangingDefaults()
        {
            var overrides = new List<UserOverridePreference>
            {
                new UserOverridePreference { Name = "Alice", Player = 1 }
            };

            Assert.IsTrue(UserOverrideEditor.TryAssign(
                overrides,
                2,
                2,
                "alice"
            ));
            Assert.HasCount(1, overrides);
            Assert.AreEqual("alice", overrides[0].Name);
            Assert.AreEqual(2, overrides[0].Player);

            UserCommandRouter router = CreateRouter(
                multiplayerOverrides: overrides
            );
            Assert.AreEqual(PlayerTargets.Player2, router.Resolve(2, "alice"));
        }

        [TestMethod]
        public void AssignmentRejectsInvalidPlayerAndPatternSyntax()
        {
            var overrides = new List<UserOverridePreference>();

            Assert.IsFalse(UserOverrideEditor.TryAssign(
                overrides,
                2,
                3,
                "alice"
            ));
            Assert.IsFalse(UserOverrideEditor.TryAssign(
                overrides,
                2,
                1,
                "team_*"
            ));
        }

        [TestMethod]
        public void DuplicateOverridesAreRejected()
        {
            var overrides = new List<UserOverridePreference>
            {
                new UserOverridePreference { Name = "alice", Player = 1 },
                new UserOverridePreference { Name = "ALICE", Player = 2 }
            };

            AssertFormatException(delegate
            {
                CreateRouter(multiplayerOverrides: overrides);
            });
        }

        [TestMethod]
        public void OverrideOutsideModePlayerRangeIsRejected()
        {
            var overrides = new List<UserOverridePreference>
            {
                new UserOverridePreference { Name = "alice", Player = 3 }
            };

            AssertFormatException(delegate
            {
                CreateRouter(multiplayerOverrides: overrides);
            });
        }

        [TestMethod]
        public void OverlappingDefaultRoutesReturnFirstPlayer()
        {
            var router = new UserCommandRouter(
                new[] { "*" },
                new List<UserOverridePreference>(),
                new[] { "team*", "team*" },
                new List<UserOverridePreference>(),
                new[] { "*", "*", "*", "*" },
                new List<UserOverridePreference>()
            );

            Assert.AreEqual(PlayerTargets.Player1, router.Resolve(2, "team_red"));
        }

        [TestMethod]
        [DataRow("a*b")]
        [DataRow("[z-a]*")]
        [DataRow("[a-m]")]
        [DataRow("[a-m]**")]
        public void InvalidDefaultPatternsAreRejected(string pattern)
        {
            AssertFormatException(delegate
            {
                new UserCommandRouter(
                    new[] { "*" },
                    new List<UserOverridePreference>(),
                    new[] { pattern, "*" },
                    new List<UserOverridePreference>(),
                    new[] { "*", "*", "*", "*" },
                    new List<UserOverridePreference>()
                );
            });
        }

        private static void AssertFormatException(Action action)
        {
            try
            {
                action();
                Assert.Fail("Expected FormatException.");
            }
            catch (FormatException)
            {
            }
        }
    }
}
