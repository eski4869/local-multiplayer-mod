using JumpKing.Player;

namespace LocalMultiplayerMod
{
    public static class LocalMultiplayerApi
    {
        public const int ApiVersion = 1;
        private const int MaximumPlayers = 4;
        private static readonly InputComponent.State[] HeldStates =
            new InputComponent.State[MaximumPlayers];
        private static readonly InputComponent.State[] PressedStates =
            new InputComponent.State[MaximumPlayers];
        private static int _currentViewPlayerMask = 1;

        public static int GetApiVersion()
        {
            return ApiVersion;
        }

        public static bool IsActive()
        {
            return MultiplayerRuntime.IsActive;
        }

        public static int GetPlayerCount()
        {
            return ModEntry.PlayerCount;
        }

        public static int ResolvePlayerMask(string user)
        {
            return (int)ModEntry.ResolvePlayerTargets(user);
        }

        public static PlayerEntity GetPlayer(int playerNumber)
        {
            return MultiplayerRuntime.GetPlayer(playerNumber);
        }

        public static int GetCurrentViewPlayerMask()
        {
            return _currentViewPlayerMask;
        }

        public static void SubmitInput(
            int playerNumber,
            InputComponent.State held,
            InputComponent.State pressed
        )
        {
            int index = playerNumber - 1;
            if (index < 1 || index >= MaximumPlayers)
            {
                return;
            }

            HeldStates[index] = held;
            PressedStates[index] = pressed;
        }

        internal static InputComponent.State GetInputState(int playerNumber, bool pressed)
        {
            int index = playerNumber - 1;
            if (index < 1 || index >= MaximumPlayers)
            {
                return new InputComponent.State();
            }

            return pressed ? PressedStates[index] : HeldStates[index];
        }

        internal static void ClearInput(int playerNumber)
        {
            int index = playerNumber - 1;
            if (index < 1 || index >= MaximumPlayers)
            {
                return;
            }

            HeldStates[index] = new InputComponent.State();
            PressedStates[index] = new InputComponent.State();
        }

        internal static void SetCurrentViewPlayerMask(int mask)
        {
            _currentViewPlayerMask = mask == 0 ? 1 : mask;
        }
    }
}
