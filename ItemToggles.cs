using JumpKing.MiscEntities.WorldItems;
using JumpKing.MiscEntities.WorldItems.Inventory;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Gives the two in-run toggleable items a per-player identity.
    ///
    /// The base game has no per-player channel for them at all: <c>GameLoop</c>
    /// reads the toggle straight off <c>ControllerManager.GetPressedPadState()</c>
    /// - the one physical pad - and applies it to the global
    /// <c>SkinManager</c>. So a second player has no way to ask for Giant Boots,
    /// and if the first player equips them everybody gets them.
    ///
    /// Here each player carries its own override for those two items, seeded from
    /// whatever the global state was when the player was created. Every other item
    /// stays global, which keeps cosmetics and one-off items behaving as they do
    /// in single player.
    /// </summary>
    internal static class ItemToggles
    {
        internal static readonly Items[] PerPlayerItems =
        {
            Items.GiantBoots,
            Items.SnakeRing
        };

        public static bool IsPerPlayerItem(Items item)
        {
            for (int i = 0; i < PerPlayerItems.Length; i++)
            {
                if (PerPlayerItems[i] == item)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool CanToggle(Items item)
        {
            return InventoryManager.GetItemCount(item) > 0;
        }

        /// <summary>
        /// Copies the current global equip state onto a freshly created context so
        /// the player starts out matching what is on screen, then owns it.
        ///
        /// Only done in multiplayer. In single player no override is ever set, the
        /// shims never intercept, and the game behaves exactly as it does without
        /// this mod installed.
        /// </summary>
        public static void Seed(PlayerContext context)
        {
            if (context == null || !ModEntry.IsMultiplayerEnabled)
            {
                return;
            }

            for (int i = 0; i < PerPlayerItems.Length; i++)
            {
                Items item = PerPlayerItems[i];
                context.ItemsInternal.SetEquipped(
                    item,
                    GlobalItemState.IsWearingSkinUnpatched(item)
                );
            }
        }

        /// <summary>
        /// Seeds a context that predates multiplayer being switched on, without
        /// discarding a choice that player has already made.
        /// </summary>
        public static void SeedIfUnset(PlayerContext context)
        {
            if (context == null ||
                context.ItemsInternal.HasOverride(PerPlayerItems[0]))
            {
                return;
            }

            Seed(context);
        }

        /// <summary>
        /// Hands the items back to the base game global on the way out of
        /// multiplayer, so single player is untouched by anything set here.
        /// </summary>
        public static void ClearOverrides(PlayerContext context)
        {
            if (context != null)
            {
                context.ItemsInternal.Clear();
            }
        }

        /// <summary>
        /// Mirrors a global equip change onto the player it belongs to: the scoped
        /// player if there is one, otherwise player 1, who owns the local pad and
        /// the pause menu.
        /// </summary>
        public static void OnGlobalSkinChanged(Items item, bool equipped)
        {
            if (!ItemToggles.IsPerPlayerItem(item) ||
                !ModEntry.IsMultiplayerEnabled)
            {
                return;
            }

            PlayerContext context = PlayerScope.Current ??
                MultiplayerRuntime.GetContext(1);
            if (context == null)
            {
                return;
            }

            context.ItemsInternal.SetEquipped(item, equipped);
        }
    }

    /// <summary>
    /// Routes a global equip change to one player instead of to everyone.
    ///
    /// Patched on <c>EnableSkin</c> / <c>DisableSkin</c> rather than
    /// <c>SetSkinEnabled</c>: those are the real funnel. <c>AddSkinSprite</c>
    /// calls <c>EnableSkin</c> directly when workshop skin content loads, which
    /// would otherwise leave a player's override stale.
    /// </summary>
    internal static class SkinManagerEnableSkinPatch
    {
        public static void Postfix(Items __0)
        {
            ItemToggles.OnGlobalSkinChanged(__0, true);
        }
    }

    internal static class SkinManagerDisableSkinPatch
    {
        public static void Postfix(Items __0)
        {
            ItemToggles.OnGlobalSkinChanged(__0, false);
        }
    }
}
