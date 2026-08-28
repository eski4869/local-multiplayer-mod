using System;
using BehaviorTree;
using JumpKing;
using JumpKing.Controller;
using JumpKing.PauseMenu;
using JumpKing.PauseMenu.BT;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// One door per mode, and the door is also the way out.
    ///
    /// The first attempt put all seven of the mod's lines inside a single
    /// "Online" folder. That was wrong twice over: the player count and the split
    /// layout are <em>local</em> settings - online fixes the count at two and
    /// leaves the view alone - so hiding them behind "Online" meant a player who
    /// wanted two-player split screen had to open the online menu to find them.
    /// And nothing confirmed anything: settings applied as they were cycled, and
    /// an unrelated line further down opened a lobby.
    ///
    /// So local and online each get their own entry. Opening one shows its
    /// settings, and those settings do nothing until Start is pressed. That press
    /// is the confirmation, and it is the moment
    /// <see cref="ModEntry.IsSessionLocked" /> has always described.
    ///
    /// Once a mode is running, its entry becomes the way to end it and the other
    /// entry greys out. No separate "leave" line sits inert in the list, because
    /// the way in is the way out.
    /// </summary>
    internal static class MultiplayerMenu
    {
        private const string EndLabel = "End multiplayer";

        public static ModeEntrance CreateLocal(GuiFormat format)
        {
            LocalMultiplayerModeOption players = new LocalMultiplayerModeOption();
            LocalMultiplayerSplitOption layout = new LocalMultiplayerSplitOption();

            MenuSelector menu = new MenuSelector(format);
            MenuAction start = new MenuAction(
                "Start",
                menu,
                () => ModEntry.SetPlayerMode(
                    players.SelectedPlayerCount,
                    layout.SelectedLayout
                )
            );
            MenuAction end = new MenuAction(EndLabel, menu, EndLocal);

            menu.AddChild(players);
            menu.AddChild(layout);
            menu.AddChild(start);
            menu.AddChild(end);
            menu.Initialize();

            return new ModeEntrance(
                "Local multiplayer",
                EndLabel,
                menu,
                new IMenuItem[] { players, layout, start },
                new IMenuItem[] { end },
                () => ModEntry.PlayerCount > 1,
                () => ModEntry.IsSessionLocked
            );
        }

        public static ModeEntrance CreateOnline(GuiFormat format)
        {
            LocalMultiplayerBattleOption battle = new LocalMultiplayerBattleOption();
            LocalMultiplayerJoinAction join = new LocalMultiplayerJoinAction();

            MenuSelector menu = new MenuSelector(format);
            MenuAction start = new MenuAction("Start", menu, StartOnline);

            // Inviting only means anything once a lobby exists, which is exactly
            // when the running half of this menu is the half on show.
            MenuAction invite = new MenuAction("Invite a friend", menu, Invite);
            MenuAction end = new MenuAction(EndLabel, menu, EndOnline);

            menu.AddChild(battle);
            menu.AddChild(start);
            menu.AddChild(join);
            menu.AddChild(invite);
            menu.AddChild(end);
            menu.Initialize();

            return new ModeEntrance(
                "Online",
                EndLabel,
                menu,
                new IMenuItem[] { battle, start, join },
                new IMenuItem[] { invite, end },
                () => ModEntry.IsSessionLocked,
                () => ModEntry.PlayerCount > 1
            );
        }

        private static bool EndLocal()
        {
            return ModEntry.SetPlayerMode(1, ModEntry.TwoPlayerLayout);
        }

        private static bool StartOnline()
        {
            if (ModEntry.Netplay.Current != NetplaySession.Phase.Idle)
            {
                return false;
            }

            ModEntry.Netplay.Host();
            return true;
        }

        private static bool Invite()
        {
            ModEntry.Netplay.Invite();

            // Stays open. Inviting one friend is no reason to lose the ability to
            // invite another, and the session is running either way.
            return false;
        }

        private static bool EndOnline()
        {
            ModEntry.Netplay.Leave();
            return true;
        }
    }

    /// <summary>
    /// A top-level entry whose label, availability and contents follow the state
    /// of the mode it opens.
    ///
    /// It holds one <see cref="MenuSelector" /> and shows a different set of its
    /// lines either side of Start, rather than swapping in a second selector.
    /// That is not a style choice: <c>MenuFactory.TryCreateModSetting</c> takes
    /// the decorator's child <em>at construction</em> and registers only that one
    /// for drawing, so a selector handed over later would run without ever being
    /// drawn. <c>EnableMenuItem</c> and <c>DisableMenuItem</c> are the mechanism
    /// the game already has for this, down to recalculating the frame around
    /// whatever is currently shown.
    /// </summary>
    public sealed class ModeEntrance : TextButton
    {
        private readonly string _idleLabel;
        private readonly string _endLabel;
        private readonly MenuSelector _menu;
        private readonly IMenuItem[] _idleItems;
        private readonly IMenuItem[] _runningItems;
        private readonly Func<bool> _isRunning;
        private readonly Func<bool> _isBlocked;

        public ModeEntrance(
            string idleLabel,
            string endLabel,
            MenuSelector menu,
            IMenuItem[] idleItems,
            IMenuItem[] runningItems,
            Func<bool> isRunning,
            Func<bool> isBlocked
        )
            : base(idleLabel, menu)
        {
            _idleLabel = idleLabel;
            _endLabel = endLabel;
            _menu = menu;
            _idleItems = idleItems;
            _runningItems = runningItems;
            _isRunning = isRunning;
            _isBlocked = isBlocked;

            ShowLinesForState();
        }

        private string Label
        {
            get { return _isRunning() ? _endLabel : _idleLabel; }
        }

        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                Label,
                _isBlocked() ? Color.Gray : Color.White,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(Label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            // A menu already open stays open, whatever the state has become while
            // the player was inside it. Deciding again here would rebuild the list
            // under them the moment Start changed the answer.
            if (last_result == BTresult.Running)
            {
                return base.MyRun(p_data);
            }

            if (_isBlocked())
            {
                return BTresult.Failure;
            }

            ShowLinesForState();
            return base.MyRun(p_data);
        }

        /// <summary>
        /// Settings and Start before a mode is running; the things that only mean
        /// something once it is, after. Applied on the way in rather than held,
        /// because which half applies is a question about right now.
        /// </summary>
        private void ShowLinesForState()
        {
            bool running = _isRunning();
            Show(_idleItems, !running);
            Show(_runningItems, running);
        }

        private void Show(IMenuItem[] items, bool shown)
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (shown)
                {
                    _menu.EnableMenuItem(items[i]);
                }
                else
                {
                    _menu.DisableMenuItem(items[i]);
                }
            }
        }
    }

    /// <summary>
    /// A line that does one thing and then closes the menu it lives in.
    ///
    /// Closing is <see cref="MenuSelector.SetResult" />. A selector returns
    /// Running for as long as it is open, and only a set result or a cancel ends
    /// it - so an action that merely succeeded would leave the menu sitting there
    /// looking as though nothing had happened.
    /// </summary>
    internal sealed class MenuAction : IBTSimpleMenuItem
    {
        private readonly string _label;
        private readonly MenuSelector _menu;
        private readonly Func<bool> _act;

        /// <param name="act">
        /// Returns whether the menu should close. False keeps it open, which is
        /// what an action worth repeating needs, and also what a refused action
        /// needs so the player can see that it did not take.
        /// </param>
        public MenuAction(string label, MenuSelector menu, Func<bool> act)
        {
            _label = label;
            _menu = menu;
            _act = act;
        }

        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                _label,
                Color.White,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(_label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            if (!ControllerManager.instance.MenuController.GetPadState().confirm)
            {
                return BTresult.Failure;
            }

            ControllerManager.instance.MenuController.ConsumePadPresses();

            if (_act())
            {
                _menu.SetResult(BTresult.Success);
            }

            return BTresult.Success;
        }
    }
}
