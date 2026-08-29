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
    /// Which mode a machine can be in. Two, and only one at a time.
    ///
    /// The kind exists so that "is anything else running" can be worked out
    /// rather than written down per entry. Written down, it was wrong: the online
    /// entry asked whether the player count was above one, which netplay itself
    /// makes true, so a session greyed out the only way to end it.
    /// </summary>
    internal enum ModeKind
    {
        Local,
        Online
    }

    /// <summary>
    /// One line on a page: the item, and when it is there.
    ///
    /// Every line that comes and goes goes through this - the settings before a
    /// mode starts, the actions that only exist after, the delay that only exists
    /// under Manual, a lobby that has only just been found. There was a separate
    /// mechanism for each of those, and each arrived as another argument on
    /// something that already had too many.
    /// </summary>
    internal sealed class MenuLine
    {
        public readonly IBTSimpleMenuItem Item;
        public readonly Func<bool> When;

        /// <summary>The page this line opens, so refreshing reaches it too.</summary>
        public readonly Page Opens;

        private MenuLine(IBTSimpleMenuItem item, Func<bool> when, Page opens)
        {
            Item = item;
            When = when;
            Opens = opens;
        }

        public static MenuLine Always(IBTSimpleMenuItem item)
        {
            return new MenuLine(item, null, null);
        }

        public static MenuLine Shown(IBTSimpleMenuItem item, Func<bool> when)
        {
            return new MenuLine(item, when, null);
        }

        /// <summary>
        /// A line that opens a page of its own. Nesting is safe built this way and
        /// only this way: <c>MenuFactory</c> registers a submenu for drawing by
        /// walking the tree it is handed at construction, so a page in place from
        /// the start is drawn and one substituted later is not.
        /// </summary>
        /// <param name="whileOpen">
        /// Run on every tick the page is open, and on none while it is not. A
        /// list of what is out there has to keep asking while somebody is looking
        /// at it, and asking on the way in only would show them one snapshot of a
        /// moment that has passed.
        /// </param>
        public static MenuLine Opening(
            string label,
            Page page,
            Func<bool> when,
            Action whileOpen = null
        )
        {
            return new MenuLine(
                new PageDoor(label, page, whileOpen),
                when,
                page
            );
        }
    }

    /// <summary>
    /// A door onto a page, which can keep something running for as long as it is
    /// standing open.
    /// </summary>
    internal sealed class PageDoor : TextButton
    {
        private readonly Action _whileOpen;

        public PageDoor(string label, Page page, Action whileOpen)
            : base(label, page.Selector)
        {
            _whileOpen = whileOpen;
        }

        protected override BTresult MyRun(TickData p_data)
        {
            BTresult result = base.MyRun(p_data);

            // Running is the selector saying it is still on screen. Anything else
            // means the page is shut, and a shut page has nothing to keep doing.
            if (result == BTresult.Running && _whileOpen != null)
            {
                _whileOpen();
            }

            return result;
        }
    }

    /// <summary>
    /// A menu whose lines answer for themselves whether they are there.
    ///
    /// Refreshed every tick rather than only on the way in. A lobby list fills as
    /// replies arrive, and a session can start while its own menu is open, so a
    /// page that decided once would be describing a moment that has passed.
    /// </summary>
    internal sealed class Page
    {
        private readonly MenuSelector _selector;
        private readonly MenuLine[] _lines;

        public Page(GuiFormat format, params MenuLine[] lines)
        {
            _selector = new MenuSelector(format);
            _lines = lines;

            for (int i = 0; i < lines.Length; i++)
            {
                _selector.AddChild(lines[i].Item);
            }

            _selector.Initialize();
            Refresh();
        }

        public MenuSelector Selector
        {
            get { return _selector; }
        }

        public void Refresh()
        {
            for (int i = 0; i < _lines.Length; i++)
            {
                MenuLine line = _lines[i];

                if (line.When == null || line.When())
                {
                    _selector.EnableMenuItem(line.Item);
                }
                else
                {
                    _selector.DisableMenuItem(line.Item);
                }

                if (line.Opens != null)
                {
                    line.Opens.Refresh();
                }
            }
        }

        public void Close()
        {
            _selector.SetResult(BTresult.Success);
        }
    }

    /// <summary>
    /// One door per mode, and the door is also the way out.
    ///
    /// A setting sits inside the thing that consumes it and does nothing until
    /// that thing is pressed, so the press is the confirmation. Local settings
    /// stay in the local menu, because online fixes the player count at two and
    /// never touches the view - filing split screen under "Online" would hide it
    /// behind a word with nothing to do with it.
    ///
    /// What a lobby will be is decided one level in, behind Create lobby, because
    /// those settings are not alternatives to creating a lobby: they are the
    /// description of the one about to be created. Beside it they read as a list
    /// of unrelated things to press. Joining is a level in for the same reason -
    /// choosing among somebody's lobbies is its own question, and it used to be
    /// asked by cycling a single line sideways.
    /// </summary>
    internal static class MultiplayerMenu
    {
        /// <summary>
        /// How many found lobbies the list can show. Fixed, so the page is built
        /// once and its lines appear as results arrive, rather than the menu being
        /// rebuilt under somebody while they are reading it.
        /// </summary>
        private const int LobbySlots = 10;

        public static bool IsRunning(ModeKind kind)
        {
            return kind == ModeKind.Local
                ? ModEntry.IsLocalMultiplayerActive
                : ModEntry.IsSessionLocked;
        }

        /// <summary>
        /// Derived, never handed in. A mode cannot block itself out of its own
        /// session this way, whatever anybody writes at the call site.
        /// </summary>
        public static bool AnyOtherRunning(ModeKind mine)
        {
            return (mine != ModeKind.Local && IsRunning(ModeKind.Local))
                || (mine != ModeKind.Online && IsRunning(ModeKind.Online));
        }

        public static Slot CreateLocal(GuiFormat format)
        {
            LocalMultiplayerModeOption players = new LocalMultiplayerModeOption();
            LocalMultiplayerSplitOption layout = new LocalMultiplayerSplitOption();

            Func<bool> idle = delegate { return !IsRunning(ModeKind.Local); };
            Func<bool> running = delegate { return IsRunning(ModeKind.Local); };

            MenuAction start = new MenuAction(
                "Start",
                delegate
                {
                    return ModEntry.SetPlayerMode(
                        players.SelectedPlayerCount,
                        layout.SelectedLayout
                    );
                }
            );
            MenuAction exit = new MenuAction("Exit local multiplayer", EndLocal);

            Page page = new Page(
                format,
                MenuLine.Shown(players, idle),
                MenuLine.Shown(layout, idle),
                MenuLine.Shown(start, idle),
                MenuLine.Shown(exit, running)
            );

            start.Closes(page);
            exit.Closes(page);

            ModeEntrance entrance = new ModeEntrance(
                ModeKind.Local,
                page,
                delegate
                {
                    return IsRunning(ModeKind.Local)
                        ? "Exit local multiplayer"
                        : "Local multiplayer";
                }
            );

            // While a session is up this slot is the invitation instead. Local
            // multiplayer is not merely unavailable then, it is beside the point:
            // a line nobody can press is still a line to read past.
            return new Slot(
                MenuLine.Shown(
                    new MenuAction("Invite a friend", Invite),
                    delegate { return IsRunning(ModeKind.Online); }
                ),
                MenuLine.Always(entrance)
            );
        }

        public static Slot CreateOnline(GuiFormat format)
        {
            Func<bool> idle = delegate { return !IsRunning(ModeKind.Online); };
            Func<bool> running = delegate { return IsRunning(ModeKind.Online); };

            // What the lobby will be.
            MenuAction create = new MenuAction("Create", CreateLobby);
            Page setup = new Page(
                format,
                MenuLine.Always(new LocalMultiplayerBattleOption()),
                MenuLine.Always(new NetworkModeOption()),
                MenuLine.Shown(
                    new InputDelayOption(),
                    delegate { return !NetplaySettings.AutomaticDelay; }
                ),
                MenuLine.Always(create)
            );

            // Whose lobby to join.
            LobbySlot[] slots = new LobbySlot[LobbySlots];
            MenuLine[] lines = new MenuLine[LobbySlots + 3];

            // Nothing was ever searching. The list showed "searching..." whenever
            // it was empty and no search had been started, because the redesign
            // moved the display of the results across and left the asking behind:
            // Join used to start a search on the press that opened it, and after
            // the rewrite nothing called it at all. It asks once a second for as
            // long as the page is open now, so a friend who opens a lobby while
            // somebody is looking at the list turns up in it.
            lines[0] = MenuLine.Shown(
                new MenuText("searching..."),
                delegate { return ModEntry.Netplay.IsSearching; }
            );

            lines[1] = MenuLine.Shown(
                new MenuText("nothing found"),
                delegate
                {
                    return ModEntry.Netplay.HasSearched &&
                        !ModEntry.Netplay.IsSearching &&
                        ModEntry.Netplay.Found.Count == 0;
                }
            );

            for (int i = 0; i < LobbySlots; i++)
            {
                int slot = i;
                slots[slot] = new LobbySlot(slot);
                lines[slot + 2] = MenuLine.Shown(
                    slots[slot],
                    delegate { return slot < ModEntry.Netplay.Found.Count; }
                );
            }

            // Said rather than silently dropped. Ten is not a ranking - Steam
            // returns them in its own order and there is nothing here to rank them
            // by - so a list that quietly stopped at ten would be hiding lobbies
            // without admitting which.
            lines[LobbySlots + 2] = MenuLine.Shown(
                new MenuText("...and more"),
                delegate { return ModEntry.Netplay.Found.Count > LobbySlots; }
            );

            Page browse = new Page(format, lines);
            for (int i = 0; i < LobbySlots; i++)
            {
                slots[i].Closes(browse);
            }

            // Only the two ways in. Once a session exists this page is not
            // reachable at all - the slot below shows the way out in its place -
            // so it has no state to describe but the one before anything started.
            Page page = new Page(
                format,
                MenuLine.Opening("Create lobby", setup, null),
                MenuLine.Opening("Join", browse, null, KeepSearching)
            );

            // Creating answers the question the whole branch was asking, so it
            // leaves the description of a thing that now exists rather than
            // sitting inside it.
            create.Closes(setup, page);

            ModeEntrance entrance = new ModeEntrance(
                ModeKind.Online,
                page,
                delegate { return "Online"; }
            );

            // A door labelled Destroy lobby with an invitation inside it was the
            // wrong shape. During a session there is nothing to fold: the two
            // things left to do are invite and end it, and they are the whole of
            // the menu rather than the contents of something else.
            return new Slot(
                MenuLine.Shown(
                    new MenuAction(CloseLabel, CloseSession),
                    running
                ),
                MenuLine.Always(entrance)
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

        /// <summary>
        /// How long to leave between asking Steam again. Once a second: often
        /// enough that a lobby opened while somebody is reading the list appears
        /// in it, and rare enough not to be hammering matchmaking from a menu.
        /// </summary>
        private const int SearchIntervalMilliseconds = 1000;

        private static int _lastSearchAt;

        /// <summary>
        /// Asks again, for as long as the list is being looked at. A search
        /// already out is left alone - overlapping requests would answer out of
        /// order and the later reply is not necessarily the newer one.
        /// </summary>
        private static void KeepSearching()
        {
            if (ModEntry.Netplay.IsSearching ||
                ModEntry.Netplay.Current != NetplaySession.Phase.Idle)
            {
                return;
            }

            int now = Environment.TickCount;
            if (_lastSearchAt != 0 &&
                unchecked(now - _lastSearchAt) < SearchIntervalMilliseconds)
            {
                return;
            }

            _lastSearchAt = now;
            ModEntry.Netplay.Join();
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
    /// One place in the mod's options list, showing whichever of its alternatives
    /// applies now.
    ///
    /// The game's own list has no idea about any of this and cannot be asked to
    /// hide a line, so a slot that must disappear instead becomes something else.
    /// It is the same condition-per-line idea as <see cref="Page" />, one level
    /// up, and it is what lets a session replace both entries outright rather than
    /// greying one and burying the other.
    /// </summary>
    public sealed class Slot : IBTSimpleMenuItem
    {
        private readonly MenuLine[] _alternatives;

        internal Slot(params MenuLine[] alternatives)
        {
            _alternatives = alternatives;
        }

        /// <summary>The first whose condition holds. The last should have none.</summary>
        private IBTSimpleMenuItem Active
        {
            get
            {
                for (int i = 0; i < _alternatives.Length; i++)
                {
                    MenuLine line = _alternatives[i];
                    if (line.When == null || line.When())
                    {
                        return line.Item;
                    }
                }

                return null;
            }
        }

        public override void Draw(int x, int y, bool selected)
        {
            IBTSimpleMenuItem active = Active;
            if (active != null)
            {
                active.Draw(x, y, selected);
            }
        }

        public override Point GetSize()
        {
            IBTSimpleMenuItem active = Active;
            return active == null ? Point.Zero : active.GetSize();
        }

        protected override BTresult MyRun(TickData p_data)
        {
            IBTSimpleMenuItem active = Active;
            return active == null ? BTresult.Failure : active.Run(p_data);
        }
    }

    /// <summary>
    /// A top-level entry: what it is called, what it opens, and whether it can be
    /// used at all. Everything conditional about the page itself belongs to the
    /// page.
    /// </summary>
    public sealed class ModeEntrance : TextButton
    {
        private readonly ModeKind _kind;
        private readonly Page _page;
        private readonly Func<string> _label;

        internal ModeEntrance(ModeKind kind, Page page, Func<string> label)
            : base("", page.Selector)
        {
            _kind = kind;
            _page = page;
            _label = label;
        }

        private bool Blocked
        {
            get { return MultiplayerMenu.AnyOtherRunning(_kind); }
        }

        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                _label(),
                Blocked ? Color.Gray : Color.White,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(_label());
        }

        protected override BTresult MyRun(TickData p_data)
        {
            _page.Refresh();

            if (last_result != BTresult.Running && Blocked)
            {
                return BTresult.Failure;
            }

            return base.MyRun(p_data);
        }
    }

    /// <summary>
    /// A line that does one thing, and closes the pages that thing has answered.
    /// </summary>
    internal class MenuAction : IBTSimpleMenuItem
    {
        private readonly Func<string> _label;
        private readonly Func<bool> _act;
        private Page[] _closes = new Page[0];

        public MenuAction(string label, Func<bool> act)
            : this(delegate { return label; }, act)
        {
        }

        /// <param name="act">
        /// Returns whether the action took. False leaves every page open, which is
        /// what a repeatable action needs, and what a refused one needs so the
        /// player can see it did not happen.
        /// </param>
        public MenuAction(Func<string> label, Func<bool> act)
        {
            _label = label;
            _act = act;
        }

        public void Closes(params Page[] pages)
        {
            _closes = pages;
        }

        protected virtual string Label
        {
            get { return _label(); }
        }

        protected virtual bool Act()
        {
            return _act();
        }

        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                Label,
                Color.White,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(Label);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            if (!ControllerManager.instance.MenuController.GetPadState().confirm)
            {
                return BTresult.Failure;
            }

            ControllerManager.instance.MenuController.ConsumePadPresses();

            if (Act())
            {
                for (int i = 0; i < _closes.Length; i++)
                {
                    _closes[i].Close();
                }
            }

            return BTresult.Success;
        }
    }

    /// <summary>One found lobby, named by whoever is hosting it.</summary>
    internal sealed class LobbySlot : MenuAction
    {
        private readonly int _slot;

        public LobbySlot(int slot)
            : base(string.Empty, null)
        {
            _slot = slot;
        }

        protected override string Label
        {
            get
            {
                var found = ModEntry.Netplay.Found;
                return _slot < found.Count
                    ? (found[_slot].HostName ?? "a friend")
                    : string.Empty;
            }
        }

        protected override bool Act()
        {
            return ModEntry.Netplay.JoinFound(_slot);
        }
    }

    /// <summary>Something to read rather than press.</summary>
    internal sealed class MenuText : IBTSimpleMenuItem
    {
        private readonly string _text;

        public MenuText(string text)
        {
            _text = text;
        }

        public override void Draw(int x, int y, bool selected)
        {
            MenuItemHelper.Draw(
                x,
                y,
                _text,
                Color.Gray,
                Game1.instance.contentManager.font.MenuFont
            );
        }

        public override Point GetSize()
        {
            return MenuItemHelper.GetSize(_text);
        }

        protected override BTresult MyRun(TickData p_data)
        {
            return BTresult.Failure;
        }
    }

    /// <summary>
    /// Auto or Manual, cycled with left and right like every other option here.
    ///
    /// Auto is answered when somebody joins, from the handshake's own round trip,
    /// because when the lobby is created there is nobody on the other end to
    /// measure. Manual is for a connection that measures badly, or for somebody
    /// who would rather decide.
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
}
