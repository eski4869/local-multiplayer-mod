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

            /// <summary>Per-player in every mode.</summary>
            public string[] PlayerOwned;

            /// <summary>Per-player only when worlds are independent.</summary>
            public string[] LevelOwned;
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
                TypeName = "SwitchBlocks.DataSand",
                PlayerOwned = new[] { "HasSwitched", "HasEntered" },
                LevelOwned = new[] { "State", "Progress", "ProgressUnclamped" }
            }
        };

        private static readonly List<string> Missing = new List<string>();
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

            for (int i = 0; i < Targets.Length; i++)
            {
                ScopedType target = Targets[i];
                Type type = FindType(target.TypeName);
                if (type == null)
                {
                    continue;
                }

                for (int j = 0; j < target.PlayerOwned.Length; j++)
                {
                    Patch(harmony, type, target.PlayerOwned[j], getter, setter);
                }
            }

            if (Missing.Count > 0)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer could not scope gimmick state, so those " +
                    "switches stay shared between players: " +
                    string.Join(", ", Missing.ToArray())
                );
            }
        }

        private static void Patch(
            Harmony harmony,
            Type type,
            string propertyName,
            HarmonyMethod getter,
            HarmonyMethod setter
        )
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (property == null || property.PropertyType != typeof(bool))
            {
                Missing.Add(type.Name + "." + propertyName);
                return;
            }

            try
            {
                MethodInfo get = property.GetGetMethod(true);
                MethodInfo set = property.GetSetMethod(true);
                if (get == null || set == null)
                {
                    Missing.Add(type.Name + "." + propertyName);
                    return;
                }

                harmony.Patch(get, getter);
                harmony.Patch(set, setter);
                IsActive = true;
            }
            catch (Exception ex)
            {
                Missing.Add(type.Name + "." + propertyName + " (" + ex.Message + ")");
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
        private static bool BoolGetterPrefix(
            object __instance,
            ref bool __result,
            MethodBase __originalMethod
        )
        {
            object value;
            if (!ScopedFieldStore.TryRead(
                __instance,
                PropertyNameOf(__originalMethod),
                out value
            ))
            {
                return true;
            }

            __result = (bool)value;
            return false;
        }

        private static bool BoolSetterPrefix(
            object __instance,
            bool value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                __instance,
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
