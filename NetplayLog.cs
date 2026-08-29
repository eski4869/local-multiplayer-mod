using System;
using System.Collections.Generic;
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
        /// How many lines to hold before touching the disk.
        ///
        /// The point is that no gameplay frame opens a file. A line a second and
        /// a buffer of this size is a write every half a minute, which is nowhere
        /// near a frame; the cost of a crash losing what is still in memory is
        /// the price, and it is the right way round - a measurement that changes
        /// what it measures is worth less than one that is occasionally missing
        /// its last few lines.
        /// </summary>
        private const int BufferedLines = 32;

        private static readonly List<string> Pending = new List<string>();

        /// <summary>
        /// Takes one line. Writes nothing until there are enough of them, so the
        /// call is a string concatenation and a list add and never a file.
        /// </summary>
        public static void Write(string line)
        {
            lock (Pending)
            {
                Pending.Add(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line
                );

                if (Pending.Count < BufferedLines)
                {
                    return;
                }
            }

            Flush();
        }

        /// <summary>
        /// Puts what is held onto the disk. Called where a pause is already
        /// happening - a session ending, a level ending - so the one write that
        /// does happen lands somewhere nobody is jumping.
        ///
        /// Silent on failure by design. There is no second place to report to,
        /// the crash log being the one this exists to stay out of.
        /// </summary>
        public static void Flush()
        {
            string[] lines;

            lock (Pending)
            {
                if (Pending.Count == 0)
                {
                    return;
                }

                lines = Pending.ToArray();
                Pending.Clear();
            }

            try
            {
                File.AppendAllLines(FilePath(), lines);
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
