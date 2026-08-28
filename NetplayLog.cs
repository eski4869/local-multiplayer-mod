using System;
using System.IO;
using System.Reflection;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// This mod's own log file, beside its settings.
    ///
    /// Deliberately not <c>Program.crashLog</c>. That file is the game's, shared
    /// with every other mod, and is what a player is asked to hand over after a
    /// crash - which is the wrong place to put a measurement taken while somebody
    /// else was in the session. This one is the mod's own and holds one line per
    /// session.
    ///
    /// Nothing here sits on a per-frame path. An unbuffered write once per frame
    /// is what made an earlier report feed the fault it was reporting; a write
    /// when a session ends cannot.
    /// </summary>
    internal static class NetplayLog
    {
        private const string FileName =
            "eski4869.LocalMultiplayerMod.NetplayLog.txt";

        private static string _path;

        /// <summary>
        /// Appends one line.
        ///
        /// Silent on failure by design. There is no second place to report to -
        /// the crash log is the one this exists to stay out of - and a measurement
        /// that interrupts what it measures is worse than a missing one.
        /// </summary>
        public static void Write(string line)
        {
            try
            {
                File.AppendAllText(
                    FilePath(),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line +
                        Environment.NewLine
                );
            }
            catch
            {
            }
        }

        private static string FilePath()
        {
            if (_path == null)
            {
                _path = Path.Combine(
                    Path.GetDirectoryName(
                        Assembly.GetExecutingAssembly().Location
                    ),
                    FileName
                );
            }

            return _path;
        }
    }
}
