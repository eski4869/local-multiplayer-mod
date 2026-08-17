using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Copies an object field by field without running its constructor.
    ///
    /// Separated from <see cref="BehaviourCloner"/> so that the mechanics can be
    /// tested without the game: what goes wrong here - a readonly field silently
    /// left at its default, a private field on a base class missed, a shared
    /// instance copied twice - produces a mod that loads, runs, and behaves subtly
    /// wrong for the second player only.
    /// </summary>
    internal static class ObjectCopier
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Decides what happens to each field value. The copier knows how to walk
        /// an object; only the caller knows which references mean "belongs to a
        /// particular player".
        /// </summary>
        public interface IFieldPolicy
        {
            /// <summary>
            /// True when <paramref name="replacement"/> should be stored instead of
            /// the original value.
            /// </summary>
            bool TryRebind(
                FieldInfo field,
                object value,
                out object replacement
            );

            /// <summary>
            /// A note when a value was copied as-is but probably should not have
            /// been, or null when it is fine. Reporting beats guessing: rewriting
            /// an arbitrary object graph would be inventing intent.
            /// </summary>
            string Inspect(object value);
        }

        /// <param name="identityMap">
        /// Keyed by reference. Two fields holding the same object must still hold
        /// the same object after copying, or state that was deliberately shared
        /// becomes two independent halves.
        /// </param>
        /// <param name="notes">Field names that could not be handled cleanly.</param>
        public static object Copy(
            object original,
            IFieldPolicy policy,
            IDictionary<object, object> identityMap,
            IList<string> notes
        )
        {
            if (original == null)
            {
                return null;
            }

            object existing;
            if (identityMap != null && identityMap.TryGetValue(original, out existing))
            {
                return existing;
            }

            Type type = original.GetType();
            object copy = FormatterServices.GetUninitializedObject(type);

            // Recorded before the fields are walked, so an object that refers back
            // to itself resolves to the copy rather than recursing forever.
            if (identityMap != null)
            {
                identityMap[original] = copy;
            }

            for (Type level = type; level != null && level != typeof(object);
                level = level.BaseType)
            {
                FieldInfo[] fields = level.GetFields(InstanceFields);
                for (int i = 0; i < fields.Length; i++)
                {
                    CopyField(fields[i], original, copy, policy, identityMap, notes);
                }
            }

            return copy;
        }

        private static void CopyField(
            FieldInfo field,
            object original,
            object copy,
            IFieldPolicy policy,
            IDictionary<object, object> identityMap,
            IList<string> notes
        )
        {
            object value;
            try
            {
                value = field.GetValue(original);
            }
            catch
            {
                Note(notes, field.Name + " (unreadable)");
                return;
            }

            object toStore = value;
            object alreadyCopied;

            if (policy != null && policy.TryRebind(field, value, out toStore))
            {
                // Claimed by the caller: a player reference becomes the target's.
            }
            else if (value != null && identityMap != null &&
                identityMap.TryGetValue(value, out alreadyCopied))
            {
                // Already copied for this player, so point at that copy rather than
                // back at the original. This is what keeps a behaviour that refers
                // to itself, or to another behaviour in the same set, pointing
                // inside its own player's set instead of at player 1's.
                toStore = alreadyCopied;
            }
            else
            {
                toStore = value;
                if (policy != null)
                {
                    string note = policy.Inspect(value);
                    if (note != null)
                    {
                        Note(notes, field.Name + " (" + note + ")");
                    }
                }
            }

            try
            {
                // Readonly instance fields are writable through reflection, and
                // they have to be: most captured player references are declared
                // readonly.
                field.SetValue(copy, toStore);
            }
            catch
            {
                Note(notes, field.Name + " (unwritable)");
            }
        }

        private static void Note(IList<string> notes, string text)
        {
            if (notes != null)
            {
                notes.Add(text);
            }
        }

        /// <summary>
        /// Reference identity, not <c>Equals</c>. A behaviour that overrides
        /// equality would otherwise have two distinct registrations collapsed into
        /// one copy.
        /// </summary>
        public sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
