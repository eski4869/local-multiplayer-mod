using System;
using System.IO;
using System.Reflection;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// This mod's own log file, beside its settings. Everything this mod has to
    /// say goes here and nothing goes anywhere else.
    ///
    /// Deliberately not <c>Program.crashLog</c>. That file is the game's, shared
    /// with every other mod, and is what a player is asked to hand over after a
    /// crash. Filling it with a line a second of somebody else's diagnostics
    /// buries whatever the crash actually was, and hands over a measurement taken
    /// while another person was in the session.
    ///
    /// The file keeps the name it had when only the session summary lived here.
    /// Renaming it mid-investigation would cost more than the narrower name does.
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
