using System;
using System.Reflection;
using HarmonyLib;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Says what just happened, on screen.
    ///
    /// A netplay session changes state at moments the player is not looking at a
    /// menu: somebody joins, the connection settles, the peer leaves, a level does
    /// not match. Reporting those only through the crash log means the player finds
    /// out by opening a menu and reading a label, or does not find out at all.
    ///
    /// Sent through EskiUI when it is installed, which is the ecosystem's answer to
    /// in-game notices, and reached by name so that it stays optional: nothing here
    /// requires it, and without it the message still reaches the log. No mod may
    /// require another to exist.
    /// </summary>
    internal static class NetplayNotice
    {
        private static bool _resolved;
        private static MethodInfo _notify;

        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            // Always logged, whether or not it was also shown. The log is what gets
            // sent back when something went wrong.
            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer netplay: " + message
            );

            Resolve();
            if (_notify == null)
            {
                return;
            }

            try
            {
                _notify.Invoke(null, new object[] { "Netplay: " + message });
            }
            catch
            {
                // A notice that cannot be drawn must not take the session with it.
                _notify = null;
            }
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;

            Type ui = AccessTools.TypeByName("EskiUI");
            if (ui == null)
            {
                return;
            }

            _notify = AccessTools.Method(ui, "Notify", new[] { typeof(string) });
        }
    }
}
