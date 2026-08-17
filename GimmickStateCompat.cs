using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Makes named fields of a gimmick mod's state singleton resolve per player.
    ///
    /// The mods this targets are not badly written. Keeping switch state in one
    /// place is correct while there is one player, and it stayed correct until
    /// this mod put a second one in the level. Fixing it is therefore our work,
    /// not a defect to report upstream.
    ///
    /// The whole compatibility layer is a table. Which fields belong to a player
    /// rather than to the level is the only thing that has to be known, and the
    /// two interaction modes differ only in how much of the table is used:
    /// <c>Shared</c> scopes what describes a player, <c>Independent</c> scopes
    /// everything, giving each player their own switch state.
    ///
    /// Targets are matched by name. SwitchBlocks is used by many published maps,
    /// so its type and property names are effectively stable; if a rename does
    /// happen, the miss is reported at startup and this layer stays off rather
    /// than half-applied.
    /// </summary>
    internal static class GimmickStateCompat
    {
        private sealed class ScopedType
        {
            public string TypeName;

            /// <summary>
            /// Per-player, and only ever read from inside a player's own pass.
            /// Outside a scope the mod's own field answers.
            /// </summary>
            public string[] PlayerOwned;

            /// <summary>
            /// Per-player, but also read from outside any player - by a logic
            /// entity deciding something for the level as a whole. Those reads
            /// get every player's value combined with AND, which is what
            /// "is it safe" means once there is more than one of them.
            /// </summary>
            public string[] PlayerOwnedCombined;

            /// <summary>Per-player only when worlds are independent.</summary>
            public string[] LevelOwned;

            /// <summary>
            /// The members are static rather than instance. The type itself
            /// stands in for the owner, since there is no instance to key on.
            /// </summary>
            public bool Static;
        }

        /// <summary>
        /// Only Sand has been traced end to end. The other seven switch types are
        /// built the same way and are expected to need the same treatment, but
        /// guessing their field roles would be worse than leaving them alone -
        /// scoping a field that really is level state would break a switch that
        /// currently works.
        /// </summary>
        private static readonly ScopedType[] Targets =
        {
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataSand",
                PlayerOwned = new[] { "HasSwitched", "HasEntered" },
                PlayerOwnedCombined = new string[0],
                LevelOwned = new[] { "State", "Progress", "ProgressUnclamped" }
            },

            // Auto, Countdown and Jump share one protocol, and the mod applies it
            // to all three in the same two places: BehaviourPre sets
            // CanSwitchSafely at the start of a player's block pass, and the slope
            // patch clears it when that player is standing where a switch would
            // trap them. Both are per player. Their logic entities then read it
            // once a frame from outside any player, which is the combined read.
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataCountdown",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new[] { "CanSwitchSafely" },
                LevelOwned = new[] { "State", "Progress", "ProgressUnclamped" }
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataAuto",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new[] { "CanSwitchSafely" },
                LevelOwned = new[] { "State", "Progress", "ProgressUnclamped" }
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataJump",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new[] { "CanSwitchSafely" },
                LevelOwned = new[] { "State", "Progress", "ProgressUnclamped" }
            },

            // Eight statics describing one player, on a behaviour that is
            // registered per body. BehaviourPre clears them at the start of a
            // player's pass and that player's collisions set them, so with two
            // players the second pass overwrites the first and both then read
            // whichever finished last.
            //
            // PrevVelocity is the one that shows: the lever asks which side it
            // was struck from by looking at the velocity from the previous
            // frame, and with the wrong player's velocity a ceiling struck from
            // below does not read as struck from below, so the lever never fires.
            new ScopedType
            {
                TypeName = "SwitchBlocks.Behaviours.Dummy.BehaviourPost",
                Static = true,
                PlayerOwned = new[]
                {
                    "PrevVelocity",
                    "IsPlayerOnIce",
                    "IsPlayerOnSnow",
                    "IsPlayerOnWater",
                    "IsPlayerOnTypeSand",
                    "IsPlayerOnTypeSandUp",
                    "IsPlayerOnMoveUp",
                    "IsPlayerOnInfinityJump"
                },
                PlayerOwnedCombined = new string[0],
                LevelOwned = new string[0]
            }
        };

        private static readonly List<string> Missing = new List<string>();

        /// <summary>
        /// Type-qualified names whose out-of-scope reads combine every player's
        /// value rather than falling through to the mod's own field.
        /// </summary>
        private static readonly HashSet<string> Combined = new HashSet<string>();

        private static bool _installed;

        public static bool IsActive { get; private set; }

        /// <summary>
        /// Runs once, after mod assemblies are loaded. A mod that is not installed
        /// is not an error - most maps use none of these.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;

            var getter = new HarmonyMethod(
                typeof(GimmickStateCompat).GetMethod(
                    "BoolGetterPrefix",
                    BindingFlags.NonPublic | BindingFlags.Static
                )
            );
            var setter = new HarmonyMethod(
                typeof(GimmickStateCompat).GetMethod(
                    "BoolSetterPrefix",
                    BindingFlags.NonPublic | BindingFlags.Static
                )
            );

            int patched = 0;

            for (int i = 0; i < Targets.Length; i++)
            {
                ScopedType target = Targets[i];
                Type type = FindType(target.TypeName);
                if (type == null)
                {
                    // Silence here is how a typo in the type name became a layer
                    // that installed nothing and reported nothing. A mod that is
                    // simply not installed reaches the same branch, so the message
                    // says which case it cannot tell apart rather than claiming a
                    // fault.
                    Missing.Add(target.TypeName + " (type not found)");
                    continue;
                }

                for (int j = 0; j < target.PlayerOwned.Length; j++)
                {
                    if (Patch(harmony, type, target.PlayerOwned[j], getter, setter))
                    {
                        patched++;
                    }
                }

                for (int j = 0; j < target.PlayerOwnedCombined.Length; j++)
                {
                    string name = target.PlayerOwnedCombined[j];
                    Combined.Add(type.FullName + "." + name);
                    if (Patch(harmony, type, name, getter, setter))
                    {
                        patched++;
                    }
                }
            }

            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer gimmick state: scoped " + patched +
                " properties" +
                (Missing.Count == 0
                    ? "."
                    : "; not scoped, so these stay shared between players: " +
                        string.Join(", ", Missing.ToArray()))
            );
        }

        private static bool Patch(
            Harmony harmony,
            Type type,
            string propertyName,
            HarmonyMethod getter,
            HarmonyMethod setter
        )
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static
            );

            if (property == null)
            {
                Missing.Add(type.Name + "." + propertyName);
                return false;
            }

            // One prefix pair per property type: Harmony's __result has to be
            // declared as the real return type, so bool and Vector2 cannot share.
            HarmonyMethod typedGetter;
            HarmonyMethod typedSetter;
            if (property.PropertyType == typeof(bool))
            {
                typedGetter = getter;
                typedSetter = setter;
            }
            else if (property.PropertyType == typeof(Microsoft.Xna.Framework.Vector2))
            {
                typedGetter = Method("VectorGetterPrefix");
                typedSetter = Method("VectorSetterPrefix");
            }
            else
            {
                Missing.Add(
                    type.Name + "." + propertyName +
                    " (" + property.PropertyType.Name + " not handled)"
                );
                return false;
            }

            try
            {
                MethodInfo get = property.GetGetMethod(true);
                MethodInfo set = property.GetSetMethod(true);
                if (get == null || set == null)
                {
                    Missing.Add(type.Name + "." + propertyName);
                    return false;
                }

                harmony.Patch(get, typedGetter);
                harmony.Patch(set, typedSetter);
                IsActive = true;
                return true;
            }
            catch (Exception ex)
            {
                Missing.Add(type.Name + "." + propertyName + " (" + ex.Message + ")");
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type;
                try
                {
                    type = assemblies[i].GetType(fullName, false);
                }
                catch
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>
        /// One pair of prefixes serves every scoped property of the same type,
        /// because <c>__originalMethod</c> says which one is being read.
        /// Returning true lets the mod's own accessor run, which is what happens
        /// in single player and on a player's first read.
        /// </summary>
        /// <summary>
        /// A static member has no instance to key on, so its declaring type
        /// stands in as the owner. Instance members key on the instance, which is
        /// what keeps a replaced singleton from inheriting the previous one's
        /// values.
        /// </summary>
        private static object OwnerOf(object instance, MethodBase accessor)
        {
            return instance ?? accessor.DeclaringType;
        }

        private static HarmonyMethod Method(string name)
        {
            return new HarmonyMethod(
                typeof(GimmickStateCompat).GetMethod(
                    name,
                    BindingFlags.NonPublic | BindingFlags.Static
                )
            );
        }

        private static bool VectorGetterPrefix(
            object __instance,
            ref Microsoft.Xna.Framework.Vector2 __result,
            MethodBase __originalMethod
        )
        {
            object value;
            if (!ScopedFieldStore.TryRead(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                out value
            ))
            {
                return true;
            }

            __result = (Microsoft.Xna.Framework.Vector2)value;
            return false;
        }

        private static bool VectorSetterPrefix(
            object __instance,
            Microsoft.Xna.Framework.Vector2 value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                value
            );
        }

        private static bool BoolGetterPrefix(
            object __instance,
            ref bool __result,
            MethodBase __originalMethod
        )
        {
            string name = PropertyNameOf(__originalMethod);
            object owner = OwnerOf(__instance, __originalMethod);

            object value;
            if (ScopedFieldStore.TryRead(owner, name, out value))
            {
                __result = (bool)value;
                return false;
            }

            // No player is being processed. For a value the mod consults on
            // behalf of the whole level, answer for all of them at once.
            Type declaring = __instance == null
                ? __originalMethod.DeclaringType
                : __instance.GetType();

            if (Combined.Contains(declaring.FullName + "." + name))
            {
                bool all;
                if (ScopedFieldStore.TryReadAll(owner, name, out all))
                {
                    __result = all;
                    return false;
                }
            }

            return true;
        }

        private static bool BoolSetterPrefix(
            object __instance,
            bool value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                value
            );
        }

        private static string PropertyNameOf(MethodBase accessor)
        {
            // "get_HasSwitched" / "set_HasSwitched"
            return accessor.Name.Substring(4);
        }
    }
}
