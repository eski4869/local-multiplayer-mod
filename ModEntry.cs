using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using EntityComponent;
using HarmonyLib;
using JumpKing;
using JumpKing.GameManager.MultiEnding;
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

        private static Harmony _harmony;
        private static LocalMultiplayerPreferences _preferences;
        private static UserCommandRouter _userRouter;
        private static string _settingsPath;

        [BeforeLevelLoad]
        public static void BeforeLevelLoad()
        {
            EnsurePreferencesLoaded();
            EnsurePatched();
        }

        [OnLevelStart]
        public static void OnLevelStart()
        {
            EnsurePreferencesLoaded();
            EnsurePatched();
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

            try
            {
                MethodInfo gameUpdate = AccessTools.Method(typeof(Game1), "Update");
                MethodInfo gameUpdatePrefix = AccessTools.Method(
                    typeof(LocalMultiplayerGameUpdatePatch),
                    "Prefix"
                );
                MethodInfo inputGetState = AccessTools.Method(typeof(InputComponent), "GetState");
                MethodInfo inputGetPressedState = AccessTools.Method(
                    typeof(InputComponent),
                    "GetPressedState"
                );
                MethodInfo inputStatePrefix = AccessTools.Method(
                    typeof(AdditionalPlayerInputStatePatch),
                    "Prefix"
                );
                MethodInfo playerUpdate = AccessTools.Method(typeof(PlayerEntity), "Update");
                MethodInfo playerUpdatePrefix = AccessTools.Method(
                    typeof(AdditionalPlayerSaveUpdatePatch),
                    "Prefix"
                );
                MethodInfo playerSetSprite = AccessTools.Method(
                    typeof(PlayerEntity),
                    "SetSprite"
                );
                MethodInfo spritePrefix = AccessTools.Method(
                    typeof(AdditionalPlayerSpritePatch),
                    "Prefix"
                );
                MethodInfo entityUpdateComponents = AccessTools.Method(
                    typeof(Entity),
                    "UpdateComponents"
                );
                MethodInfo screenPrefix = AccessTools.Method(
                    typeof(AdditionalPlayerScreenUpdatePatch),
                    "Prefix"
                );
                MethodInfo screenPostfix = AccessTools.Method(
                    typeof(AdditionalPlayerScreenUpdatePatch),
                    "Postfix"
                );
                MethodInfo jumpGameDraw = AccessTools.Method(typeof(JumpGame), "Draw");
                MethodInfo jumpGameDrawPrefix = AccessTools.Method(
                    typeof(MultiplayerDrawPatch),
                    "Prefix"
                );
                Type endingManagerType = AccessTools.TypeByName(
                    "JumpKing.GameManager.MultiEnding.EndingManager"
                );
                MethodInfo checkWin = endingManagerType == null ? null :
                    AccessTools.Method(endingManagerType, "CheckWin");
                MethodInfo checkWinPostfix = AccessTools.Method(
                    typeof(MultiplayerEndingPatch),
                    "Postfix"
                );

                if (gameUpdate == null || gameUpdatePrefix == null ||
                    inputGetState == null || inputGetPressedState == null ||
                    inputStatePrefix == null || playerUpdate == null ||
                    playerUpdatePrefix == null || playerSetSprite == null ||
                    spritePrefix == null || entityUpdateComponents == null ||
                    screenPrefix == null || screenPostfix == null ||
                    jumpGameDraw == null || jumpGameDrawPrefix == null ||
                    checkWin == null || checkWinPostfix == null)
                {
                    JumpKing.Program.crashLog.AddErrorMessage(
                        "Local Multiplayer patch target not found."
                    );
                    return;
                }

                _harmony = new Harmony("eski4869.LocalMultiplayerMod");
                _harmony.Patch(gameUpdate, prefix: new HarmonyMethod(gameUpdatePrefix));
                _harmony.Patch(inputGetState, prefix: new HarmonyMethod(inputStatePrefix));
                _harmony.Patch(inputGetPressedState, prefix: new HarmonyMethod(inputStatePrefix));
                _harmony.Patch(playerUpdate, prefix: new HarmonyMethod(playerUpdatePrefix));
                _harmony.Patch(playerSetSprite, prefix: new HarmonyMethod(spritePrefix));
                _harmony.Patch(
                    entityUpdateComponents,
                    prefix: new HarmonyMethod(screenPrefix),
                    postfix: new HarmonyMethod(screenPostfix)
                );
                _harmony.Patch(jumpGameDraw, prefix: new HarmonyMethod(jumpGameDrawPrefix));
                _harmony.Patch(checkWin, postfix: new HarmonyMethod(checkWinPostfix));
            }
            catch (Exception ex)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer patch failed: " + ex.Message
                );
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
                preferences.SingleMode.Player1Users,
                preferences.MultiplayerMode.Player1Users,
                preferences.MultiplayerMode.Player2Users,
                preferences.FourPlayerMode.Player1Users,
                preferences.FourPlayerMode.Player2Users,
                preferences.FourPlayerMode.Player3Users,
                preferences.FourPlayerMode.Player4Users
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

            if (preferences.MultiplayerMode == null)
            {
                preferences.MultiplayerMode = new MultiplayerModePreferences();
            }

            if (preferences.FourPlayerMode == null)
            {
                preferences.FourPlayerMode = new FourPlayerModePreferences();
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
            MultiplayerRuntime.SynchronizeBlockBehaviours();
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
    }

    public class SingleModePreferences
    {
        public string Player1Users { get; set; } = "*";
    }

    public class MultiplayerModePreferences
    {
        public string Player1Users { get; set; } = "[a-m]*";
        public string Player2Users { get; set; } = "[n-z]*";
    }

    public class FourPlayerModePreferences
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
