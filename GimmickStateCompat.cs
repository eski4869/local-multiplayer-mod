using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using EntityComponent;
using HarmonyLib;
using JumpKing.Level;
using JumpKing.Player;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Makes the parts of SwitchBlocks state that describe a player resolve to
    /// the player currently being processed. The actual switch state remains
    /// shared: in shared-world mode, either player changes the same level.
    /// </summary>
    internal static class GimmickStateCompat
    {
        private sealed class ScopedType
        {
            public string TypeName;
            public string[] PlayerOwned;
            public string[] PlayerOwnedCombined;
        }

        private static readonly ScopedType[] Targets =
        {
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataAuto",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new[] { "CanSwitchSafely" }
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataBasic",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new string[0]
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataCountdown",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new[] { "CanSwitchSafely" }
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataGroup",
                PlayerOwned = new[] { "HasSwitched", "Touched" },
                PlayerOwnedCombined = new string[0]
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataJump",
                PlayerOwned = new[] { "CooldownTick" },
                PlayerOwnedCombined = new[] { "CanSwitchSafely" }
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataSand",
                PlayerOwned = new[] { "HasSwitched", "HasEntered" },
                PlayerOwnedCombined = new string[0]
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataSequence",
                PlayerOwned = new[] { "HasSwitched" },
                PlayerOwnedCombined = new string[0]
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Data.DataThreshold",
                PlayerOwned = new string[0],
                PlayerOwnedCombined = new[] { "CanSwitchSafely" }
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Behaviours.Dummy.BehaviourPost",
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
                PlayerOwnedCombined = new string[0]
            },
            new ScopedType
            {
                TypeName = "SwitchBlocks.Behaviours.Dummy.BehaviourConveyor",
                PlayerOwned = new[]
                {
                    "IsPlayerOnConveyor",
                    "WasPlayerOnConveyor",
                    "ConveyorBlock",
                    "ConveyorPrevPosition"
                },
                PlayerOwnedCombined = new string[0]
            }
        };

        private static readonly List<string> Missing = new List<string>();
        private static readonly HashSet<string> Combined = new HashSet<string>();

        private static Func<object> _groupData;
        private static Func<object, HashSet<int>> _groupTouched;

        private static Func<bool> _jumpIsUsed;
        private static Func<object> _jumpData;
        private static Func<object, int> _jumpCooldownTick;
        private static Action<object, int> _setJumpCooldownTick;
        private static Action<object, bool> _setJumpSwitchOnceSafe;
        private static Func<object, bool> _settingsCanJumpInAir;
        private static Func<object, int> _settingsCooldown;
        private static Func<int> _getTick;
        private static Func<InputComponent, InputComponent.State> _getPressedState;
        private static bool _canJumpInAir;
        private static int _jumpCooldown;
        private static bool _installed;

        public static bool IsActive { get; private set; }

        public static void Install(Harmony harmony)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            int patched = 0;

            for (int i = 0; i < Targets.Length; i++)
            {
                ScopedType target = Targets[i];
                Type type = FindType(target.TypeName);
                if (type == null)
                {
                    Missing.Add(target.TypeName + " (type not found)");
                    continue;
                }

                if (target.TypeName == "SwitchBlocks.Data.DataGroup")
                {
                    BindGroup(type);
                }

                for (int j = 0; j < target.PlayerOwned.Length; j++)
                {
                    if (Patch(harmony, type, target.PlayerOwned[j]))
                    {
                        patched++;
                    }
                }

                for (int j = 0; j < target.PlayerOwnedCombined.Length; j++)
                {
                    string name = target.PlayerOwnedCombined[j];
                    Combined.Add(type.FullName + "." + name);
                    if (Patch(harmony, type, name))
                    {
                        patched++;
                    }
                }
            }

            patched += InstallJumpInput(harmony);

            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer SwitchBlocks compatibility: scoped " +
                patched + " targets" +
                (Missing.Count == 0
                    ? "."
                    : "; unavailable targets: " +
                        string.Join(", ", Missing.ToArray()))
            );
        }

        private static bool Patch(Harmony harmony, Type type, string propertyName)
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

            HarmonyMethod getterPrefix = null;
            HarmonyMethod getterPostfix = null;
            HarmonyMethod setterPrefix;

            if (property.PropertyType == typeof(bool))
            {
                getterPrefix = Method("BoolGetterPrefix");
                setterPrefix = Method("BoolSetterPrefix");
            }
            else if (property.PropertyType == typeof(int))
            {
                getterPrefix = Method("IntGetterPrefix");
                setterPrefix = Method("IntSetterPrefix");
            }
            else if (property.PropertyType == typeof(Vector2))
            {
                getterPrefix = Method("VectorGetterPrefix");
                setterPrefix = Method("VectorSetterPrefix");
            }
            else if (property.PropertyType == typeof(IBlock))
            {
                getterPrefix = Method("BlockGetterPrefix");
                setterPrefix = Method("BlockSetterPrefix");
            }
            else if (property.PropertyType == typeof(HashSet<int>))
            {
                // The original must produce its collection first so the first
                // private copy can be seeded from the level's loaded value.
                getterPostfix = Method("IntSetGetterPostfix");
                setterPrefix = Method("IntSetSetterPrefix");
            }
            else
            {
                Missing.Add(
                    type.Name + "." + propertyName + " (" +
                    property.PropertyType.Name + " not handled)"
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

                harmony.Patch(get, getterPrefix, getterPostfix);
                harmony.Patch(set, setterPrefix);
                IsActive = true;
                return true;
            }
            catch (Exception ex)
            {
                Missing.Add(type.Name + "." + propertyName + " (" + ex.Message + ")");
                return false;
            }
        }

        private static void BindGroup(Type dataType)
        {
            try
            {
                _groupData = CompileStaticObjectGetter(dataType, "Instance");
                _groupTouched = CompileGetter<HashSet<int>>(dataType, "Touched");
            }
            catch (Exception ex)
            {
                Missing.Add("DataGroup.Touched clone binding (" + ex.Message + ")");
                _groupData = null;
                _groupTouched = null;
            }
        }

        /// <summary>
        /// Group behaviours cache DataGroup.Touched in a private field. A cloned
        /// behaviour never calls the accessor, so the copier must rebind that one
        /// known field explicitly, including when multiplayer is enabled mid-run.
        /// </summary>
        public static bool TryRebindCloneField(
            FieldInfo field,
            object value,
            PlayerContext target,
            out object replacement
        )
        {
            replacement = null;
            if (field == null || target == null || !(value is HashSet<int>) ||
                field.Name != "<Touched>k__BackingField" ||
                field.DeclaringType == null ||
                (field.DeclaringType.FullName !=
                    "SwitchBlocks.Behaviours.BehaviourGroupLeaving" &&
                 field.DeclaringType.FullName !=
                    "SwitchBlocks.Behaviours.BehaviourGroupDuration") ||
                _groupData == null || _groupTouched == null)
            {
                return false;
            }

            object owner = _groupData();
            HashSet<int> source = _groupTouched(owner);
            HashSet<int> scoped;
            if (!ScopedFieldStore.TryGetOrCreateIntSetFor(
                target,
                owner,
                "Touched",
                source,
                out scoped
            ))
            {
                return false;
            }

            replacement = scoped;
            return true;
        }

        private static int InstallJumpInput(Harmony harmony)
        {
            Type setupType = FindType("SwitchBlocks.Setups.SetupJump");
            Type logicType = FindType("SwitchBlocks.Entities.EntityLogicJump");
            Type settingsType = FindType("SwitchBlocks.Settings.SettingsJump");
            Type dataType = FindType("SwitchBlocks.Data.DataJump");
            Type tickType = FindType("SwitchBlocks.Patches.PatchAchievementManager");

            if (setupType == null || logicType == null || settingsType == null ||
                dataType == null || tickType == null)
            {
                return 0;
            }

            try
            {
                MethodInfo setup = AccessTools.Method(setupType, "Setup");
                MethodInfo update = AccessTools.Method(logicType, "Update");
                MethodInfo tick = AccessTools.Method(tickType, "GetTick");
                MethodInfo pressed = AccessTools.Method(
                    typeof(InputComponent),
                    "GetPressedState"
                );
                if (setup == null || update == null || tick == null || pressed == null)
                {
                    Missing.Add("Jump in-air input methods");
                    return 0;
                }

                _jumpIsUsed = CompileStaticGetter<bool>(setupType, "IsUsed");
                _jumpData = CompileStaticObjectGetter(dataType, "Instance");
                _jumpCooldownTick = CompileGetter<int>(dataType, "CooldownTick");
                _setJumpCooldownTick = CompileSetter<int>(dataType, "CooldownTick");
                _setJumpSwitchOnceSafe =
                    CompileSetter<bool>(dataType, "SwitchOnceSafe");
                _settingsCanJumpInAir =
                    CompileGetter<bool>(settingsType, "CanJumpInAir");
                _settingsCooldown = CompileGetter<int>(settingsType, "Cooldown");
                _getTick = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), tick);
                _getPressedState =
                    (Func<InputComponent, InputComponent.State>)Delegate.CreateDelegate(
                        typeof(Func<InputComponent, InputComponent.State>),
                        pressed
                    );

                harmony.Patch(setup, postfix: Method("JumpSetupPostfix"));
                harmony.Patch(update, prefix: Method("JumpUpdatePrefix"));
                IsActive = true;
                return 2;
            }
            catch (Exception ex)
            {
                Missing.Add("Jump in-air input (" + ex.Message + ")");
                return 0;
            }
        }

        private static void JumpSetupPostfix(object settings)
        {
            if (LevelStartReplay.IsSecondaryPass || settings == null ||
                _jumpIsUsed == null || !_jumpIsUsed())
            {
                return;
            }

            _canJumpInAir = _settingsCanJumpInAir(settings);
            _jumpCooldown = _settingsCooldown(settings);

            object data = _jumpData();
            int initialCooldownTick = _jumpCooldownTick(data);
            for (int number = 2; number <= ModEntry.PlayerCount; number++)
            {
                ScopedFieldStore.SeedFor(
                    MultiplayerRuntime.GetContext(number),
                    data,
                    "CooldownTick",
                    initialCooldownTick
                );
            }
        }

        private static void JumpUpdatePrefix()
        {
            if (!MultiplayerRuntime.IsActive || !_canJumpInAir)
            {
                return;
            }

            object data = _jumpData();
            int currentTick = _getTick();

            for (int number = 2; number <= ModEntry.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || !context.IsAlive || context.Body.IsOnGround)
                {
                    continue;
                }

                object ignored;
                if (!ScopedFieldStore.TryReadFor(
                    context,
                    data,
                    "CooldownTick",
                    out ignored
                ))
                {
                    // A player added after level setup is immediately eligible,
                    // exactly as a newly loaded player with an expired cooldown.
                    ScopedFieldStore.SeedFor(
                        context,
                        data,
                        "CooldownTick",
                        currentTick - _jumpCooldown
                    );
                }

                using (PlayerScope.Enter(context, false, false))
                {
                    InputComponent input = context.Player.GetComponent<InputComponent>();
                    if (input == null ||
                        currentTick < _jumpCooldownTick(data) + _jumpCooldown ||
                        !_getPressedState(input).jump)
                    {
                        continue;
                    }

                    _setJumpSwitchOnceSafe(data, true);
                    _setJumpCooldownTick(data, currentTick);
                }
            }
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type type = assemblies[i].GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // A partially loaded optional mod does not stop the search.
                }
            }

            return null;
        }

        private static Func<object> CompileStaticObjectGetter(
            Type type,
            string propertyName
        )
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            );
            Expression body = Expression.Convert(
                Expression.Property(null, property),
                typeof(object)
            );
            return Expression.Lambda<Func<object>>(body).Compile();
        }

        private static Func<T> CompileStaticGetter<T>(
            Type type,
            string propertyName
        )
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            );
            return Expression.Lambda<Func<T>>(
                Expression.Property(null, property)
            ).Compile();
        }

        private static Func<object, T> CompileGetter<T>(
            Type type,
            string propertyName
        )
        {
            ParameterExpression instance = Expression.Parameter(typeof(object));
            Expression body = Expression.Property(
                Expression.Convert(instance, type),
                propertyName
            );
            return Expression.Lambda<Func<object, T>>(body, instance).Compile();
        }

        private static Action<object, T> CompileSetter<T>(
            Type type,
            string propertyName
        )
        {
            ParameterExpression instance = Expression.Parameter(typeof(object));
            ParameterExpression value = Expression.Parameter(typeof(T));
            Expression body = Expression.Assign(
                Expression.Property(Expression.Convert(instance, type), propertyName),
                value
            );
            return Expression.Lambda<Action<object, T>>(
                body,
                instance,
                value
            ).Compile();
        }

        private static object OwnerOf(object instance, MethodBase accessor)
        {
            return instance ?? accessor.DeclaringType;
        }

        private static string PropertyNameOf(MethodBase accessor)
        {
            return accessor.Name.Substring(4);
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

        private static bool IntGetterPrefix(
            object __instance,
            ref int __result,
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

            __result = (int)value;
            return false;
        }

        private static bool IntSetterPrefix(
            object __instance,
            int value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                value
            );
        }

        private static bool VectorGetterPrefix(
            object __instance,
            ref Vector2 __result,
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

            __result = (Vector2)value;
            return false;
        }

        private static bool VectorSetterPrefix(
            object __instance,
            Vector2 value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                value
            );
        }

        private static bool BlockGetterPrefix(
            object __instance,
            ref IBlock __result,
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

            __result = (IBlock)value;
            return false;
        }

        private static bool BlockSetterPrefix(
            object __instance,
            IBlock value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                value
            );
        }

        private static void IntSetGetterPostfix(
            object __instance,
            ref HashSet<int> __result,
            MethodBase __originalMethod
        )
        {
            HashSet<int> scoped;
            if (ScopedFieldStore.TryGetOrCreateIntSet(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                __result,
                out scoped
            ))
            {
                __result = scoped;
            }
        }

        private static bool IntSetSetterPrefix(
            object __instance,
            HashSet<int> value,
            MethodBase __originalMethod
        )
        {
            return !ScopedFieldStore.TryWrite(
                OwnerOf(__instance, __originalMethod),
                PropertyNameOf(__originalMethod),
                value
            );
        }
    }
}
