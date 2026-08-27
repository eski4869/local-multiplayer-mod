using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using EntityComponent;
using HarmonyLib;
using JumpKing;
using JumpKing.API;
using JumpKing.GameManager.MultiEnding;
using JumpKing.Level;
using JumpKing.MiscEntities.WorldItems.Inventory;
using JumpKing.Mods;
using BehaviorTree;
using JumpKing.Controller;
using JumpKing.PauseMenu;
using JumpKing.PauseMenu.BT.Actions;
using Microsoft.Xna.Framework;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    [JumpKingMod("eski4869.LocalMultiplayerMod")]
    public static class ModEntry
    {
        private const string SettingsFileName =
            "eski4869.LocalMultiplayerMod.Settings.xml";
        internal const string CommandTarget = "local_multiplayer";

        private static Harmony _harmony;
        private static LocalMultiplayerPreferences _preferences;
        private static UserCommandRouter _userRouter;
        private static string _settingsPath;

        [BeforeLevelLoad]
        public static void BeforeLevelLoad()
        {
            EnsurePreferencesLoaded();
            EnsurePatched();
            BrokerCommandClient.Register(CommandTarget);
        }

        [OnLevelStart]
        public static void OnLevelStart()
        {
            EnsurePreferencesLoaded();
            EnsurePatched();
            BrokerCommandClient.Register(CommandTarget);
            MultiplayerRuntime.OnLevelStart();

            // A joiner waiting on the right level finds out here, and a host who
            // changed level updates what joiners are told to load.
            Netplay.OnLevelStarted();
        }

        [OnLevelEnd]
        public static void OnLevelEnd()
        {
            MultiplayerRuntime.OnLevelEnd();
        }

        [OnLevelUnload]
        public static void OnLevelUnload()
        {
            MultiplayerRuntime.OnLevelEnd();
        }

        internal static int PlayerCount
        {
            get
            {
                if (_netplayPlayerCount > 0)
                {
                    return _netplayPlayerCount;
                }

                EnsurePreferencesLoaded();
                return _preferences.PlayerCount;
            }
        }

        /// <summary>
        /// The player count a netplay session imposes while it lasts, or zero.
        ///
        /// A session is a thing that starts and ends; the preference is what the
        /// player chose for this machine. Netplay used to express itself by writing
        /// the preference, which saves to disk - so closing the window with the
        /// × during a session left two players configured, and the game came back
        /// in offline two-player next launch. There is no teardown that can fix
        /// that, because closing the window does not run one.
        ///
        /// So the session never touches what the player chose. It overrides it for
        /// as long as it is running, the override lives only in memory, and
        /// whatever they had picked is still there when it ends - including when it
        /// "ends" by the process going away.
        /// </summary>
        private static int _netplayPlayerCount;

        private static TwoPlayerLayout _netplayLayout;

        /// <summary>
        /// Imposes a player count for the duration of a netplay session, or lifts
        /// it again with a count of zero. Never saved.
        /// </summary>
        internal static void SetNetplayPlayerMode(int playerCount, TwoPlayerLayout layout)
        {
            if (_netplayPlayerCount == playerCount &&
                (playerCount == 0 || _netplayLayout == layout))
            {
                return;
            }

            _netplayPlayerCount = playerCount;
            _netplayLayout = layout;
            MultiplayerRuntime.SetPlayerCount(PlayerCount);
        }

        internal static bool IsMultiplayerEnabled
        {
            get { return PlayerCount > 1; }
        }


        internal static TwoPlayerLayout TwoPlayerLayout
        {
            get
            {
                if (_netplayPlayerCount > 0)
                {
                    return _netplayLayout;
                }

                EnsurePreferencesLoaded();
                return _preferences.TwoPlayerLayout;
            }
        }

        /// <summary>
        /// Whether players fight each other. Only meaningful alongside more than
        /// one player, and <see cref="BattleMode"/> checks that itself, so the
        /// setting can be left on while dropping back to one player.
        /// </summary>
        internal static bool IsBattleMode
        {
            get
            {
                EnsurePreferencesLoaded();
                return _preferences.BattleMode;
            }
        }

        internal static bool SetBattleMode(bool enabled)
        {
            EnsurePreferencesLoaded();
            if (_preferences.BattleMode == enabled)
            {
                return true;
            }

            _preferences.BattleMode = enabled;
            SavePreferences();

            // A round already in progress does not survive the switch: coming
            // back to battle mode should start even, not resume half-fought
            // health from before it was turned off.
            BattleMode.ResetRound();
            return true;
        }

        internal static PlayerTargets ResolvePlayerTargets(string user)
        {
            EnsurePreferencesLoaded();
            return _userRouter.Resolve(PlayerCount, user);
        }

        /// <summary>
        /// The netplay session. Idle until a lobby is opened, and costs one branch
        /// per frame while it is.
        /// </summary>
        internal static readonly NetplaySession Netplay = new NetplaySession();

        /// <summary>
        /// True once a lobby exists, whether or not the peer has arrived.
        ///
        /// The settings a session agreed on stop being this machine's to change at
        /// that point. `world-interaction.md` names the split: the line is not
        /// local against network but before a session against during one, and
        /// session-wide settings are locked while one is running rather than
        /// silently disagreeing between the two machines.
        ///
        /// The view is not among them. It decides what this screen shows and
        /// nothing about the simulation, so it stays each machine's own - which is
        /// also why netplay can fix it to one value without that being a
        /// restriction anybody has to agree to.
        /// </summary>
        internal static bool IsSessionLocked
        {
            get { return Netplay.Current != NetplaySession.Phase.Idle; }
        }

        internal static bool WriteSetupManifest
        {
            get
            {
                EnsurePreferencesLoaded();
                return _preferences.PlayerSetup.WriteManifest;
            }
        }

        /// <summary>
        /// The diagnostic probes, each off unless the settings file asks for it.
        /// Read through here rather than from the probes themselves so they stay
        /// free of settings plumbing and can be deleted in one piece.
        /// </summary>
        internal static DiagnosticsPreferences Diagnostics
        {
            get
            {
                EnsurePreferencesLoaded();
                return _preferences.Diagnostics;
            }
        }

        internal static void ProcessBrokerCommand()
        {
            IReadOnlyDictionary<string, string> parameters;
            if (!BrokerCommandClient.TryDequeue(
                CommandTarget,
                out parameters
            ))
            {
                return;
            }

            string command;
            if (!parameters.TryGetValue("command", out command))
            {
                return;
            }

            command = (command ?? string.Empty).Trim().ToLowerInvariant();

            int moverNumber;
            int targetNumber;
            if (GatherCommand.TryParse(command, out moverNumber, out targetNumber))
            {
                MultiplayerRuntime.GatherPlayer(moverNumber, targetNumber);
                return;
            }

            string user;
            if (command.Length != 2 || command[0] != 'p' ||
                command[1] < '1' || command[1] > '4' ||
                !parameters.TryGetValue("user", out user))
            {
                return;
            }

            AssignUserToPlayer(command[1] - '0', user);
        }

        internal static bool AssignUserToPlayer(int playerNumber, string user)
        {
            EnsurePreferencesLoaded();

            int playerCount = _preferences.PlayerCount;
            List<UserOverridePreference> overrides;
            if (playerCount == 1)
            {
                overrides = _preferences.SingleMode.UserOverrides;
            }
            else if (playerCount == 2)
            {
                overrides = _preferences.MultiplayerMode.UserOverrides;
            }
            else
            {
                overrides = _preferences.FourPlayerMode.UserOverrides;
            }

            if (!UserOverrideEditor.TryAssign(
                overrides,
                playerCount,
                playerNumber,
                user
            ))
            {
                return false;
            }

            _userRouter = CreateUserRouter(_preferences);
            SavePreferences();
            return true;
        }

        internal static bool SetPlayerMode(
            int playerCount,
            TwoPlayerLayout twoPlayerLayout
        )
        {
            EnsurePreferencesLoaded();
            if (playerCount != 1 && playerCount != 2 && playerCount != 4)
            {
                return false;
            }

            if (!Enum.IsDefined(typeof(TwoPlayerLayout), twoPlayerLayout))
            {
                return false;
            }

            if (_preferences.PlayerCount == playerCount &&
                _preferences.TwoPlayerLayout == twoPlayerLayout)
            {
                return true;
            }

            if (playerCount > 1)
            {
                string error;
                if (!TryReloadPreferences(out error))
                {
                    JumpKing.Program.crashLog.AddErrorMessage(
                        "Local Multiplayer settings were not loaded: " + error
                    );
                    return false;
                }
            }

            _preferences.PlayerCount = playerCount;
            _preferences.TwoPlayerLayout = twoPlayerLayout;
            SavePreferences();
            MultiplayerRuntime.SetPlayerCount(playerCount);
            return true;
        }

        // Online comes first, because it is the one line a player has to notice
        // without already knowing it is there. The rest are settings they went
        // looking for.
        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerHostAction LocalMultiplayerHostMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerHostAction();
        }

        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerInviteAction LocalMultiplayerInviteMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerInviteAction();
        }

        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerJoinAction LocalMultiplayerJoinMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerJoinAction();
        }

        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerLeaveAction LocalMultiplayerLeaveMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerLeaveAction();
        }

        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerModeOption LocalMultiplayerMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerModeOption();
        }

        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerSplitOption LocalMultiplayerSplitMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerSplitOption();
        }

        [PauseMenuItemSetting]
        [MainMenuItemSetting]
        public static LocalMultiplayerBattleOption LocalMultiplayerBattleMenu(
            object factory,
            JumpKing.PauseMenu.GuiFormat format
        )
        {
            return new LocalMultiplayerBattleOption();
        }

        private static void EnsurePatched()
        {
            if (_harmony != null)
            {
                return;
            }

            var harmony = new Harmony("eski4869.LocalMultiplayerMod");
            bool complete = true;

            // Steam callbacks are pumped by Game1.Update already, so registering
            // is all this needs. Idle until somebody opens a lobby.
            Netplay.Install();

            // PauseManager is internal to the game, so it is reached by name
            // rather than by type, exactly as AudioMixer and the gimmick
            // compatibility layer reach their own targets.
            Type pauseManager =
                AccessTools.TypeByName("JumpKing.PauseMenu.PauseManager");
            complete &= TryPatch(
                harmony,
                AccessTools.PropertyGetter(pauseManager, "IsPaused"),
                typeof(NetplayPausePatch),
                "PauseManager.IsPaused"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(pauseManager, "SetPause"),
                typeof(NetplaySetPausePatch),
                "PauseManager.SetPause"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(EntityManager), "Update"),
                typeof(NetplayStallPatch),
                "EntityManager.Update"
            );

            // Broker polling.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(Game1), "Update"),
                typeof(LocalMultiplayerGameUpdatePatch),
                "Game1.Update"
            );

            // The other end of the frame measurement, and the drawing's own share.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(Game1), "Draw"),
                typeof(LocalMultiplayerFrameEndPatch),
                "Game1.Draw (frame budget)"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(EntityManager), "Update"),
                typeof(LocalMultiplayerSimulationTimingPatch),
                "EntityManager.Update (frame budget)"
            );

            // Battle mode. Stomps are resolved after the whole frame has moved,
            // so this is a second patch on the same method rather than more work
            // inside the prefix above.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(Game1), "Update"),
                typeof(BattleUpdatePatch),
                "Game1.Update (battle)"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(PlayerEntity), "Draw"),
                typeof(BattlePlayerDrawPatch),
                "PlayerEntity.Draw (battle gauge)"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(FailState), "Start"),
                typeof(BattleSplatPatch),
                "FailState.Start"
            );

            // Additional players have no physical pad.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(InputComponent), "GetState"),
                typeof(AdditionalPlayerInputStatePatch),
                "InputComponent.GetState"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(InputComponent), "GetPressedState"),
                typeof(AdditionalPlayerInputStatePatch),
                "InputComponent.GetPressedState"
            );

            // The scope that makes every one-player global resolve to the player
            // being updated. This is what the rest of the design hangs off.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(Entity), "UpdateComponents"),
                typeof(PlayerUpdateScopePatch),
                "Entity.UpdateComponents"
            );

            // Per-player item state.
            complete &= TryPatch(
                harmony,
                GlobalItemState.IsWearingSkinMethod,
                typeof(SkinManagerIsWearingSkinPatch),
                "SkinManager.IsWearingSkin"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(
                    typeof(InventoryManager),
                    "HasItemEnabled"
                ),
                typeof(InventoryHasItemEnabledPatch),
                "InventoryManager.HasItemEnabled"
            );
            Type skinManagerType = AccessTools.TypeByName(
                "JumpKing.Player.Skins.SkinManager"
            );
            complete &= TryPatch(
                harmony,
                skinManagerType == null ? null :
                    AccessTools.Method(skinManagerType, "EnableSkin"),
                typeof(SkinManagerEnableSkinPatch),
                "SkinManager.EnableSkin"
            );
            complete &= TryPatch(
                harmony,
                skinManagerType == null ? null :
                    AccessTools.Method(skinManagerType, "DisableSkin"),
                typeof(SkinManagerDisableSkinPatch),
                "SkinManager.DisableSkin"
            );

            // Keep additional players out of the real save slot, so their
            // PlayerEntity.Update can run normally instead of being skipped.
            complete &= TryPatch(
                harmony,
                SaveLubeAccess.Getter,
                typeof(SaveLubePlayerPositionGetPatch),
                "SaveLube.PlayerPosition.get"
            );
            complete &= TryPatch(
                harmony,
                SaveLubeAccess.Setter,
                typeof(SaveLubePlayerPositionSetPatch),
                "SaveLube.PlayerPosition.set"
            );

            // Per-player level start: create the players before any mod hook runs,
            // then give each additional player its own pass.
            MethodInfo callOnLevelStart = AccessTools.Method(
                typeof(ModLoader),
                "CallOnLevelStartMethods"
            );
            complete &= TryPatch(
                harmony,
                callOnLevelStart,
                typeof(ModLevelStartDispatchPatch),
                "ModLoader.CallOnLevelStartMethods"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Property(typeof(EntityManager), "Entities")
                    .GetGetMethod(),
                typeof(EntityManagerEntitiesPatch),
                "EntityManager.Entities"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(
                    typeof(BodyComp),
                    "RegisterBlockBehaviour",
                    new Type[] { typeof(Type), typeof(IBlockBehaviour) }
                ),
                typeof(BodyCompRegisterBlockBehaviourPatch),
                "BodyComp.RegisterBlockBehaviour"
            );

            // Rendering and win handover.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(PlayerEntity), "Draw"),
                typeof(AdditionalPlayerDrawPatch),
                "PlayerEntity.Draw"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(JumpGame), "Draw"),
                typeof(MultiplayerDrawPatch),
                "JumpGame.Draw"
            );

            // The world half of JumpGame.Draw, suppressed during the split
            // renderer's single screen-space pass so the UI is not repeated in
            // every view.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(JumpGame), "DrawBG"),
                typeof(WorldDrawSuppressionPatch),
                "JumpGame.DrawBG"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelScreen), "Draw"),
                typeof(WorldDrawSuppressionPatch),
                "LevelScreen.Draw"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(LevelScreen), "DrawForeground"),
                typeof(WorldDrawSuppressionPatch),
                "LevelScreen.DrawForeground"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(EntityManager), "Draw"),
                typeof(WorldDrawSuppressionPatch),
                "EntityManager.Draw"
            );
            Type endingManagerType = AccessTools.TypeByName(
                "JumpKing.GameManager.MultiEnding.EndingManager"
            );
            complete &= TryPatch(
                harmony,
                endingManagerType == null ? null :
                    AccessTools.Method(endingManagerType, "CheckWin"),
                typeof(MultiplayerEndingPatch),
                "EndingManager.CheckWin"
            );

            // After the base patches, and safe here because mod assemblies are
            // loaded before [BeforeLevelLoad] runs: this one looks other mods up
            // by name.
            GimmickStateCompat.Install(harmony);
            TeleportProbe.Install(harmony);
            JumpProbe.Install(harmony);

            _harmony = harmony;
            if (!complete)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer installed with missing patches; " +
                    "multiplayer behaviour will be degraded."
                );
            }
        }

        /// <summary>
        /// Applies whichever of Prefix and Postfix the patch class declares.
        /// Reports the specific target that failed instead of silently disabling
        /// the whole mod, which is what the previous all-or-nothing check did.
        /// </summary>
        private static bool TryPatch(
            Harmony harmony,
            MethodBase target,
            Type patchType,
            string description
        )
        {

            if (target == null)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer patch target not found: " + description
                );
                return false;
            }

            try
            {
                MethodInfo prefix = AccessTools.Method(patchType, "Prefix");
                MethodInfo postfix = AccessTools.Method(patchType, "Postfix");
                if (prefix == null && postfix == null)
                {
                    return false;
                }

                harmony.Patch(
                    target,
                    prefix == null ? null : new HarmonyMethod(prefix),
                    postfix == null ? null : new HarmonyMethod(postfix)
                );
                return true;
            }
            catch (Exception ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer patch failed for " + description + ": " +
                    ex.Message
                );
                return false;
            }
        }

        private static void EnsurePreferencesLoaded()
        {
            if (_preferences != null)
            {
                return;
            }

            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _settingsPath = Path.Combine(assemblyDir, SettingsFileName);

            try
            {
                if (File.Exists(_settingsPath))
                {
                    var serializer = new XmlSerializer(typeof(LocalMultiplayerPreferences));
                    using (var stream = File.OpenRead(_settingsPath))
                    {
                        _preferences =
                            (LocalMultiplayerPreferences)serializer.Deserialize(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer settings error: " + ex.Message
                );
            }

            if (_preferences == null)
            {
                _preferences = new LocalMultiplayerPreferences();
            }

            EnsurePreferenceSections(_preferences);
            try
            {
                _userRouter = CreateUserRouter(_preferences);
            }
            catch (FormatException ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer settings error: " + ex.Message
                );
                _preferences = new LocalMultiplayerPreferences();
                _userRouter = CreateUserRouter(_preferences);
            }

            if (!File.Exists(_settingsPath))
            {
                SavePreferences();
            }
        }

        private static bool TryReloadPreferences(out string error)
        {
            error = null;
            try
            {
                var serializer = new XmlSerializer(typeof(LocalMultiplayerPreferences));
                LocalMultiplayerPreferences candidate;
                using (var stream = File.OpenRead(_settingsPath))
                {
                    candidate =
                        (LocalMultiplayerPreferences)serializer.Deserialize(stream);
                }

                EnsurePreferenceSections(candidate);
                UserCommandRouter router = CreateUserRouter(candidate);
                candidate.PlayerCount = _preferences.PlayerCount;
                _preferences = candidate;
                _userRouter = router;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static UserCommandRouter CreateUserRouter(
            LocalMultiplayerPreferences preferences
        )
        {
            return new UserCommandRouter(
                new[]
                {
                    preferences.SingleMode.DefaultRoutes.Player1Users
                },
                preferences.SingleMode.UserOverrides,
                new[]
                {
                    preferences.MultiplayerMode.DefaultRoutes.Player1Users,
                    preferences.MultiplayerMode.DefaultRoutes.Player2Users
                },
                preferences.MultiplayerMode.UserOverrides,
                new[]
                {
                    preferences.FourPlayerMode.DefaultRoutes.Player1Users,
                    preferences.FourPlayerMode.DefaultRoutes.Player2Users,
                    preferences.FourPlayerMode.DefaultRoutes.Player3Users,
                    preferences.FourPlayerMode.DefaultRoutes.Player4Users
                },
                preferences.FourPlayerMode.UserOverrides
            );
        }

        private static void EnsurePreferenceSections(
            LocalMultiplayerPreferences preferences
        )
        {
            if (preferences.SingleMode == null)
            {
                preferences.SingleMode = new SingleModePreferences();
            }
            else
            {
                preferences.SingleMode.EnsureInitialized();
            }

            if (preferences.MultiplayerMode == null)
            {
                preferences.MultiplayerMode = new MultiplayerModePreferences();
            }
            else
            {
                preferences.MultiplayerMode.EnsureInitialized();
            }

            if (preferences.FourPlayerMode == null)
            {
                preferences.FourPlayerMode = new FourPlayerModePreferences();
            }
            else
            {
                preferences.FourPlayerMode.EnsureInitialized();
            }

            // A settings file written before this section existed has no
            // Diagnostics element, and every probe reads through it.
            if (preferences.Diagnostics == null)
            {
                preferences.Diagnostics = new DiagnosticsPreferences();
            }
        }

        private static void SavePreferences()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(LocalMultiplayerPreferences));
                using (var stream = File.Create(_settingsPath))
                {
                    serializer.Serialize(stream, _preferences);
                }
            }
            catch (Exception ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer settings save failed: " + ex.Message
                );
            }
        }
    }

    /// <summary>
    /// Makes a peer's pause pause this game too, through the game's own mechanism.
    ///
    /// In interference mode the shared world cannot advance on one player's input,
    /// and a pause is far past any prediction window, so both sides have to stop.
    /// Doing that by suppressing the update from outside would stop the pause menu
    /// and the input polling with it, leaving no way to leave the session - the
    /// player would be locked in by their partner's pause.
    ///
    /// <c>GameLoop.Update</c> already gates every piece of game logic on this one
    /// property, and the game is built to be paused this way. Reporting the peer's
    /// pause through it borrows all of that, menus included.
    /// </summary>
    /// <summary>
    /// Holds the world still for the frames this machine is waiting out.
    ///
    /// The session decides to wait; this is what makes waiting mean anything. Both
    /// have to happen together - a frame that is not counted must also not be
    /// simulated, or the local player is handed the previous frame's input again
    /// and their own king moves on a repeat of what they last pressed.
    ///
    /// The re-simulation drives this same method directly, and must not be blocked
    /// by it: that pass is catching up, not waiting.
    /// </summary>
    internal static class NetplayStallPatch
    {
        public static bool Prefix()
        {
            return !ModEntry.Netplay.IsStalling || Resimulation.IsActive;
        }
    }

    internal static class NetplayPausePatch
    {
        public static void Postfix(ref bool __result)
        {
            if (!__result && ModEntry.Netplay.IsHeldByPeer)
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// Tells the peer when this player pauses, so both sides stop together.
    ///
    /// Patched on the static entry point rather than watched from the frame loop:
    /// a pause that was noticed a frame late would let one side advance a frame the
    /// other did not, and in a shared simulation that difference does not heal.
    /// </summary>
    internal static class NetplaySetPausePatch
    {
        public static void Postfix(bool p_pause)
        {
            ModEntry.Netplay.NoteLocalPause(p_pause);
        }
    }

    /// <summary>
    /// Stamps the end of a frame's work.
    /// </summary>
    /// <remarks>
    /// <c>Game1.Draw</c> is the last thing a frame does - from the decompiled
    /// source, it loads textures, renders to its target, blits it and calls the
    /// base. Ending the measurement here and starting it in the update prefix puts
    /// everything the frame does between the two stamps, whichever piece of
    /// hardware does it.
    /// </remarks>
    internal static class LocalMultiplayerFrameEndPatch
    {
        [ThreadStatic]
        private static long _drawBegan;

        public static void Prefix()
        {
            _drawBegan = FrameCost.Now;
        }

        public static void Postfix()
        {
            FrameCost.AddDraw(_drawBegan);
            FrameBudget.NoteFrameEnd();
        }
    }

    /// <summary>
    /// Times the whole simulation, so one player and two are measured the same way.
    /// </summary>
    /// <remarks>
    /// The per-player figure only ever covered the players this mod added, which
    /// reads as zero with one player - so it could say the second king cost nine
    /// tenths of a millisecond without saying whether that was a tenth of the
    /// simulation or half of it. Timing the whole thing under each player count
    /// answers the question that was actually being asked.
    /// </remarks>
    internal static class LocalMultiplayerSimulationTimingPatch
    {
        [ThreadStatic]
        private static long _began;

        public static void Prefix()
        {
            _began = FrameCost.Now;
        }

        public static void Postfix()
        {
            // Re-simulated frames are simulation too, and counting them here would
            // hide a correction's cost inside the ordinary per-frame figure. They
            // are reported separately as catchup_ms and resim_ms.
            if (!Resimulation.IsActive)
            {
                FrameCost.AddSimulation(_began);
            }
        }
    }

    internal static class LocalMultiplayerGameUpdatePatch
    {
        public static bool Prefix(Microsoft.Xna.Framework.GameTime gameTime)
        {
            PlayerScope.ResetIfLeaked();

            // A depth left behind by an exception inside a recomputation would
            // silence every sound for the rest of the run, so it is cleared here
            // where the depth is known to be zero.
            Resimulation.ResetIfLeaked();
            ModEntry.ProcessBrokerCommand();

            if (MultiplayerRuntime.IsActive)
            {
                for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
                {
                    PlayerContext context = MultiplayerRuntime.GetContext(number);
                    ScreenTrackingProbe.Sample(number, context);
                    JumpProbe.SamplePeak(number, context);
                }
            }

            float delta = gameTime == null
                ? 0f
                : (float)gameTime.ElapsedGameTime.TotalSeconds;

            // Whether this machine is keeping up with the fixed step at all.
            //
            // The game runs on a fixed timestep, so delta is a constant sixtieth of
            // a second however the hardware is doing - which means the simulation
            // can never say that a machine is struggling, and every measurement
            // this mod takes of its own cost is silent about it too. MonoGame does
            // know: it raises this when the update loop cannot finish a frame
            // inside its budget and has to catch up.
            //
            // It is the difference between "the slower machine cannot run this
            // game at sixty frames a second" and "this mod is making it miss", and
            // no amount of reasoning from either side separates them.
            bool runningSlowly = gameTime != null && gameTime.IsRunningSlowly;
            ModEntry.Netplay.NoteFrameTiming(runningSlowly);

            // The same reading, reported whether or not a session is running, so
            // the question can be asked of one machine on its own.
            FrameBudget.Enabled = ModEntry.Diagnostics.FrameBudget;
            FrameBudget.Note(runningSlowly);

            ModEntry.Netplay.BeforeGameUpdate(delta);
            return true;
        }

        public static void Postfix()
        {
            ModEntry.Netplay.AfterGameUpdate();
        }
    }

    /// <summary>
    /// Brackets the base <c>[OnLevelStart]</c> dispatch.
    ///
    /// The prefix creates every additional player first, because block mods look
    /// up "the player" inside that hook and register their behaviours on its body
    /// - if the player does not exist yet, it never gets them. It also creates
    /// player 1's context, which is what lets registrations be recorded as they
    /// happen.
    ///
    /// The postfix then hands the additional players the same set, by whichever
    /// mechanism <see cref="PlayerSetup"/> is configured for.
    /// </summary>
    internal static class ModLevelStartDispatchPatch
    {
        public static void Prefix()
        {
            BlockBehaviourRecorder.BeginDispatch();
            MultiplayerRuntime.BeforeModLevelStart();
        }

        public static void Postfix()
        {
            BlockBehaviourRecorder.EndDispatch();
            MultiplayerRuntime.AfterModLevelStart();
        }
    }

    public class LocalMultiplayerPreferences
    {
        public int PlayerCount { get; set; } = 1;
        public TwoPlayerLayout TwoPlayerLayout { get; set; } =
            TwoPlayerLayout.FullHeight;
        public bool BattleMode { get; set; } = false;
        public SingleModePreferences SingleMode { get; set; } =
            new SingleModePreferences();
        public MultiplayerModePreferences MultiplayerMode { get; set; } =
            new MultiplayerModePreferences();
        public FourPlayerModePreferences FourPlayerMode { get; set; } =
            new FourPlayerModePreferences();
        public PlayerSetupPreferences PlayerSetup { get; set; } =
            new PlayerSetupPreferences();
        public DiagnosticsPreferences Diagnostics { get; set; } =
            new DiagnosticsPreferences();
    }

    /// <summary>
    /// The probes. Each answers one question about a mechanism that cannot be
    /// inspected from outside the running game, and each is silent unless asked
    /// for.
    ///
    /// They stay in the build rather than being added when a symptom appears,
    /// because the symptoms that need them are the ones that are hard to
    /// reproduce on demand - by the time a probe has been written, compiled and
    /// deployed, the run that showed the fault is gone. Shipping them switched
    /// off costs one boolean read per sample.
    ///
    /// Off by default, and deliberately not exposed in the pause menu: these
    /// write to the crash log every time their answer changes, which is a
    /// developer's tool and not a player's setting.
    /// </summary>
    public class DiagnosticsPreferences
    {
        /// <summary>
        /// Whether this machine finishes its frames, reported once a second
        /// regardless of what the mod is doing.
        /// </summary>
        /// <remarks>
        /// For separating "this machine is too slow for this game" from "this mod
        /// makes it too slow", which needs one machine rather than two: run alone,
        /// run with a second player locally, compare. The equivalent numbers used
        /// to appear only inside a netplay session, where the answer arrived
        /// tangled up with everything the network was doing.
        /// </remarks>
        public bool FrameBudget { get; set; } = false;

        /// <summary>
        /// Whether <c>PlayerContext.Screen</c> - the screen collision resolves
        /// against for a player who is not the global camera - has drifted from
        /// the screen that player's own position falls on.
        ///
        /// <c>GetCollisionInfo</c> only searches the tracked screen plus or minus
        /// one, so a drift of two or more means no ground is found at all. That
        /// reads in play as a player falling through the world.
        /// </summary>
        public bool ScreenTracking { get; set; } = false;

        /// <summary>
        /// What the base game's screen teleport resolves, per player, at the
        /// moment it fires: the camera's screen, the screen the body is really
        /// on, the teleport links found, and the move that resulted.
        /// </summary>
        public bool Teleport { get; set; } = false;

        /// <summary>
        /// Every player's position, velocity and input against the frame number,
        /// on both machines.
        ///
        /// The point is that the two logs line up. A checksum says the games have
        /// drifted; this says which player, which direction, from which frame, and
        /// whether it began at a jump, a landing, or nothing at all. Turn it on for
        /// one session on both sides and compare - it writes a line per player per
        /// frame, so it is only bearable while it is being read.
        /// </summary>
        public bool Netplay { get; set; } = false;

        /// <summary>
        /// What the upside-down gravity resync produced, so a fix that is broken
        /// and a fix that never installed can be told apart.
        /// </summary>
        public bool UpsideDown { get; set; } = false;

        /// <summary>
        /// Every jump as it launches - intensity, the velocity it leaves with,
        /// and how far it rose - plus how many other players were mid-charge at
        /// the time. One line per jump, so this is quiet enough to leave on.
        /// </summary>
        public bool Jump { get; set; } = false;
    }

    /// <summary>
    /// How the additional players are given player 1's block behaviours, and
    /// whether the result is written out for inspection.
    /// </summary>
    public class PlayerSetupPreferences
    {
        /// <summary>
        /// Write what each player's body actually holds to a text file beside the
        /// settings. Off by default; the point of it is comparing two runs.
        /// </summary>
        public bool WriteManifest { get; set; } = false;
    }

    public class SingleModePreferences
    {
        public SingleModeDefaultRoutes DefaultRoutes { get; set; } =
            new SingleModeDefaultRoutes();
        [XmlArray("UserOverrides")]
        [XmlArrayItem("User")]
        public List<UserOverridePreference> UserOverrides { get; set; } =
            new List<UserOverridePreference>();

        internal void EnsureInitialized()
        {
            if (DefaultRoutes == null)
            {
                DefaultRoutes = new SingleModeDefaultRoutes();
            }
            if (UserOverrides == null)
            {
                UserOverrides = new List<UserOverridePreference>();
            }
        }
    }

    public class MultiplayerModePreferences
    {
        public MultiplayerModeDefaultRoutes DefaultRoutes { get; set; } =
            new MultiplayerModeDefaultRoutes();
        [XmlArray("UserOverrides")]
        [XmlArrayItem("User")]
        public List<UserOverridePreference> UserOverrides { get; set; } =
            new List<UserOverridePreference>();

        internal void EnsureInitialized()
        {
            if (DefaultRoutes == null)
            {
                DefaultRoutes = new MultiplayerModeDefaultRoutes();
            }
            if (UserOverrides == null)
            {
                UserOverrides = new List<UserOverridePreference>();
            }
        }
    }

    public class FourPlayerModePreferences
    {
        public FourPlayerModeDefaultRoutes DefaultRoutes { get; set; } =
            new FourPlayerModeDefaultRoutes();
        [XmlArray("UserOverrides")]
        [XmlArrayItem("User")]
        public List<UserOverridePreference> UserOverrides { get; set; } =
            new List<UserOverridePreference>();

        internal void EnsureInitialized()
        {
            if (DefaultRoutes == null)
            {
                DefaultRoutes = new FourPlayerModeDefaultRoutes();
            }
            if (UserOverrides == null)
            {
                UserOverrides = new List<UserOverridePreference>();
            }
        }
    }

    public class SingleModeDefaultRoutes
    {
        public string Player1Users { get; set; } = "*";
    }

    public class MultiplayerModeDefaultRoutes
    {
        public string Player1Users { get; set; } = "[a-m]*";
        public string Player2Users { get; set; } = "[n-z]*";
    }

    public class FourPlayerModeDefaultRoutes
    {
        public string Player1Users { get; set; } = "[a-f]*";
        public string Player2Users { get; set; } = "[g-m]*";
        public string Player3Users { get; set; } = "[n-s]*";
        public string Player4Users { get; set; } = "[t-z]*";
    }

    public enum TwoPlayerLayout
    {
        /// <summary>Side by side, each player getting half the width.</summary>
        FullHeight,

        /// <summary>Side by side at half scale, letterboxed above and below.</summary>
        Compact,

        /// <summary>
        /// One above the other, each player keeping the full width. Suits a map
        /// whose route runs sideways rather than straight up.
        /// </summary>
        Stacked,

        /// <summary>
        /// No split at all: one full-size view on player 1's camera, with the
        /// other player drawn into it.
        ///
        /// Two cameras are hard to read when the players are meant to be
        /// interacting rather than racing - in battle mode especially, the point
        /// is that both kings are in the same picture. The cost is that player 2
        /// is only visible while they are inside player 1's screen.
        /// </summary>
        Shared
    }

    /// <summary>
    /// What shape this session takes: solo, two players here, four players here,
    /// or online.
    ///
    /// **Online belongs in this control rather than beside it.** It is not a
    /// switch layered over a player count - it is a fourth answer to the same
    /// question, and mutually exclusive with the other three. Putting it here
    /// makes "online with four local players" impossible to express rather than
    /// merely discouraged, and leaves one place to look for what is running.
    ///
    /// It is also the only control that is never locked, because it is how a
    /// session is left as well as entered. Locking it would trap a player in
    /// their partner's session.
    ///
    /// Kept separate from the view, which stays each machine's own and does not
    /// describe the session at all.
    /// </summary>
    public class LocalMultiplayerModeOption : IOptions
    {
        public LocalMultiplayerModeOption() : base(
            3,
            PlayerCountToOption(ModEntry.PlayerCount),
            IOptions.EdgeMode.Wrap
        )
        {
        }

        protected override bool CanChange()
        {
            // Fixed at two while online, because a session is one player per
            // machine. Locking is what says so. Folding "online" into this control
            // as a fourth option says the same thing, but hides that online exists
            // at all behind three presses of a cycling control - which is the
            // trade that was got wrong the first time.
            return !ModEntry.IsSessionLocked;
        }

        protected override string CurrentOptionName()
        {
            switch (CurrentOption)
            {
                case 1:
                    return "Players: 2";
                case 2:
                    return "Players: 4";
                default:
                    return "Players: 1";
            }
        }

        protected override void OnOptionChange(int option)
        {
            int playerCount = OptionToPlayerCount(option);
            if (ModEntry.SetPlayerMode(playerCount, ModEntry.TwoPlayerLayout))
            {
                CurrentOption = option;
                return;
            }

            CurrentOption = PlayerCountToOption(ModEntry.PlayerCount);
        }

        private static int PlayerCountToOption(int playerCount)
        {
            switch (playerCount)
            {
                case 2:
                    return 1;
                case 4:
                    return 2;
                default:
                    return 0;
            }
        }

        private static int OptionToPlayerCount(int option)
        {
            switch (option)
            {
                case 1:
                    return 2;
                case 2:
                    return 4;
                default:
                    return 1;
            }
        }
    }

    /// <summary>
    /// How the screen is divided in two player. Has no effect on one or four
    /// players, so the label says which mode it belongs to.
    /// </summary>
    public class LocalMultiplayerSplitOption : IOptions
    {
        public LocalMultiplayerSplitOption() : base(
            4,
            LayoutToOption(ModEntry.TwoPlayerLayout),
            IOptions.EdgeMode.Wrap
        )
        {
        }

        protected override bool CanChange()
        {
            // The view is normally each machine's own business, and stays free.
            // Online is the exception, and not because it is shared: with one
            // player per machine there is only one local camera, so the split
            // layouts have nothing to split.
            return !ModEntry.IsSessionLocked;
        }

        protected override string CurrentOptionName()
        {
            switch (CurrentOption)
            {
                case 1:
                    return "2P View: Compact";
                case 2:
                    return "2P View: Stacked";
                case 3:
                    return "2P View: Shared";
                default:
                    return "2P View: Side";
            }
        }

        protected override void OnOptionChange(int option)
        {
            TwoPlayerLayout layout = OptionToLayout(option);
            if (ModEntry.SetPlayerMode(ModEntry.PlayerCount, layout))
            {
                CurrentOption = option;
                return;
            }

            CurrentOption = LayoutToOption(ModEntry.TwoPlayerLayout);
        }

        private static int LayoutToOption(TwoPlayerLayout layout)
        {
            switch (layout)
            {
                case TwoPlayerLayout.Compact:
                    return 1;
                case TwoPlayerLayout.Stacked:
                    return 2;
                case TwoPlayerLayout.Shared:
                    return 3;
                default:
                    return 0;
            }
        }

        private static TwoPlayerLayout OptionToLayout(int option)
        {
            switch (option)
            {
                case 1:
                    return TwoPlayerLayout.Compact;
                case 2:
                    return TwoPlayerLayout.Stacked;
                case 3:
                    return TwoPlayerLayout.Shared;
                default:
                    return TwoPlayerLayout.FullHeight;
            }
        }
    }

    /// <summary>
    /// Whether players fight. Kept as its own line rather than folded into the
    /// player count, so neither label has to grow to carry both.
    /// </summary>
    /// <summary>
    /// Online play: its own line, and the whole flow.
    ///
    /// It has to be a line of its own. Folding it into the player count made
    /// nonsense combinations inexpressible, which was real, but it also buried the
    /// existence of online play behind three presses of a cycling control - a
    /// player who does not already know it is there never finds it. Locking the
    /// player count while online buys the same guarantee without hiding anything.
    ///
    /// One line carries the whole flow, because there is only ever one thing to do
    /// next:
    ///
    ///   off          -> confirm opens a lobby, and nothing else happens
    ///   waiting      -> confirm opens Steam's invite picker
    ///   connected    -> nothing to do
    ///
    /// The picker is not opened by turning this on. Throwing the Steam overlay in
    /// front of somebody who has just changed a setting takes the screen away
    /// before they asked for it; opening it on a second, deliberate press does not.
    ///
    /// Steam's own picker rather than a friend list drawn here: it knows who can be
    /// invited and delivers the invitation as a normal Steam notification.
    /// Reimplementing that inside a pause menu in a pixel font would be worse at it
    /// in every way. The lobby is friends-only besides, so the other side can also
    /// simply join from their friends list.
    ///
    /// Left with the same key that started it, so nobody is stuck in a session
    /// they cannot get out of.
    /// </summary>
    /// <summary>
    /// Opens and closes the host's lobby - one line, one toggle.
    ///
    /// Opening and closing belong together because they are the same decision
    /// seen from either side of itself, and a control that reads like a switch is
    /// read like one. Inviting is a different act and has its own line: it does
    /// not change what the session is, only who is told about it, and folding the
    /// two together left one key meaning three things by state.
    ///
    /// Greyed out while a guest, because a guest has no lobby to open or close.
    /// </summary>
    public class LocalMultiplayerHostAction : IBTSimpleMenuItem
    {
        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                Label,
                IsAvailable ? Color.White : Color.Gray,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(Label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            if (!IsAvailable ||
                !ControllerManager.instance.MenuController.GetPadState().confirm)
            {
                return BTresult.Failure;
            }

            ControllerManager.instance.MenuController.ConsumePadPresses();

            if (ModEntry.Netplay.Current == NetplaySession.Phase.Idle)
            {
                ModEntry.Netplay.Host();
            }
            else
            {
                ModEntry.Netplay.Leave();
            }

            return BTresult.Success;
        }

        /// <summary>
        /// Off while a guest: their session is somebody else's to close, and this
        /// line would otherwise be the one that leaves it, which is the Guest
        /// line's job.
        /// </summary>
        private static bool IsAvailable
        {
            get
            {
                return ModEntry.Netplay.Current == NetplaySession.Phase.Idle ||
                    ModEntry.Netplay.IsHost;
            }
        }

        private static string Label
        {
            get
            {
                if (ModEntry.Netplay.Current == NetplaySession.Phase.Idle)
                {
                    return "Host: open a lobby";
                }

                if (!ModEntry.Netplay.IsHost)
                {
                    return "Host: (you joined a lobby)";
                }

                string peer = ModEntry.Netplay.PeerName;
                switch (ModEntry.Netplay.Current)
                {
                    case NetplaySession.Phase.Playing:
                        return "Host: close lobby - playing with " +
                            (string.IsNullOrEmpty(peer) ? "a friend" : peer);
                    case NetplaySession.Phase.Handshaking:
                        return "Host: close lobby - connecting";
                    default:
                        return "Host: close lobby - waiting";
                }
            }
        }
    }

    /// <summary>
    /// Invites a friend, through Steam's own picker.
    ///
    /// Separate from opening the lobby, because it does not change what the
    /// session is - only who has been told. Greyed out when there is nobody to
    /// invite to anything, which is any time this machine is not hosting an open
    /// lobby, and the label says which case it is rather than leaving it to be
    /// worked out.
    /// </summary>
    public class LocalMultiplayerInviteAction : IBTSimpleMenuItem
    {
        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                Label,
                IsAvailable ? Color.White : Color.Gray,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(Label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            if (!IsAvailable ||
                !ControllerManager.instance.MenuController.GetPadState().confirm)
            {
                return BTresult.Failure;
            }

            ControllerManager.instance.MenuController.ConsumePadPresses();
            ModEntry.Netplay.Invite();
            return BTresult.Success;
        }

        private static bool IsAvailable
        {
            get
            {
                return ModEntry.Netplay.IsHost &&
                    ModEntry.Netplay.Current != NetplaySession.Phase.Idle;
            }
        }

        private static string Label
        {
            get
            {
                if (ModEntry.Netplay.Current == NetplaySession.Phase.Idle)
                {
                    return "Invite: open a lobby first";
                }

                if (!ModEntry.Netplay.IsHost)
                {
                    return "Invite: only the host can";
                }

                return ModEntry.Netplay.Current == NetplaySession.Phase.Playing
                    ? "Invite a friend (lobby is full)"
                    : "Invite a friend";
            }
        }
    }

    /// <summary>
    /// Leaves the session, in the terms of whichever side is leaving.
    ///
    /// A separate line from the one that hosts and invites, because the two are
    /// not variations of one action. Closing a lobby ends the session for the
    /// other player; walking out of one does not. Sharing a key with "invite a
    /// friend" would put the destructive one a mispress away from the routine one.
    ///
    /// It also gives the menu somewhere to state the role at all times, which is
    /// the thing that was missing: from inside a session the two sides otherwise
    /// look identical.
    /// </summary>
    public class LocalMultiplayerLeaveAction : IBTSimpleMenuItem
    {
        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                Label,
                IsAvailable ? Color.White : Color.Gray,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(Label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            if (!IsAvailable ||
                !ControllerManager.instance.MenuController.GetPadState().confirm)
            {
                return BTresult.Failure;
            }

            ControllerManager.instance.MenuController.ConsumePadPresses();
            ModEntry.Netplay.Leave();
            return BTresult.Success;
        }

        /// <summary>
        /// For guests only. A host closes their lobby on the Host line, which is
        /// the same control that opened it - leaving that one act split across two
        /// places would be the confusion this menu keeps being reworked to remove.
        /// </summary>
        private static bool IsAvailable
        {
            get
            {
                return ModEntry.Netplay.Current != NetplaySession.Phase.Idle &&
                    !ModEntry.Netplay.IsHost;
            }
        }

        private static string Label
        {
            get
            {
                if (ModEntry.Netplay.Current == NetplaySession.Phase.Idle)
                {
                    return "Guest: not in a lobby";
                }

                if (ModEntry.Netplay.IsHost)
                {
                    return "Guest: (you are hosting)";
                }

                return "Guest: leave the lobby";
            }
        }
    }
    /// <summary>
    /// Finds lobbies and joins one, inside the game.
    ///
    /// Its own line, separate from hosting, because they are different acts and the
    /// player has to see which one they are about to do. Waiting to be invited also
    /// leaves the guest depending on the host to think of them; a friends-only lobby
    /// is visible to friends, so it can simply be looked for.
    ///
    /// The first attempt at this opened Steam's overlay instead. That was not a
    /// lobby browser - it lists friends, and there is nothing in it to press - so
    /// the guest had no way in at all.
    ///
    /// Confirm searches, then joins. Left and right move through what was found,
    /// which is why the host's name is in the label: with more than one lobby up,
    /// the name is the only thing telling them apart.
    /// </summary>
    public class LocalMultiplayerJoinAction : IBTSimpleMenuItem
    {
        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                Label,
                IsAvailable ? Color.White : Color.Gray,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(Label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            if (!IsAvailable)
            {
                return BTresult.Failure;
            }

            PadState pad = ControllerManager.instance.MenuController.GetPadState();

            if (ModEntry.Netplay.Found.Count > 1 && (pad.left || pad.right))
            {
                ControllerManager.instance.MenuController.ConsumePadPresses();
                ModEntry.Netplay.SelectNext(pad.right ? 1 : -1);
                return BTresult.Success;
            }

            if (!pad.confirm)
            {
                return BTresult.Failure;
            }

            ControllerManager.instance.MenuController.ConsumePadPresses();

            if (ModEntry.Netplay.Found.Count == 0)
            {
                ModEntry.Netplay.Join();
            }
            else
            {
                ModEntry.Netplay.JoinSelected();
            }

            return BTresult.Success;
        }

        private static bool IsAvailable
        {
            get
            {
                return ModEntry.Netplay.Current == NetplaySession.Phase.Idle &&
                    !ModEntry.Netplay.IsSearching;
            }
        }

        private static string Label
        {
            get
            {
                if (ModEntry.Netplay.IsSearching)
                {
                    return "Guest: searching...";
                }

                if (ModEntry.Netplay.Current != NetplaySession.Phase.Idle)
                {
                    return "Guest: already in a lobby";
                }

                int count = ModEntry.Netplay.Found.Count;
                if (count == 0)
                {
                    return "Guest: search for a lobby";
                }

                NetplayTransport.FoundLobby lobby =
                    ModEntry.Netplay.Found[ModEntry.Netplay.Selected];

                return count == 1
                    ? "Guest: join " + lobby.HostName
                    : "Guest: join " + lobby.HostName +
                        " (" + (ModEntry.Netplay.Selected + 1) + "/" + count + ")";
            }
        }
    }

    public class LocalMultiplayerBattleOption : IOptions
    {
        public LocalMultiplayerBattleOption() : base(
            2,
            ModEntry.IsBattleMode ? 1 : 0,
            IOptions.EdgeMode.Wrap
        )
        {
        }

        protected override bool CanChange()
        {
            // Locked while a session exists: this is agreed once and must not
            // drift apart on the two machines afterwards.
            return !ModEntry.IsSessionLocked;
        }

        protected override string CurrentOptionName()
        {
            return CurrentOption == 1 ? "Battle: On" : "Battle: Off";
        }

        protected override void OnOptionChange(int option)
        {
            if (ModEntry.SetBattleMode(option == 1))
            {
                CurrentOption = option;
                return;
            }

            CurrentOption = ModEntry.IsBattleMode ? 1 : 0;
        }
    }
}
