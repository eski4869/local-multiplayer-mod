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

        internal static PlayerTargets ResolvePlayerTargets(string user)
        {
            EnsurePreferencesLoaded();
            return _userRouter.Resolve(PlayerCount, user);
        }

        /// <summary>
        /// Decides whether a mod's <c>[OnLevelStart]</c> is replayed per player.
        ///
        /// The default answer is "only if it registered a block behaviour", which
        /// a mod proves by doing it during the normal dispatch. The two lists are
        /// escape hatches for the cases that judgement gets wrong.
        /// </summary>
        internal static bool ShouldReplayMod(
            string modName,
            bool registeredBlockBehaviour
        )
        {
            EnsurePreferencesLoaded();
            LevelStartReplayPreferences settings = _preferences.LevelStartReplay;

            if (ContainsModName(settings.NeverReplay, modName))
            {
                return false;
            }

            if (ContainsModName(settings.AlsoReplay, modName))
            {
                return true;
            }

            return registeredBlockBehaviour;
        }

        private static bool ContainsModName(string list, string modName)
        {
            if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(modName))
            {
                return false;
            }

            string[] entries = list.Split(
                new[] { ';', ',' },
                StringSplitOptions.RemoveEmptyEntries
            );
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.Equals(
                    entries[i].Trim(),
                    modName,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    return true;
                }
            }

            return false;
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

            string user;
            string command;
            if (!parameters.TryGetValue("user", out user) ||
                !parameters.TryGetValue("command", out command))
            {
                return;
            }

            command = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (command.Length != 2 || command[0] != 'p' ||
                command[1] < '1' || command[1] > '4')
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
                AccessTools.Method(typeof(EntityManager), "AddObject"),
                typeof(EntityManagerAddObjectPatch),
                "EntityManager.AddObject"
            );
            complete &= TryPatch(
                harmony,
                AccessTools.Method(typeof(EntityManager), "MoveToFront"),
                typeof(EntityManagerMoveToFrontPatch),
                "EntityManager.MoveToFront"
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

            if (preferences.LevelStartReplay == null)
            {
                preferences.LevelStartReplay = new LevelStartReplayPreferences();
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
        }
    }

    /// <summary>
    /// Brackets the base <c>[OnLevelStart]</c> dispatch.
    ///
    /// The prefix creates every additional player first, because block mods look
    /// up "the player" inside that hook and register their behaviours on its body
    /// - if the player does not exist yet, it never gets them. The postfix then
    /// runs the same dispatch once per additional player so each one is set up by
    /// the mod itself rather than by cloning the first player's behaviours.
    /// </summary>
    internal static class ModLevelStartDispatchPatch
    {
        public static void Prefix()
        {
            LevelStartReplay.BeginBaseDispatch();
            MultiplayerRuntime.BeforeModLevelStart();
        }

        public static void Postfix()
        {
            LevelStartReplay.EndBaseDispatch();
            MultiplayerRuntime.AfterModLevelStart();
        }
    }

    public class LocalMultiplayerPreferences
    {
        public int PlayerCount { get; set; } = 1;
        public TwoPlayerLayout TwoPlayerLayout { get; set; } =
            TwoPlayerLayout.FullHeight;
        public SingleModePreferences SingleMode { get; set; } =
            new SingleModePreferences();
        public MultiplayerModePreferences MultiplayerMode { get; set; } =
            new MultiplayerModePreferences();
        public FourPlayerModePreferences FourPlayerMode { get; set; } =
            new FourPlayerModePreferences();
        public LevelStartReplayPreferences LevelStartReplay { get; set; } =
            new LevelStartReplayPreferences();
    }

    /// <summary>
    /// Overrides for which mods get their <c>[OnLevelStart]</c> replayed per
    /// player. Both are semicolon-separated mod names as they appear in
    /// <c>ModLoadLog.txt</c>, and both are normally empty: a mod qualifies by
    /// registering a block behaviour, which needs no configuration.
    /// </summary>
    public class LevelStartReplayPreferences
    {
        /// <summary>Replay these even though they registered no block behaviour.</summary>
        public string AlsoReplay { get; set; } = string.Empty;

        /// <summary>Never replay these, whatever they registered.</summary>
        public string NeverReplay { get; set; } = string.Empty;
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
        FullHeight,
        Compact
    }

    public class LocalMultiplayerModeOption : IOptions
    {
        public LocalMultiplayerModeOption() : base(
            4,
            ModeToOption(ModEntry.PlayerCount, ModEntry.TwoPlayerLayout),
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
                    return "Multiplayer: 2P";
                case 2:
                    return "Multiplayer: 2P Compact";
                case 3:
                    return "Multiplayer: 4P";
                default:
                    return "Multiplayer: 1P";
            }
        }

        protected override void OnOptionChange(int option)
        {
            int playerCount = OptionToPlayerCount(option);
            TwoPlayerLayout layout = OptionToLayout(option);
            if (ModEntry.SetPlayerMode(playerCount, layout))
            {
                CurrentOption = option;
                return;
            }

            CurrentOption = ModeToOption(
                ModEntry.PlayerCount,
                ModEntry.TwoPlayerLayout
            );
        }

        private static int ModeToOption(
            int playerCount,
            TwoPlayerLayout layout
        )
        {
            if (playerCount == 4)
            {
                return 3;
            }

            if (playerCount == 2)
            {
                return layout == TwoPlayerLayout.Compact ? 2 : 1;
            }

            return 0;
        }

        private static int OptionToPlayerCount(int option)
        {
            return option == 3 ? 4 : option == 1 || option == 2 ? 2 : 1;
        }

        private static TwoPlayerLayout OptionToLayout(int option)
        {
            return option == 2
                ? TwoPlayerLayout.Compact
                : TwoPlayerLayout.FullHeight;
        }
    }
}
