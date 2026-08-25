using System.Collections.Generic;
using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Covers returning an object to values it held earlier.
    ///
    /// These are the failures that would not announce themselves. A snapshot that
    /// quietly skips a base class's private field, or that replaces the instance
    /// instead of refilling it, produces a rollback that looks like it worked and
    /// leaves the player one frame of charge short - which in this game is about one
    /// block of height, and reads in play as "the same jump reached a different
    /// place".
    /// </summary>
    [TestClass]
    public class StateSnapshotTests
    {
        private class BaseState
        {
            private int _privateOnBase;
            protected string ProtectedOnBase;

            public void SeedBase(int value, string text)
            {
                _privateOnBase = value;
                ProtectedOnBase = text;
            }

            public int ReadPrivateOnBase()
            {
                return _privateOnBase;
            }

            public string ReadProtectedOnBase()
            {
                return ProtectedOnBase;
            }
        }

        private class Body : BaseState
        {
            public readonly string Name;
            public float ChargeTimer;
            public int Screen;
            public bool IsOnGround;
            public object CollisionQuery;

            public Body(string name)
            {
                Name = name;
            }
        }

        private class Other
        {
            public int Value;
        }

        [TestMethod]
        public void RestoresValueTypeFields()
        {
            var body = new Body("p1") { ChargeTimer = 0.5f, Screen = 3 };

            object[] snapshot = StateSnapshot.Capture(body);
            body.ChargeTimer = 1f;
            body.Screen = 99;
            StateSnapshot.Restore(body, snapshot);

            // One frame of charge is roughly one block of height, so this is the
            // value rollback exists to protect.
            Assert.AreEqual(0.5f, body.ChargeTimer);
            Assert.AreEqual(3, body.Screen);
        }

        [TestMethod]
        public void RestoresIntoTheSameInstance()
        {
            var body = new Body("p1") { Screen = 1 };
            object[] snapshot = StateSnapshot.Capture(body);
            body.Screen = 2;

            StateSnapshot.Restore(body, snapshot);

            // The EntityManager, the behaviour tree and every cloned block
            // behaviour hold this reference. Handing back a new object would leave
            // all of them on the old state.
            Assert.AreEqual(1, body.Screen);
            Assert.AreEqual("p1", body.Name);
        }

        [TestMethod]
        public void CoversPrivateAndProtectedFieldsOnBaseTypes()
        {
            var body = new Body("p1");
            body.SeedBase(7, "before");

            object[] snapshot = StateSnapshot.Capture(body);
            body.SeedBase(8, "after");
            StateSnapshot.Restore(body, snapshot);

            Assert.AreEqual(7, body.ReadPrivateOnBase());
            Assert.AreEqual("before", body.ReadProtectedOnBase());
        }

        [TestMethod]
        public void RestoresReadonlyFields()
        {
            var body = new Body("p1");

            object[] snapshot = StateSnapshot.Capture(body);
            StateSnapshot.Restore(body, snapshot);

            // Readonly instance fields are writable through reflection and must be:
            // skipping them would make the covered set depend on how a third party
            // happened to declare its fields.
            Assert.AreEqual("p1", body.Name);
        }

        [TestMethod]
        public void RestoresReferenceFieldsAsTheSameReference()
        {
            var query = new Other { Value = 1 };
            var body = new Body("p1") { CollisionQuery = query };

            object[] snapshot = StateSnapshot.Capture(body);
            body.CollisionQuery = new Other { Value = 2 };
            StateSnapshot.Restore(body, snapshot);

            // Captured as the reference it is, not followed. Following it would try
            // to snapshot the level, and through it the whole map.
            Assert.AreSame(query, body.CollisionQuery);
        }

        [TestMethod]
        public void DoesNotFollowReferencesIntoSharedState()
        {
            var query = new Other { Value = 1 };
            var body = new Body("p1") { CollisionQuery = query };

            object[] snapshot = StateSnapshot.Capture(body);
            query.Value = 2;
            StateSnapshot.Restore(body, snapshot);

            // The world moving on is not this snapshot's business. Anything mutable
            // that must come back has to be its own root, which is what
            // DescribeUncoveredReferences exists to surface.
            Assert.AreEqual(2, query.Value);
        }

        [TestMethod]
        public void RejectsASnapshotTakenFromADifferentType()
        {
            var body = new Body("p1") { Screen = 1 };
            object[] foreign = StateSnapshot.Capture(new Other { Value = 5 });

            bool restored = StateSnapshot.Restore(body, foreign);

            // Writing it would corrupt the target field by field, in whatever order
            // reflection happened to return.
            Assert.IsFalse(restored);
            Assert.AreEqual(1, body.Screen);
        }

        [TestMethod]
        public void ReportsAReferenceNoRootCovers()
        {
            var body = new Body("p1") { CollisionQuery = new Other() };
            var notes = new List<string>();

            StateSnapshot.DescribeUncoveredReferences(
                body,
                new HashSet<object>(ObjectCopier.ReferenceComparer.Instance),
                notes
            );

            Assert.HasCount(1, notes);
            StringAssert.Contains(notes[0], "CollisionQuery");
        }

        [TestMethod]
        public void DoesNotReportAReferenceThatIsItselfARoot()
        {
            var query = new Other();
            var body = new Body("p1") { CollisionQuery = query };
            var roots = new HashSet<object>(ObjectCopier.ReferenceComparer.Instance)
            {
                query
            };
            var notes = new List<string>();

            StateSnapshot.DescribeUncoveredReferences(body, roots, notes);

            Assert.IsEmpty(notes);
        }

        [TestMethod]
        public void DoesNotReportValueTypesOrStrings()
        {
            var body = new Body("p1") { ChargeTimer = 1f, IsOnGround = true };
            body.SeedBase(1, "text");
            var notes = new List<string>();

            StateSnapshot.DescribeUncoveredReferences(
                body,
                new HashSet<object>(ObjectCopier.ReferenceComparer.Instance),
                notes
            );

            // A float cannot change behind the snapshot's back, and neither can a
            // string. Reporting them would bury the entries that matter.
            Assert.IsEmpty(notes);
        }

        [TestMethod]
        public void CapturingNullYieldsNothingToRestore()
        {
            Assert.IsNull(StateSnapshot.Capture(null));
            Assert.IsFalse(StateSnapshot.Restore(new Other(), null));
            Assert.IsFalse(StateSnapshot.Restore(null, new object[0]));
        }
    }
}
