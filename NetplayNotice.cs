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

        /// <summary>
        /// Whether this is the same thing being said again too soon.
        /// </summary>
        /// <remarks>
        /// Some of these are raised from a decision taken every frame, and the
        /// decisions that raise them are exactly the ones that persist - a
        /// correction that cannot reach back far enough will fail again next frame
        /// for the same reason. A real session logged "desynchronised - the
        /// correction arrived too late" a hundred and ninety six times and
        /// "correction skipped" seventy three.
        ///
        /// The crash log is unbuffered, so each of those is a write to disk, at
        /// sixty a second, starting exactly when the machine is already missing its
        /// frame budget. **The reporting was making the fault it reported worse**,
        /// and a player reading the same line sixty times learns nothing they did
        /// not learn from the first.
        ///
        /// By content rather than by call site, because the same condition reported
        /// from two places is still the same news.
        /// </remarks>
        private static bool IsRepeat(string message)
        {
            int now = Environment.TickCount;

            if (message == _lastMessage &&
                unchecked(now - _lastShownAt) < RepeatSilenceMilliseconds)
            {
                return true;
            }

            _lastMessage = message;
            _lastShownAt = now;
            return false;
        }

        private static string _lastMessage;
        private static int _lastShownAt;

        /// <summary>
        /// Two seconds. Long enough that a persistent condition reports at a rate
        /// somebody can read, short enough that a recurring one still looks
        /// recurring.
        /// </summary>
        private const int RepeatSilenceMilliseconds = 2000;

        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (IsRepeat(message))
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
