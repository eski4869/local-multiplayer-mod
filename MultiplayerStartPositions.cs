using JumpKing.Workshop;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Optional per-player spawn points, so a map author can place additional
    /// players somewhere deliberate instead of always stacking them on player
    /// 1.
    ///
    /// A map that says nothing behaves exactly as before: this returns no data,
    /// and the caller falls back to spawning on player 1.
    /// </summary>
    internal static class MultiplayerStartPositions
    {
        /// <summary>
        /// The configured spawn for one additional player, if the map defines
        /// one. Player 1 is never looked up here - its spawn is the base game's
        /// own <c>StartData</c>, read the base game's own way.
        /// </summary>
        public static bool TryGet(
            int playerNumber,
            out Vector2 position,
            out Vector2 velocity
        )
        {
            position = Vector2.Zero;
            velocity = Vector2.Zero;

            Level.StartPosition? data = Read(playerNumber);
            if (!data.HasValue || !data.Value.Position.HasValue)
            {
                return false;
            }

            position = data.Value.Position.Value;
            velocity = data.Value.Velocity ?? Vector2.Zero;
            return true;
        }

        private static Level.StartPosition? Read(int playerNumber)
        {
            LocalMultiplayerStartPositions positions =
                LocalMultiplayerLevelFile.Load()?.StartPositions;
            if (positions == null)
            {
                return null;
            }

            switch (playerNumber)
            {
                case 2: return positions.Player2;
                case 3: return positions.Player3;
                case 4: return positions.Player4;
                default: return null;
            }
        }

        public static void Reset()
        {
            LocalMultiplayerLevelFile.Reset();
        }
    }
}
