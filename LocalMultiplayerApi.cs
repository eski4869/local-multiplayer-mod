using JumpKing.Player;

namespace LocalMultiplayerMod
{
    public static class LocalMultiplayerApi
    {
        public const int ApiVersion = 3;
        private static int _currentViewPlayerMask = 1;

        public static int GetApiVersion()
        {
            return ApiVersion;
        }

        public static bool IsActive()
        {
            return MultiplayerRuntime.IsActive;
        }

        public static PlayerEntity ResolvePlayer(string user)
        {
            switch (ModEntry.ResolvePlayerTargets(user))
            {
                case PlayerTargets.Player1:
                    return MultiplayerRuntime.GetPlayer(1);
                case PlayerTargets.Player2:
                    return MultiplayerRuntime.GetPlayer(2);
                case PlayerTargets.Player3:
                    return MultiplayerRuntime.GetPlayer(3);
                case PlayerTargets.Player4:
                    return MultiplayerRuntime.GetPlayer(4);
                default:
                    return null;
            }
        }

        public static bool IsPlayerInCurrentView(PlayerEntity player)
        {
            int playerNumber = MultiplayerRuntime.GetPlayerNumber(player);
            return playerNumber > 0 &&
                (_currentViewPlayerMask & (1 << (playerNumber - 1))) != 0;
        }

        internal static void SetCurrentViewPlayerMask(int mask)
        {
            _currentViewPlayerMask = mask == 0 ? 1 : mask;
        }
    }
}
