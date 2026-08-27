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
                    values[i] = CaptureValue(fields[i].GetValue(target));
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
        /// An array of value types is copied rather than referenced.
        ///
        /// The rule elsewhere is that references are stored as references, because
        /// following them reaches the whole world. An array of primitives or
        /// structs is the exception worth carving out: it holds no references of
        /// its own to follow, it is almost always private working state of the
        /// object that declares it, and leaving it shared makes a restore silently
        /// incomplete.
        ///
        /// That silence is what this cost. <c>JumpState</c> keeps the last four
        /// input states in a buffer and uses them to recover the direction when a
        /// key was released just before takeoff. Restoring the timer but not the
        /// buffer left a jump reading directions from frames that had been rolled
        /// back - visible only at the moment of a left or right jump, which is the
        /// only thing that buffer feeds.
        /// </summary>
        private static object CaptureValue(object value)
        {
            var array = value as Array;
            if (array == null || array.Rank != 1)
            {
                return value;
            }

            Type element = array.GetType().GetElementType();
            if (element == null || !element.IsValueType)
            {
                // An array of references is left alone: copying it would duplicate
                // the entries, and following them is what this must not do.
                return value;
            }

            var copy = (Array)array.Clone();
            return new ArraySnapshot { Original = array, Values = copy };
        }

        /// <summary>
        /// A copied array and the one it came from, so a restore writes the values
        /// back into the array the object still holds rather than replacing it.
        /// </summary>
        private sealed class ArraySnapshot
        {
            public Array Original;
            public Array Values;
        }

        /// <summary>
        /// Writes captured values back into the same object.
        /// </summary>
        /// <returns>False when the values do not describe this object.</returns>
        /// <summary>Field writes that threw since the counter was last read.</summary>
        public static int FailedWrites;

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
                    var array = values[i] as ArraySnapshot;
                    if (array != null)
                    {
                        // Written back into the array the object still holds, so
                        // anything else pointing at it sees the restored contents.
                        Array.Copy(
                            array.Values,
                            array.Original,
                            array.Values.Length
                        );
                        fields[i].SetValue(target, array.Original);
                        continue;
                    }

                    // Readonly instance fields are writable through reflection and
                    // have to be, exactly as in ObjectCopier.
                    fields[i].SetValue(target, values[i]);
                }
                catch
                {
                    // A field that cannot be written is one the snapshot does not
                    // cover. Reported through DescribeUncoveredReferences rather
                    // than thrown, so a rollback is never abandoned halfway.
                    //
                    // Counted, because a throw costs far more than the write it
                    // replaced and this loop runs over every field of every root
                    // of every player. Whether restoring is slow because
                    // reflection is slow or because it is throwing dozens of
                    // exceptions is the difference between a cost that can be
                    // removed and one that cannot, and guessing between them is
                    // how this file has been wrong before.
                    FailedWrites++;
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

                // An array of value types is captured by value, so it is covered
                // even though it is a reference. Reporting it would put a false
                // entry in a list whose whole worth is that everything on it is
                // worth looking at.
                var array = value as Array;
                if (array != null && array.Rank == 1)
                {
                    Type element = array.GetType().GetElementType();
                    if (element != null && element.IsValueType)
                    {
                        continue;
                    }
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
