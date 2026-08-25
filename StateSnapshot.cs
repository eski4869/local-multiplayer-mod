using System;
using System.Collections.Generic;
using System.Reflection;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Captures an object's instance fields and writes them back into that same
    /// object later.
    ///
    /// This is the mechanical half of the rollback contract: "return this object to
    /// the values it held at frame N". It is deliberately not a deep copy.
    /// <see cref="ObjectCopier"/> produces a new object; this restores the existing
    /// one, because everything else in the game holds references to it - the
    /// EntityManager, the behaviour tree, every cloned block behaviour. Replacing
    /// the instance would leave all of them pointing at the old state.
    ///
    /// **A snapshot must be bounded by explicit roots, never by following
    /// references.** A player's object graph reaches the whole world: `BodyComp`
    /// holds an `ICollisionQuery`, which reaches the level, which reaches every
    /// block. Walking it would try to snapshot the map. So a reference-typed field
    /// is captured as the reference it is, and any mutable object reachable that way
    /// has to be its own root or it is not covered. <see cref="PlayerSnapshot"/> is
    /// where that set is declared, and <see cref="DescribeUncoveredReferences"/> is
    /// how a missing one gets reported rather than guessed at.
    /// </summary>
    internal static class StateSnapshot
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, FieldInfo[]> FieldCache =
            new Dictionary<Type, FieldInfo[]>();

        /// <summary>
        /// Every instance field on the type and its bases, resolved once.
        ///
        /// Cached because rollback captures every frame and a reflection walk per
        /// frame is the kind of cost that only shows up once the netcode is under
        /// load, by which point it is buried.
        /// </summary>
        public static FieldInfo[] FieldsOf(Type type)
        {
            if (type == null)
            {
                return new FieldInfo[0];
            }

            FieldInfo[] cached;
            if (FieldCache.TryGetValue(type, out cached))
            {
                return cached;
            }

            var fields = new List<FieldInfo>();
            for (Type level = type; level != null && level != typeof(object);
                level = level.BaseType)
            {
                fields.AddRange(level.GetFields(InstanceFields));
            }

            cached = fields.ToArray();
            FieldCache[type] = cached;
            return cached;
        }

        /// <summary>
        /// Reads every instance field into an array positioned to match
        /// <see cref="FieldsOf"/> for the object's own type.
        /// </summary>
        /// <remarks>
        /// Values are boxed. That is accepted for now: correctness first, and the
        /// cost is only worth measuring against a rollback loop that does not exist
        /// yet. Typed accessors compiled per field would remove it.
        /// </remarks>
        public static object[] Capture(object target)
        {
            if (target == null)
            {
                return null;
            }

            FieldInfo[] fields = FieldsOf(target.GetType());
            var values = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                try
                {
                    values[i] = fields[i].GetValue(target);
                }
                catch
                {
                    // Left null and skipped on restore rather than failing the
                    // whole capture: one unreadable field must not cost the frame.
                    values[i] = UnreadableField.Instance;
                }
            }

            return values;
        }

        /// <summary>
        /// Writes captured values back into the same object.
        /// </summary>
        /// <returns>False when the values do not describe this object.</returns>
        public static bool Restore(object target, object[] values)
        {
            if (target == null || values == null)
            {
                return false;
            }

            FieldInfo[] fields = FieldsOf(target.GetType());
            if (fields.Length != values.Length)
            {
                // The type changed shape since capture, which can only mean the
                // snapshot belongs to a different object. Writing it would corrupt
                // this one field by field.
                return false;
            }

            for (int i = 0; i < fields.Length; i++)
            {
                if (ReferenceEquals(values[i], UnreadableField.Instance))
                {
                    continue;
                }

                try
                {
                    // Readonly instance fields are writable through reflection and
                    // have to be, exactly as in ObjectCopier.
                    fields[i].SetValue(target, values[i]);
                }
                catch
                {
                    // A field that cannot be written is one the snapshot does not
                    // cover. Reported through DescribeUncoveredReferences rather
                    // than thrown, so a rollback is never abandoned halfway.
                }
            }

            return true;
        }

        /// <summary>
        /// Names the reference-typed fields whose values are not among
        /// <paramref name="roots"/>.
        ///
        /// Every one is a place where state could change between capture and
        /// restore without the snapshot noticing. Most are legitimate - a sprite, a
        /// settings object, the collision query - and the answer is to leave them
        /// alone. The point is that the list is visible and reviewed rather than
        /// assumed empty, the same reason <see cref="ObjectCopier.IFieldPolicy"/>
        /// reports instead of rewriting.
        /// </summary>
        public static void DescribeUncoveredReferences(
            object target,
            ICollection<object> roots,
            IList<string> notes
        )
        {
            if (target == null || notes == null)
            {
                return;
            }

            FieldInfo[] fields = FieldsOf(target.GetType());
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.FieldType.IsValueType || field.FieldType == typeof(string))
                {
                    continue;
                }

                object value;
                try
                {
                    value = field.GetValue(target);
                }
                catch
                {
                    continue;
                }

                if (value == null || (roots != null && roots.Contains(value)))
                {
                    continue;
                }

                notes.Add(
                    target.GetType().Name + "." + field.Name +
                    " -> " + value.GetType().Name
                );
            }
        }

        /// <summary>
        /// Marks a field that could not be read, so restore can tell "no value" from
        /// a captured null.
        /// </summary>
        private sealed class UnreadableField
        {
            public static readonly UnreadableField Instance = new UnreadableField();
        }
    }
}
