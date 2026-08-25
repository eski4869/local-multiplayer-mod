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
using JumpKing.PauseMenu.BT.Actions;
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
                EnsurePreferencesLoaded();
                return _preferences.PlayerCount;
            }
        }

        internal static bool IsMultiplayerEnabled
        {
            get { return PlayerCount > 1; }
        }


        internal static TwoPlayerLayout TwoPlayerLayout
        {
            get
            {
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

            // Broker polling.
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(Game1), "Update"),
                typeof(LocalMultiplayerGameUpdatePatch),
                "Game1.Update"
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

    internal static class LocalMultiplayerGameUpdatePatch
    {
        public static void Prefix()
        {
            PlayerScope.ResetIfLeaked();
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
    /// How many players. Kept separate from the split layout so each menu line
    /// stays short enough to fit, and so changing one does not read as changing
    /// the other.
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
            return true;
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
            return true;
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
            return true;
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
