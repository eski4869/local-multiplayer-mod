using System;
using JumpKing.MiscEntities.WorldItems;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The integration boundary for consumer mods.
    ///
    /// The intent is that a gameplay mod stays multiplayer-agnostic: it resolves
    /// a user to one <see cref="PlayerEntity" /> and otherwise writes ordinary
    /// single-player code. This mod is responsible for mapping the one-player
    /// globals underneath it onto whichever player is being processed.
    ///
    /// Everything here is reflection-friendly (public static, base-game or
    /// primitive parameter types) so consumers can bind optionally and fall back
    /// to single player when this mod is absent.
    /// </summary>
    public static class LocalMultiplayerApi
    {
        public const int ApiVersion = 4;
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
            return MultiplayerRuntime.PlayerCount;
        }

        /// <summary>
        /// Resolves a chat user to the player they control, or null when the user
        /// routes nowhere. This is the call a command-driven mod should use.
        /// </summary>
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

        public static PlayerEntity GetPlayer(int playerNumber)
        {
            return MultiplayerRuntime.GetPlayer(playerNumber);
        }

        /// <summary>
        /// 1-based player number, or 0 when the entity is not a managed player.
        /// </summary>
        public static int GetPlayerNumber(PlayerEntity player)
        {
            return MultiplayerRuntime.GetPlayerNumber(player);
        }

        /// <summary>
        /// The player currently being processed, or null outside a player scope.
        /// Drawing and update code that needs to know "whose frame is this" should
        /// ask here rather than assuming player 1.
        /// </summary>
        public static PlayerEntity GetCurrentPlayer()
        {
            PlayerContext context = PlayerScope.Current;
            return context == null ? null : context.Player;
        }

        /// <summary>
        /// Runs an action with <paramref name="player" /> installed as the scoped
        /// player, so the camera, the screen that collision resolves against, and
        /// that player's item state all apply. Use this when a mod drives a
        /// specific player outside its normal update.
        ///
        /// Does not reorder the entity list, so it is safe to call from drawing
        /// code. Resolve the player through <see cref="ResolvePlayer" /> rather
        /// than <c>EntityManager.Find</c> inside the action.
        /// </summary>
        public static void RunAsPlayer(PlayerEntity player, Action action)
        {
            if (action == null)
            {
                return;
            }

            PlayerContext context = MultiplayerRuntime.GetContext(player);
            if (context == null)
            {
                action();
                return;
            }

            using (PlayerScope.Enter(context))
            {
                action();
            }
        }

        public static bool IsPlayerInCurrentView(PlayerEntity player)
        {
            int playerNumber = MultiplayerRuntime.GetPlayerNumber(player);
            return playerNumber > 0 &&
                (_currentViewPlayerMask & (1 << (playerNumber - 1))) != 0;
        }

        /// <summary>
        /// True while a mod's <c>[OnLevelStart]</c> is being replayed for an
        /// additional player. Process-wide setup should be skipped when this is
        /// set; per-player setup (block behaviours, player components) should not.
        /// </summary>
        public static bool IsSecondaryInitPass()
        {
            return LevelStartReplay.IsSecondaryPass;
        }

        /// <summary>
        /// Per-player item equip state. Items left untouched fall through to the
        /// base game global, so a player with no overrides behaves exactly as in
        /// single player.
        ///
        /// The base game reads equipped items from a global
        /// (<c>SkinManager.IsWearingSkin</c>, <c>InventoryManager.HasItemEnabled</c>)
        /// and toggles them from the local pad in <c>GameLoop</c>, so there is no
        /// per-player channel for Giant Boots or the Snake Ring. A mod that knows
        /// which user sent a toggle should route it here.
        /// </summary>
        public static bool IsItemEquipped(PlayerEntity player, int item)
        {
            PlayerContext context = MultiplayerRuntime.GetContext(player);
            return context != null &&
                context.Items.IsEquipped((Items)item);
        }

        public static bool SetItemEquipped(
            PlayerEntity player,
            int item,
            bool equipped
        )
        {
            PlayerContext context = MultiplayerRuntime.GetContext(player);
            if (context == null)
            {
                return false;
            }

            context.Items.SetEquipped((Items)item, equipped);
            return true;
        }

        /// <summary>
        /// Flips one item for one player and reports the new state. Returns false
        /// when the player is unknown or does not own the item.
        /// </summary>
        public static bool ToggleItem(PlayerEntity player, int item, out bool equipped)
        {
            equipped = false;
            PlayerContext context = MultiplayerRuntime.GetContext(player);
            if (context == null || !ItemToggles.CanToggle((Items)item))
            {
                return false;
            }

            equipped = !context.Items.IsEquipped((Items)item);
            context.Items.SetEquipped((Items)item, equipped);
            return true;
        }

        /// <summary>
        /// Per-player storage for consumer mods. Prefer this over a static field
        /// for anything conceptually owned by one player - a static turns into a
        /// cross-player bug the moment a second player exists.
        /// </summary>
        public static object GetPlayerState(PlayerEntity player, object key)
        {
            PlayerContext context = MultiplayerRuntime.GetContext(player);
            object value;
            return context != null && key != null &&
                context.State.TryGetValue(key, out value) ? value : null;
        }

        public static bool SetPlayerState(
            PlayerEntity player,
            object key,
            object value
        )
        {
            PlayerContext context = MultiplayerRuntime.GetContext(player);
            if (context == null || key == null)
            {
                return false;
            }

            context.State[key] = value;
            return true;
        }

        internal static void SetCurrentViewPlayerMask(int mask)
        {
            _currentViewPlayerMask = mask == 0 ? 1 : mask;
        }
    }
}
