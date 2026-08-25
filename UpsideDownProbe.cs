using JumpKing;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Logs what the upside-down resync produced, right after it runs, so a
    /// broken fix and a fix that never installed can be told apart. Temporary.
    /// </summary>
    internal static class UpsideDownProbe
    {
        /// <summary>
        /// Per player, because the players are sampled in turn: a single previous
        /// line alternates between them and therefore never repeats, so the
        /// "only on change" guard never held and this wrote every frame. It grew
        /// the crash log past 800 MB in one session before that was noticed.
        /// </summary>
        private static readonly string[] Last = new string[5];

        public static void Sample(int playerNumber)
        {
            if (!ModEntry.Diagnostics.UpsideDown || !GimmickStateCompat.IsActive ||
                playerNumber < 1 || playerNumber >= Last.Length)
            {
                return;
            }

            string line = GimmickStateCompat.DescribeUpsideDownState();
            if (line == Last[playerNumber])
            {
                return;
            }

            Last[playerNumber] = line;
            Program.crashLog.AddErrorMessage(
                "Local Multiplayer upside-down: player " + playerNumber + " " + line
            );
        }
    }
}
