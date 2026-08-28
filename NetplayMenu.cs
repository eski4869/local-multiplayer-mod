using JumpKing.PauseMenu;
using JumpKing.PauseMenu.BT;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// One way in and one way out.
    ///
    /// Everything this mod adds used to sit as seven separate lines in the mod's
    /// own options list, which put the settings beside the thing they configure
    /// rather than inside it. There was no moment of confirmation: a player set a
    /// count, a layout and a battle flag, then pressed an unrelated line to open
    /// a lobby, and nothing ever said the settings had been taken.
    ///
    /// Here they are one entry that opens a menu of its own. The settings sit
    /// above the action that consumes them, so pressing it is the confirmation -
    /// which is also what the "session settings are fixed before the lobby opens"
    /// decision has always meant, finally shown rather than only enforced.
    ///
    /// No new drawing code, and none is needed. <see cref="TextButton" /> is an
    /// <c>IBTMenuDecorator</c>, and <c>MenuFactory.TryCreateModSetting</c> looks
    /// for exactly that shape - a decorator whose child is a
    /// <see cref="MenuSelector" /> - and registers the submenu for drawing
    /// itself, recursing so nesting works too. Returning one from a
    /// <c>[PauseMenuItemSetting]</c> method is a supported case, not a trick.
    /// </summary>
    internal static class NetplayMenu
    {
        /// <summary>
        /// The label the entry carries in the mod's options list.
        ///
        /// Deliberately not "Netplay" or "Settings". A player who does not
        /// already know this mod has an online mode has to be able to find it
        /// from this one word.
        /// </summary>
        private const string Label = "Online";

        public static TextButton Create(GuiFormat format)
        {
            MenuSelector menu = new MenuSelector(format);

            // Settings first, in the order a session is decided: how many are
            // playing, how the screen is shared, and what the rules are.
            menu.AddChild(new LocalMultiplayerModeOption());
            menu.AddChild(new LocalMultiplayerSplitOption());
            menu.AddChild(new LocalMultiplayerBattleOption());

            // Then the actions those settings feed. Host reads what is above it,
            // which is the whole reason they are above it.
            menu.AddChild(new LocalMultiplayerHostAction());
            menu.AddChild(new LocalMultiplayerInviteAction());
            menu.AddChild(new LocalMultiplayerJoinAction());
            menu.AddChild(new LocalMultiplayerLeaveAction());

            // Adds the back entry itself - Initialize's first argument defaults
            // to true - so the way out costs nothing and looks like every other
            // menu in the game.
            menu.Initialize();

            return new TextButton(Label, menu);
        }
    }
}
