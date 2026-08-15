using System.Collections.Generic;
using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Covers the mechanics of copying a block behaviour for another player.
    ///
    /// These are the failures that would not announce themselves: a constructor
    /// that never runs leaving a readonly field at its default, a private field on
    /// a base class going unnoticed, or two block types that deliberately shared
    /// one behaviour ending up with a copy each. All three produce a mod that loads
    /// and runs, and misbehaves only for the second player.
    /// </summary>
    [TestClass]
    public class ObjectCopierTests
    {
        private class Marker
        {
            public string Owner;
        }

        private class BaseBehaviour
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

        private class Behaviour : BaseBehaviour
        {
            public readonly Marker Bound;
            public readonly int Setting;
            public bool IsPlayerOnBlock;
            public Marker Shared;

            public Behaviour(Marker bound, int setting)
            {
                Bound = bound;
                Setting = setting;
            }
        }

        private class SelfReferencing
        {
            public SelfReferencing Self;
            public int Value;
        }

        /// <summary>Swaps any Marker owned by "one" for the target's own.</summary>
        private sealed class SwapOwner : ObjectCopier.IFieldPolicy
        {
            private readonly Marker _replacement;

            public SwapOwner(Marker replacement)
            {
                _replacement = replacement;
            }

            public bool TryRebind(object value, out object replacement)
            {
                var marker = value as Marker;
                if (marker != null && marker.Owner == "one")
                {
                    replacement = _replacement;
                    return true;
                }

                replacement = null;
                return false;
            }

            public string Inspect(object value)
            {
                return null;
            }
        }

        private static object Copy(
            object original,
            ObjectCopier.IFieldPolicy policy,
            IDictionary<object, object> map,
            IList<string> notes
        )
        {
            return ObjectCopier.Copy(original, policy, map, notes);
        }

        [TestMethod]
        public void CopiesReadonlyFields()
        {
            var original = new Behaviour(new Marker { Owner = "one" }, 42);

            var copy = (Behaviour)Copy(original, null, null, null);

            // A readonly field is exactly what a skipped constructor leaves empty.
            Assert.AreEqual(42, copy.Setting);
            Assert.IsNotNull(copy.Bound);
        }

        [TestMethod]
        public void CopiesPrivateAndProtectedFieldsFromBaseTypes()
        {
            var original = new Behaviour(new Marker { Owner = "one" }, 1);
            original.SeedBase(7, "kept");

            var copy = (Behaviour)Copy(original, null, null, null);

            Assert.AreEqual(7, copy.ReadPrivateOnBase());
            Assert.AreEqual("kept", copy.ReadProtectedOnBase());
        }

        [TestMethod]
        public void DoesNotRunTheConstructor()
        {
            var original = new Behaviour(new Marker { Owner = "one" }, 5);
            original.IsPlayerOnBlock = true;

            var copy = (Behaviour)Copy(original, null, null, null);

            // Field state is carried over wholesale, including the interface's own
            // per-player flag, rather than being re-derived.
            Assert.IsTrue(copy.IsPlayerOnBlock);
        }

        [TestMethod]
        public void RebindsValuesThePolicyClaims()
        {
            var mine = new Marker { Owner = "two" };
            var original = new Behaviour(new Marker { Owner = "one" }, 1);

            var copy = (Behaviour)Copy(original, new SwapOwner(mine), null, null);

            Assert.AreSame(mine, copy.Bound);
        }

        [TestMethod]
        public void LeavesUnclaimedValuesAlone()
        {
            var shared = new Marker { Owner = "shared" };
            var original = new Behaviour(new Marker { Owner = "one" }, 1);
            original.Shared = shared;

            var copy = (Behaviour)Copy(
                original,
                new SwapOwner(new Marker { Owner = "two" }),
                null,
                null
            );

            // A settings object or collision query is meant to be shared, so it is
            // carried across by reference rather than duplicated.
            Assert.AreSame(shared, copy.Shared);
        }

        [TestMethod]
        public void KeepsSharedInstancesSharedWithinOneMap()
        {
            var shared = new Behaviour(new Marker { Owner = "one" }, 1);
            var map = new Dictionary<object, object>(
                ObjectCopier.ReferenceComparer.Instance
            );

            object first = Copy(shared, null, map, null);
            object second = Copy(shared, null, map, null);

            // SwitchBlocks registers one behaviour for two block types on purpose.
            Assert.AreSame(first, second);
        }

        [TestMethod]
        public void SeparatesSharedInstancesAcrossMaps()
        {
            var shared = new Behaviour(new Marker { Owner = "one" }, 1);

            object playerTwo = Copy(
                shared,
                null,
                new Dictionary<object, object>(ObjectCopier.ReferenceComparer.Instance),
                null
            );
            object playerThree = Copy(
                shared,
                null,
                new Dictionary<object, object>(ObjectCopier.ReferenceComparer.Instance),
                null
            );

            Assert.AreNotSame(playerTwo, playerThree);
        }

        [TestMethod]
        public void SurvivesASelfReference()
        {
            var original = new SelfReferencing { Value = 3 };
            original.Self = original;

            var map = new Dictionary<object, object>(
                ObjectCopier.ReferenceComparer.Instance
            );
            var copy = (SelfReferencing)Copy(original, null, map, null);

            Assert.AreEqual(3, copy.Value);
            Assert.AreSame(copy, copy.Self);
        }

        [TestMethod]
        public void ReportsWhatThePolicyFlags()
        {
            var original = new Behaviour(new Marker { Owner = "one" }, 1);
            var notes = new List<string>();

            Copy(original, new FlagEverything(), null, notes);

            Assert.IsTrue(notes.Count > 0);
            CollectionAssert.Contains(notes, "Bound (suspicious)");
        }

        private sealed class FlagEverything : ObjectCopier.IFieldPolicy
        {
            public bool TryRebind(object value, out object replacement)
            {
                replacement = null;
                return false;
            }

            public string Inspect(object value)
            {
                return value is Marker ? "suspicious" : null;
            }
        }
    }
}
