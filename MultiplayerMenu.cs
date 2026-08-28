using System;
using BehaviorTree;
using JumpKing;
using JumpKing.Controller;
using JumpKing.PauseMenu;
using JumpKing.PauseMenu.BT;
using JumpKing.PauseMenu.BT.Actions;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// One door per mode, and the door is also the way out.
    ///
    /// The settings that decide a session sit inside the thing that consumes
    /// them and do nothing until it is pressed. That press is the confirmation,
    /// and it is the moment <see cref="ModEntry.IsSessionLocked" /> has always
    /// described. Local settings stay in the local menu - online fixes the player
    /// count at two and does not touch the view, so putting them under Online
    /// would hide split screen behind a word that has nothing to do with it.
    ///
    /// Once a mode is running, its own entry becomes the way out of it and the
    /// other entry greys out. The two ways out of a lobby are not the same act
    /// and are not named the same: a guest leaves, and the host destroys the
    /// thing everyone else is in.
    /// </summary>
    internal static class MultiplayerMenu
    {
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
            MenuAction exit = new MenuAction(
                "Exit local multiplayer",
                menu,
                EndLocal
            );

            menu.AddChild(players);
            menu.AddChild(layout);
            menu.AddChild(start);
            menu.AddChild(exit);
            menu.Initialize();

            return new ModeEntrance(
                menu,
                () => ModEntry.PlayerCount > 1
                    ? "Exit local multiplayer"
                    : "Local multiplayer",
                new IMenuItem[] { players, layout, start },
                new IMenuItem[] { exit },
                () => ModEntry.PlayerCount > 1,
                () => ModEntry.IsSessionLocked
            );
        }

        public static ModeEntrance CreateOnline(GuiFormat format)
        {
            LocalMultiplayerBattleOption battle = new LocalMultiplayerBattleOption();
            NetworkModeOption network = new NetworkModeOption();
            InputDelayOption delay = new InputDelayOption();
            LocalMultiplayerJoinAction join = new LocalMultiplayerJoinAction();

            MenuSelector menu = new MenuSelector(format);
            MenuAction create = new MenuAction("Create lobby", menu, CreateLobby);

            // Inviting only means anything once a lobby exists, which is exactly
            // when the running half of this menu is the half on show.
            MenuAction invite = new MenuAction("Invite a friend", menu, Invite);
            MenuAction close = new MenuAction(CloseLabel, menu, CloseSession);

            menu.AddChild(battle);
            menu.AddChild(network);
            menu.AddChild(delay);
            menu.AddChild(create);
            menu.AddChild(join);
            menu.AddChild(invite);
            menu.AddChild(close);
            menu.Initialize();

            return new ModeEntrance(
                menu,
                () => ModEntry.IsSessionLocked ? CloseLabel() : "Online",
                new IMenuItem[] { battle, network, delay, create, join },
                new IMenuItem[] { invite, close },
                () => ModEntry.IsSessionLocked,
                () => ModEntry.PlayerCount > 1,
                // The delay line only exists as a question while the answer is not
                // being worked out for you.
                delay,
                () => !NetplaySettings.AutomaticDelay
            );
        }

        /// <summary>
        /// Leaving and destroying are different acts and the line says which one
        /// this is. A guest walks out of a lobby that carries on without them;
        /// the host ends the thing the other player is standing in.
        /// </summary>
        private static string CloseLabel()
        {
            return ModEntry.Netplay.IsHost ? "Destroy lobby" : "Leave lobby";
        }

        private static bool EndLocal()
        {
            return ModEntry.SetPlayerMode(1, ModEntry.TwoPlayerLayout);
        }

        private static bool CreateLobby()
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

        private static bool CloseSession()
        {
            ModEntry.Netplay.Leave();
            return true;
        }
    }

    /// <summary>
    /// Auto or Manual, cycled with left and right like every other option here.
    ///
    /// Auto cannot be answered when the lobby is created - there is nobody on the
    /// other end to measure yet - so it is answered when somebody joins, from the
    /// handshake's own round trip. Manual is the escape hatch for a connection
    /// that measures badly, or for someone who would rather decide.
    /// </summary>
    public class NetworkModeOption : IOptions
    {
        public NetworkModeOption() : base(
            2,
            NetplaySettings.AutomaticDelay ? 0 : 1,
            IOptions.EdgeMode.Wrap
        )
        {
        }

        protected override bool CanChange()
        {
            return !ModEntry.IsSessionLocked;
        }

        protected override string CurrentOptionName()
        {
            return CurrentOption == 0 ? "Network: Auto" : "Network: Manual";
        }

        protected override void OnOptionChange(int option)
        {
            NetplaySettings.AutomaticDelay = option == 0;
        }
    }

    /// <summary>
    /// The input delay in frames, chosen sideways rather than typed. Typing a
    /// number into a pause menu with a controller in your hands is the kind of
    /// thing nobody does twice.
    /// </summary>
    public class InputDelayOption : IOptions
    {
        public InputDelayOption() : base(
            RollbackPlan.MaxInputDelayFrames - RollbackPlan.MinInputDelayFrames + 1,
            NetplaySettings.ManualDelayFrames - RollbackPlan.MinInputDelayFrames,
            IOptions.EdgeMode.Clamp
        )
        {
        }

        protected override bool CanChange()
        {
            return !ModEntry.IsSessionLocked;
        }

        protected override string CurrentOptionName()
        {
            int frames = CurrentOption + RollbackPlan.MinInputDelayFrames;
            return "Input delay: " + frames + (frames == 1 ? " frame" : " frames");
        }

        protected override void OnOptionChange(int option)
        {
            NetplaySettings.ManualDelayFrames =
                option + RollbackPlan.MinInputDelayFrames;
        }
    }

    /// <summary>
    /// A top-level entry whose label, availability and contents follow the state
    /// of the mode it opens.
    ///
    /// It holds one <see cref="MenuSelector" /> and shows a different set of its
    /// lines either side of the action, rather than swapping in a second
    /// selector. That is forced, not preferred:
    /// <c>MenuFactory.TryCreateModSetting</c> takes the decorator's child at
    /// construction and registers only that one for drawing, so a selector handed
    /// over later would run without ever being drawn. <c>EnableMenuItem</c> and
    /// <c>DisableMenuItem</c> are the mechanism the game already has for this,
    /// down to recalculating the frame around whatever is currently shown.
    /// </summary>
    public sealed class ModeEntrance : TextButton
    {
        private readonly MenuSelector _menu;
        private readonly Func<string> _label;
        private readonly IMenuItem[] _idleItems;
        private readonly IMenuItem[] _runningItems;
        private readonly Func<bool> _isRunning;
        private readonly Func<bool> _isBlocked;
        private readonly IMenuItem _conditional;
        private readonly Func<bool> _conditionalShown;

        public ModeEntrance(
            MenuSelector menu,
            Func<string> label,
            IMenuItem[] idleItems,
            IMenuItem[] runningItems,
            Func<bool> isRunning,
            Func<bool> isBlocked
        )
            : this(menu, label, idleItems, runningItems, isRunning, isBlocked, null, null)
        {
        }

        /// <param name="conditional">
        /// A line that is only worth showing under some further condition, on top
        /// of being an idle-state line at all. Null when there is none.
        /// </param>
        public ModeEntrance(
            MenuSelector menu,
            Func<string> label,
            IMenuItem[] idleItems,
            IMenuItem[] runningItems,
            Func<bool> isRunning,
            Func<bool> isBlocked,
            IMenuItem conditional,
            Func<bool> conditionalShown
        )
            : base("", menu)
        {
            _menu = menu;
            _label = label;
            _idleItems = idleItems;
            _runningItems = runningItems;
            _isRunning = isRunning;
            _isBlocked = isBlocked;
            _conditional = conditional;
            _conditionalShown = conditionalShown;

            ShowLinesForState();
        }

        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                _label(),
                _isBlocked() ? Color.Gray : Color.White,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(_label());
        }

        protected override BTresult MyRun(TickData p_data)
        {
            // A menu already open stays open, whatever the state has become while
            // the player was inside it - except for the one line whose condition
            // the player changes from inside this very menu, which has to follow
            // them or the control they just used would appear to do nothing.
            if (last_result == BTresult.Running)
            {
                ShowConditional();
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
        /// Settings and the action before a mode is running; the things that only
        /// mean something once it is, after. Applied on the way in rather than
        /// held, because which half applies is a question about right now.
        /// </summary>
        private void ShowLinesForState()
        {
            bool running = _isRunning();
            Show(_idleItems, !running);
            Show(_runningItems, running);
            ShowConditional();
        }

        private void ShowConditional()
        {
            if (_conditional == null)
            {
                return;
            }

            Show(
                new IMenuItem[] { _conditional },
                !_isRunning() && _conditionalShown()
            );
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
        private readonly Func<string> _label;
        private readonly MenuSelector _menu;
        private readonly Func<bool> _act;

        public MenuAction(string label, MenuSelector menu, Func<bool> act)
            : this(() => label, menu, act)
        {
        }

        /// <param name="act">
        /// Returns whether the menu should close. False keeps it open, which is
        /// what an action worth repeating needs, and also what a refused action
        /// needs so the player can see that it did not take.
        /// </param>
        public MenuAction(Func<string> label, MenuSelector menu, Func<bool> act)
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
                _label(),
                Color.White,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(_label());
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
