using System;
using System.Collections.Generic;
using System.Reflection;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Produces a per-player copy of a registered <c>IBlockBehaviour</c> without
    /// running any of the mod's code.
    ///
    /// The level-start replay exists because a behaviour cannot be built again from
    /// outside: its constructor takes an <c>ICollisionQuery</c>, a settings object,
    /// sometimes the <c>PlayerEntity</c> itself. That is true, and it is also beside
    /// the point - copying an object does not require its constructor.
    ///
    /// Sharing one instance is not an option either: <c>IsPlayerOnBlock</c> is a
    /// settable member of <c>IBlockBehaviour</c> that the body writes per player,
    /// so every behaviour carries mutable per-player state whether its author added
    /// any or not. A copy per player is the least that can be correct.
    ///
    /// What has to change in the copy is small and identifiable by type. Across all
    /// twelve block mods currently installed there are thirteen player-typed fields
    /// in total; everything else - collision queries, settings, numbers - is meant
    /// to be shared and is carried across untouched.
    /// </summary>
    internal static class BehaviourCloner
    {
        public static object Clone(
            object original,
            PlayerContext target,
            IDictionary<object, object> identityMap,
            out string problem
        )
        {
            problem = null;
            if (original == null || target == null)
            {
                return null;
            }

            var notes = new List<string>();
            object copy = ObjectCopier.Copy(
                original,
                new PlayerRebindPolicy(target),
                identityMap,
                notes
            );

            if (notes.Count > 0)
            {
                problem = original.GetType().Name + ": " +
                    string.Join(", ", notes.ToArray());
            }

            return copy;
        }

        public static IDictionary<object, object> NewIdentityMap()
        {
            return new Dictionary<object, object>(ObjectCopier.ReferenceComparer.Instance);
        }

        private sealed class PlayerRebindPolicy : ObjectCopier.IFieldPolicy
        {
            private const BindingFlags InstanceFields =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly;

            private readonly PlayerContext _target;

            public PlayerRebindPolicy(PlayerContext target)
            {
                _target = target;
            }

            public bool TryRebind(object value, out object replacement)
            {
                if (value is PlayerEntity)
                {
                    replacement = _target.Player;
                    return true;
                }

                if (value is BodyComp)
                {
                    replacement = _target.Body;
                    return true;
                }

                replacement = null;
                return false;
            }

            /// <summary>
            /// One level down only, and it reports rather than rewrites. The
            /// installed block mods hold nothing but player references, collision
            /// queries, settings and primitives, so this finds nothing today - it
            /// is here so a mod released tomorrow that nests a player reference
            /// shows up in the log instead of quietly driving the wrong player.
            /// </summary>
            public string Inspect(object value)
            {
                if (value == null)
                {
                    return null;
                }

                Type type = value.GetType();
                if (type.IsPrimitive || type.IsEnum || value is string || value is Type)
                {
                    return null;
                }

                for (Type level = type; level != null && level != typeof(object);
                    level = level.BaseType)
                {
                    FieldInfo[] fields;
                    try
                    {
                        fields = level.GetFields(InstanceFields);
                    }
                    catch
                    {
                        return null;
                    }

                    for (int i = 0; i < fields.Length; i++)
                    {
                        Type fieldType = fields[i].FieldType;
                        if (typeof(PlayerEntity).IsAssignableFrom(fieldType) ||
                            typeof(BodyComp).IsAssignableFrom(fieldType))
                        {
                            return "holds a player reference";
                        }
                    }
                }

                return null;
            }
        }
    }
}
