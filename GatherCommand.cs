namespace LocalMultiplayerMod
{
    /// <summary>
    /// Parses the broker's gather command: <c>pX-pY</c> means "move player X onto
    /// player Y".
    ///
    /// The separator is a plain hyphen rather than an arrow so the command
    /// survives a query string untouched. <c>-&gt;</c> reads better but would have
    /// to be percent-encoded by every sender, and the senders are hand-written
    /// URLs in a streaming tool.
    ///
    /// Kept apart from the rest of the command handling because it is pure text,
    /// with no game types in it - which is what lets the test project link it.
    /// </summary>
    internal static class GatherCommand
    {
        private const int Length = 5;
        private const char LowestPlayer = '1';
        private const char HighestPlayer = '4';

        /// <summary>
        /// Expects <paramref name="command" /> already trimmed and lowercased, as
        /// the broker handler does for every command before dispatching.
        ///
        /// Rejects a player gathering onto itself. That is a no-op rather than an
        /// error, but rejecting it here keeps the caller from having to know it,
        /// and leaves one place that decides what a valid gather is.
        /// </summary>
        public static bool TryParse(
            string command,
            out int moverNumber,
            out int targetNumber
        )
        {
            moverNumber = 0;
            targetNumber = 0;

            if (command == null || command.Length != Length ||
                command[0] != 'p' || !IsPlayerDigit(command[1]) ||
                command[2] != '-' ||
                command[3] != 'p' || !IsPlayerDigit(command[4]))
            {
                return false;
            }

            int mover = command[1] - '0';
            int target = command[4] - '0';
            if (mover == target)
            {
                return false;
            }

            moverNumber = mover;
            targetNumber = target;
            return true;
        }

        private static bool IsPlayerDigit(char c)
        {
            return c >= LowestPlayer && c <= HighestPlayer;
        }
    }
}
