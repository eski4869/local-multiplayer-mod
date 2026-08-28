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
    /// Makes the parts of a gimmick mod's state that describe a player resolve to
    /// the player currently being processed. World state remains shared: in
    /// shared-world mode, either player changes the same level.
    ///
    /// Two shapes are handled, and they are not variations of one mechanism.
    /// <see cref="Targets" /> covers a value that each player needs a separate
    /// copy of, so the copies are kept in <see cref="ScopedFieldStore" />.
    /// <see cref="PlayerReferences" /> covers a mod caching "the player" itself,
    /// where there is nothing to store: the answer is always the scoped player.
    /// </summary>
    internal static class GimmickStateCompat
    {
        private sealed class ScopedType
        {
            public string TypeName;
            public string[] PlayerOwned;
            public string[] PlayerOwnedCombined;
        }

        /// <summary>
        /// A static property holding the one <see cref="PlayerEntity" /> a mod
        /// found at level start, which its Harmony patches then consult instead of
        /// the player whose frame they are running in.
        /// </summary>
        private sealed class PlayerReference
        {
            public string TypeName;
            public string PropertyName;
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
            },

            // UpsideDownCore is a shared library several gimmick mods build on
            // (JumpKing-UpsideDownBlocks among them). Its own block behaviour
            // writes these two properties from inside a player's own block pass,
            // exactly like the SwitchBlocks fields above - so they scope the same
            // way. What is different is that a second component, Manager, copies
            // both into its own plain static fields once per game tick rather
            // than reading Controller directly each time; that copy is handled
            // separately in ResyncUpsideDown below, since a field has no accessor
            // to redirect.
            new ScopedType
            {
                TypeName = "UpsideDownCore.Controller",
                PlayerOwned = new[] { "isReverseGravity", "upsideDownType" },
                PlayerOwnedCombined = new string[0]
            },

            // Movement Control Blocks keeps one DataMomentumStop for the whole
            // game: OnLevelStart registers BehaviourMomentumStopScreen(Data) on
            // the first player it finds, and BehaviourCloner hands player 2 a
            // copy of that behaviour - a copy carrying the same Data reference,
            // so one box holds both players' values.
            //
            // Screen is a latch rather than configuration, despite the name. The
            // behaviour writes Camera.CurrentScreen when the player is stopped on
            // the block and writes -1 on every frame the player is not on it,
            // which is what makes the stop happen once per screen entry. With two
            // players the one standing off the block clears the other's latch
            // every frame, so the stop repeats instead. PlayerOwned, not
            // PlayerOwnedCombined: there is no sensible way to fold two players'
            // screen numbers into one answer.
            //
            // Both readers already sit inside a scope - the behaviour runs in the
            // player's own block pass, and BlockMomentumStopScreenSolid's
            // canBlockPlayer reads ModEntry.Data.Screen during collision
            // resolution, which the per-entity update scope covers. SaveToFile at
            // level end does not, so the file keeps the unscoped value; a stale
            // latch there costs one extra stop after a reload.
            new ScopedType
            {
                TypeName = "MovementControl.Data.DataMomentumStop",
                PlayerOwned = new[] { "Screen" },
                PlayerOwnedCombined = new string[0]
            }
        };

        /// <summary>
        /// JumpKing-Expansion-Blocks caches <c>EntityManager.Find&lt;PlayerEntity&gt;()</c>
        /// into a static at level start, and twelve Harmony patches across nine
        /// files read it. Every one of them patches a base-game method that runs
        /// once per player per frame - <c>Walk.MyRun</c>, <c>JumpState.MyRun</c>,
        /// <c>ApplyGravityBehaviour.ExecuteBehaviour</c>,
        /// <c>BodyComp.GetMultipliers</c> among them - so the effect is decided
        /// from one player's body and then applied to whichever player's frame is
        /// running.
        ///
        /// The visible symptom was a RevokeWalking block: one player standing on
        /// it took left and right away from both, and the other player standing on
        /// it did nothing at all. Two of these patches also postfix
        /// <c>SkinManager.IsWearingSkin</c> and
        /// <c>InventoryManager.HasItemEnabled</c>, which this mod already scopes
        /// with a prefix - and a postfix runs even when a prefix skipped the
        /// original, so those two were overwriting the per-player answer with
        /// player 1's block contact.
        ///
        /// Redirecting the getter fixes all twelve at once, because every read is
        /// asking the same question this scope already answers.
        /// </summary>
        private static readonly PlayerReference[] PlayerReferences =
        {
            new PlayerReference
            {
                TypeName = "JumpKing_Expansion_Blocks.ModEntry",
                PropertyName = "Player"
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
            patched += InstallPlayerReferences(harmony);
            patched += InstallTeleportScreen(harmony);
            InstallUpsideDownResync();

            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer gimmick compatibility: scoped " +
                patched + " targets" +
                (Missing.Count == 0
                    ? "."
                    : "; unavailable targets: " +
                        string.Join(", ", Missing.ToArray()))
            );
        }

        /// <summary>
        /// Redirects each cached-player static to the scoped player. Nothing is
        /// stored per player: the scope already knows whose frame this is, so the
        /// getter can answer directly.
        /// </summary>
        private static int InstallPlayerReferences(Harmony harmony)
        {
            int patched = 0;

            for (int i = 0; i < PlayerReferences.Length; i++)
            {
                PlayerReference target = PlayerReferences[i];
                Type type = FindType(target.TypeName);
                if (type == null)
                {
                    // Reported rather than skipped in silence. A mod this map does
                    // not use and a typo in the name above are indistinguishable
                    // from the outside, and telling them apart afterwards has
                    // already cost two rounds of testing once.
                    Missing.Add(target.TypeName + " (type not found)");
                    continue;
                }

                PropertyInfo property = type.GetProperty(
                    target.PropertyName,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static
                );
                if (property == null ||
                    property.PropertyType != typeof(PlayerEntity))
                {
                    Missing.Add(
                        target.TypeName + "." + target.PropertyName +
                        " (not a static PlayerEntity property)"
                    );
                    continue;
                }

                MethodInfo get = property.GetGetMethod(true);
                if (get == null)
                {
                    Missing.Add(
                        target.TypeName + "." + target.PropertyName +
                        " (no getter)"
                    );
                    continue;
                }

                try
                {
                    harmony.Patch(get, Method("ScopedPlayerGetterPrefix"));
                    IsActive = true;
                    patched++;
                }
                catch (Exception ex)
                {
                    Missing.Add(
                        target.TypeName + "." + target.PropertyName +
                        " (" + ex.Message + ")"
                    );
                }
            }

            return patched;
        }

        private static Func<bool> _teleportFixEnabled;

        /// <summary>
        /// Gives the screen teleport back the player it is actually moving.
        ///
        /// TeleportBugFix transpiles <c>HandlePlayerTeleportBehaviour.ExecuteBehaviour</c>
        /// and replaces the base game's "which screen is this body on" step -
        /// originally computed from <c>behaviourContext.BodyComp</c> - with a call
        /// to its own <c>GetIndex0()</c>. That method has two paths, and the
        /// fallback reads <c>GameLoop.m_player</c>, which is always player 1:
        ///
        /// <code>
        /// if (contentManager?.level != null &amp;&amp; isPatch) return Camera.CurrentScreen;
        /// return (int)((0f - (GameLoop.m_player.m_body.Position.Y - 360f)) / 360f);
        /// </code>
        ///
        /// The teleport then moves the player by
        /// <c>360 * (thatScreen - linkTarget)</c>. Once player 1 has already
        /// teleported, its position gives the destination screen, the difference
        /// comes out zero, and every later player is wrapped horizontally without
        /// changing screen - stranded on the origin screen while the camera has
        /// moved on. Reversing who goes first makes it work, because player 1 has
        /// not moved yet.
        ///
        /// Only the fallback is redirected. The <c>isPatch</c> path reads
        /// <c>Camera.CurrentScreen</c>, which <see cref="PlayerScope" /> already
        /// installs per player, and it is that mod's deliberate fix for maps
        /// tagged <c>PatchTeleportBug</c> - overriding it would undo their work on
        /// exactly the maps that asked for it.
        ///
        /// <c>GameLoop.m_player</c> is a plain static field, so it cannot be
        /// redirected the way <see cref="PlayerReferences" /> handles a property.
        /// The method that reads it can be.
        /// </summary>
        private static int InstallTeleportScreen(Harmony harmony)
        {
            Type patchType = FindType(
                "TeleportBugFix.Patching.HandlePlayerTeleportBehaviour"
            );
            if (patchType == null)
            {
                Missing.Add(
                    "TeleportBugFix.Patching.HandlePlayerTeleportBehaviour " +
                    "(type not found)"
                );
                return 0;
            }

            MethodInfo getIndex = AccessTools.Method(patchType, "GetIndex0");
            if (getIndex == null)
            {
                Missing.Add("TeleportBugFix.GetIndex0 (method not found)");
                return 0;
            }

            Type modType = FindType("TeleportBugFix.TeleportBugFix");
            if (modType == null)
            {
                Missing.Add("TeleportBugFix.TeleportBugFix (type not found)");
            }
            else
            {
                try
                {
                    _teleportFixEnabled =
                        CompileStaticGetter<bool>(modType, "isPatch");
                }
                catch (Exception ex)
                {
                    Missing.Add("TeleportBugFix.isPatch (" + ex.Message + ")");
                }
            }

            try
            {
                harmony.Patch(getIndex, Method("TeleportScreenPrefix"));
                IsActive = true;
                return 1;
            }
            catch (Exception ex)
            {
                Missing.Add("TeleportBugFix.GetIndex0 (" + ex.Message + ")");
                return 0;
            }
        }

        private static bool TeleportScreenPrefix(ref int __result)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null || context.Body == null)
            {
                return true;
            }

            // Their own fix resolves through the camera, which is already this
            // player's camera inside the scope. Nothing to correct there.
            //
            // When the flag could not be bound at all, redirecting is the safer
            // default: it restores what the base game did before the transpile,
            // and the startup report names the binding that failed.
            if (_teleportFixEnabled != null && _teleportFixEnabled())
            {
                return true;
            }

            __result = TeleportScreenOf(context.Body.Position.Y);
            return false;
        }

        /// <summary>
        /// Deliberately the same expression the transpiled method uses, rather
        /// than the <c>-floor(y / 360)</c> form used elsewhere. The two disagree
        /// on an exact screen boundary, and matching the caller is what keeps this
        /// a redirection of <em>which player</em> rather than a change of
        /// behaviour.
        /// </summary>
        private static int TeleportScreenOf(float y)
        {
            return (int)((0f - (y - 360f)) / 360f);
        }

        /// <summary>
        /// Falls through to the mod's own static outside a scope, which is what
        /// single player always is: <c>PlayerScope.Current</c> is null there by
        /// construction, so this is inert rather than conditionally inert.
        /// </summary>
        private static bool ScopedPlayerGetterPrefix(ref PlayerEntity __result)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null || context.Player == null)
            {
                return true;
            }

            __result = context.Player;
            return false;
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
            else if (property.PropertyType.IsEnum)
            {
                // Harmony boxes a value-type __result/parameter into object on
                // request, which covers any enum without a type-specific pair -
                // useful here since UpsideDownCore's enum cannot be referenced
                // at compile time from this assembly.
                getterPrefix = Method("EnumGetterPrefix");
                setterPrefix = Method("EnumSetterPrefix");
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
            if (settings == null || _jumpIsUsed == null || !_jumpIsUsed())
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

        private static Action _upsideDownResync;

        /// <summary>
        /// UpsideDownCore.Manager caches gravity direction into its own plain
        /// static fields exactly once per game tick, via a prefix on
        /// <c>JumpGame.Update</c> that runs before any player's physics for that
        /// frame. That single copy is shared by every downstream consumer:
        /// gravity, camera screen crossing, a jump-direction transpile, at least
        /// one bug-fix behaviour. With two players, whichever one's block
        /// behaviour last wrote <c>Controller.isReverseGravity</c> decides
        /// gravity for both of them that frame, because <c>ApplyGravityBehaviour</c>
        /// runs before <c>ExecuteBlockBehaviours</c> in a body's own behaviour
        /// order (confirmed from the base game's registration list) - so a
        /// player's gravity this frame is resolved from whatever the other
        /// player's collision state happened to be, not their own.
        ///
        /// Scoping <c>Controller</c>'s two properties above fixes what block
        /// mods read directly, but <c>Manager</c>'s fields are a snapshot taken
        /// once before that scoping ever applies for the frame. Re-running the
        /// same copy step ourselves, right after installing each player's own
        /// scope, refreshes it from that player's own value before their physics
        /// runs - restoring the one-player-only assumption the mod was written
        /// under, for whichever player is currently being processed.
        /// </summary>
        private static void InstallUpsideDownResync()
        {
            Type managerType = FindType("UpsideDownCore.Models.Manager");
            if (managerType == null)
            {
                return;
            }

            try
            {
                MethodInfo update = AccessTools.Method(managerType, "Update");
                if (update == null)
                {
                    Missing.Add("UpsideDownCore.Manager.Update");
                    return;
                }

                _upsideDownResync = (Action)Delegate.CreateDelegate(
                    typeof(Action),
                    update
                );
                IsActive = true;
            }
            catch (Exception ex)
            {
                Missing.Add("UpsideDownCore resync (" + ex.Message + ")");
            }
        }

        public static void ResyncUpsideDown()
        {
            if (_upsideDownResync != null)
            {
                _upsideDownResync();
            }
        }

        private static Func<bool> _readIsReverseGravity;
        private static Func<object> _readUpsideDownType;
        private static Func<bool> _readManagerIsReverseGravity;

        /// <summary>
        /// <c>Manager.isUpsideDown</c> is the field that actually decides the
        /// camera's screen crossing, the collision comparisons, the walk and jump
        /// direction and the sprite flip - fourteen call sites against
        /// <c>isReverseGravity</c>'s two. Reading only the latter answered the
        /// wrong question.
        ///
        /// <c>gravity</c> is only consulted in <c>Auto</c> mode, and is written
        /// per player from inside each body's own pass, so it is the one Manager
        /// field the resync cannot put right on its own.
        /// </summary>
        private static Func<bool> _readManagerIsUpsideDown;
        private static Func<float> _readManagerGravity;

        /// <summary>
        /// True once binding has been attempted, success or failure. Without
        /// this, a map that never loads UpsideDownCore made every frame, for
        /// every player, re-scan every loaded assembly by reflection looking for
        /// a type that was never going to appear - cheap to describe, and heavy
        /// enough in practice to be indistinguishable from a hang.
        /// </summary>
        private static bool _upsideDownDescribeBound;
        private static string _upsideDownDescribeFailure;

        /// <summary>
        /// Diagnostic only. Reads exactly what ApplyGravityBehaviour would see at
        /// this instant, through the same patched accessors, plus Manager's own
        /// field directly (unpatched, since it is a field not a property) so a
        /// resync failure and a read failure can be told apart.
        /// </summary>
        public static string DescribeUpsideDownState()
        {
            if (!_upsideDownDescribeBound)
            {
                _upsideDownDescribeBound = true;

                Type controllerType = FindType("UpsideDownCore.Controller");
                Type managerType = FindType("UpsideDownCore.Models.Manager");
                if (controllerType == null || managerType == null)
                {
                    _upsideDownDescribeFailure = "controller/manager type not found";
                }
                else
                {
                    try
                    {
                        _readIsReverseGravity = CompileStaticGetter<bool>(
                            controllerType,
                            "isReverseGravity"
                        );
                        _readUpsideDownType =
                            CompileStaticObjectGetter(controllerType, "upsideDownType");
                        _readManagerIsReverseGravity = CompileStaticFieldGetter<bool>(
                            managerType,
                            "isReverseGravity"
                        );
                        _readManagerIsUpsideDown = CompileStaticFieldGetter<bool>(
                            managerType,
                            "isUpsideDown"
                        );
                        _readManagerGravity = CompileStaticFieldGetter<float>(
                            managerType,
                            "gravity"
                        );
                    }
                    catch (Exception ex)
                    {
                        _upsideDownDescribeFailure = "bind failed: " + ex.Message;
                    }
                }
            }

            if (_upsideDownDescribeFailure != null)
            {
                return _upsideDownDescribeFailure;
            }

            return "Controller.isReverseGravity=" + _readIsReverseGravity() +
                " Controller.upsideDownType=" + _readUpsideDownType() +
                " Manager.isReverseGravity=" + _readManagerIsReverseGravity() +
                " Manager.isUpsideDown=" + _readManagerIsUpsideDown() +
                " Manager.gravity=" + _readManagerGravity() +
                " scope=" + (PlayerScope.Current == null
                    ? "none"
                    : PlayerScope.Current.Number.ToString());
        }

        private static Func<T> CompileStaticFieldGetter<T>(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            );
            return Expression.Lambda<Func<T>>(Expression.Field(null, field)).Compile();
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

        private static bool EnumGetterPrefix(
            object __instance,
            ref object __result,
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

            __result = value;
            return false;
        }

        private static bool EnumSetterPrefix(
            object __instance,
            object value,
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
